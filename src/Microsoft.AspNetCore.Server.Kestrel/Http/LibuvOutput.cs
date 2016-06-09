﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Server.Kestrel.Infrastructure;
using Microsoft.AspNetCore.Server.Kestrel.Networking;

namespace Microsoft.AspNetCore.Server.Kestrel.Http
{
    public class LibuvOutput
    {
        public const int MaxPooledWriteReqs = 1024;

        public LibuvOutput(
            LibuvThread libuvThread,
            UvStreamHandle socket,
            MemoryPoolChannel outputChannel,
            LibuvConnection connection,
            IKestrelTrace log,
            IThreadPool threadPool,
            Queue<UvWriteReq> writeReqPool)
        {
            LibuvThread = libuvThread;
            Socket = socket;
            OutputChannel = outputChannel;
            Connection = connection;
            Log = log;
            ThreadPool = threadPool;
            WriteReqPool = writeReqPool;
        }

        public IThreadPool ThreadPool { get; }

        public IKestrelTrace Log { get; }

        public MemoryPoolChannel OutputChannel { get; }

        public UvStreamHandle Socket { get; }

        public LibuvThread LibuvThread { get; }

        public LibuvConnection Connection { get; }

        public Queue<UvWriteReq> WriteReqPool { get; }

        public async void Start()
        {
            // Reuse the awaiter
            var awaitable = new LibuvAwaitable<UvWriteReq>();

            try
            {
                while (true)
                {
                    await OutputChannel;

                    // Switch to the UV thread
                    await LibuvThread;

                    var start = OutputChannel.BeginRead();
                    var end = OutputChannel.End();

                    int bytes;
                    int buffers;
                    BytesBetween(start, end, out bytes, out buffers);


                    var req = TakeWriteReq();

                    try
                    {
                        req.Write(Socket, start, end, buffers, LibuvAwaitable<UvWriteReq>.Callback, awaitable);
                        int status = await awaitable;
                        Log.ConnectionWriteCallback(Connection.ConnectionId, status);
                    }
                    catch (Exception ex)
                    {
                        // Abort the connection for any failed write
                        // Queued on threadpool so get it in as first op.
                        Connection.Abort();

                        Log.ConnectionError(Connection.ConnectionId, ex);
                    }
                    finally
                    {
                        OutputChannel.EndRead(end);

                        // Return the request to the pool
                        ReturnWriteRequest(req);
                    }

                    if (Socket.IsClosed)
                    {
                        break;
                    }
                }
            }
            catch (TaskCanceledException)
            {
                await LibuvThread;

                try
                {
                    if (Socket.IsClosed)
                    {
                        return;
                    }

                    var shutdownAwaitable = new LibuvAwaitable<UvShutdownReq>();
                    using (var shutdownReq = new UvShutdownReq(Log))
                    {
                        shutdownReq.Init(LibuvThread.Loop);
                        shutdownReq.Shutdown(Socket, LibuvAwaitable<UvShutdownReq>.Callback, shutdownAwaitable);
                        int status = await shutdownAwaitable;

                        Log.ConnectionWroteFin(Connection.ConnectionId, status);
                    }

                }
                catch (Exception)
                {
                    // TODO: Log
                }
            }
            finally
            {
                Socket.Dispose();
                Connection.OnSocketClosed();
                OutputChannel.Dispose();

                Log.ConnectionStop(Connection.ConnectionId);
            }
        }

        private UvWriteReq TakeWriteReq()
        {
            UvWriteReq req;

            if (WriteReqPool.Count > 0)
            {
                req = WriteReqPool.Dequeue();
            }
            else
            {
                req = new UvWriteReq(Log);
                req.Init(LibuvThread.Loop);
            }

            return req;
        }

        private void ReturnWriteRequest(UvWriteReq req)
        {
            if (WriteReqPool.Count < MaxPooledWriteReqs)
            {
                WriteReqPool.Enqueue(req);
            }
            else
            {
                req.Dispose();
            }
        }

        public void Stop()
        {
            OutputChannel.Cancel();
        }

        private static void BytesBetween(MemoryPoolIterator start, MemoryPoolIterator end, out int bytes, out int buffers)
        {
            if (start.Block == end.Block)
            {
                bytes = end.Index - start.Index;
                buffers = 1;
                return;
            }

            bytes = start.Block.Data.Offset + start.Block.Data.Count - start.Index;
            buffers = 1;

            for (var block = start.Block.Next; block != end.Block; block = block.Next)
            {
                bytes += block.Data.Count;
                buffers++;
            }

            bytes += end.Index - end.Block.Data.Offset;
            buffers++;
        }
    }
}