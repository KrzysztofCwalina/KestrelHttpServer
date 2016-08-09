// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Internal.Http;
using Microsoft.AspNetCore.Server.Kestrel.Internal.Infrastructure;
using Microsoft.AspNetCore.Server.Kestrel.Internal.Networking;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Server.Kestrel.Internal
{
    /// <summary>
    /// Summary description for KestrelThread
    /// </summary>
    public class KestrelThread
    {
        // maximum times the work queues swapped and are processed in a single pass
        // as completing a task may immediately have write data to put on the network
        // otherwise it needs to wait till the next pass of the libuv loop
        private const int _maxLoops = 8;

        private static readonly Action<object, object> _postCallbackAdapter = (callback, state) => ((Action<object>)callback).Invoke(state);
        private static readonly Action<object, object> _postAsyncCallbackAdapter = (callback, state) => ((Action<object>)callback).Invoke(state);

        private readonly KestrelEngine _engine;
        private readonly IApplicationLifetime _appLifetime;
        private readonly Thread _thread;
        private readonly TaskCompletionSource<object> _threadTcs = new TaskCompletionSource<object>();
        private readonly UvLoopHandle _loop;
        private readonly UvAsyncHandle _post;
        private ConcurrentQueue<Work> _workAdding = new ConcurrentQueue<Work>();
        private ConcurrentQueue<Work> _workRunning = new ConcurrentQueue<Work>();
        private Queue<CloseHandle> _closeHandleAdding = new Queue<CloseHandle>(256);
        private Queue<CloseHandle> _closeHandleRunning = new Queue<CloseHandle>(256);
        private readonly object _workSync = new object();
        private readonly object _startSync = new object();
        private bool _stopImmediate = false;
        private bool _initCompleted = false;
        private ExceptionDispatchInfo _closeError;
        private readonly IKestrelTrace _log;
        private readonly IThreadPool _threadPool;
        private readonly TimeSpan _shutdownTimeout;
        private int _posted;

        public KestrelThread(KestrelEngine engine)
        {
            _engine = engine;
            _appLifetime = engine.AppLifetime;
            _log = engine.Log;
            _threadPool = engine.ThreadPool;
            _shutdownTimeout = engine.ServerOptions.ShutdownTimeout;
            _loop = new UvLoopHandle(_log);
            _post = new UvAsyncHandle(_log);
            _thread = new Thread(ThreadStart);
            _thread.Name = "KestrelThread - libuv";
#if !DEBUG
            // Mark the thread as being as unimportant to keeping the process alive.
            // Don't do this for debug builds, so we know if the thread isn't terminating.
            _thread.IsBackground = true;
#endif
            QueueCloseHandle = PostCloseHandle;
            QueueCloseAsyncHandle = EnqueueCloseHandle;
            Memory = new MemoryPool();
            WriteReqPool = new WriteReqPool(this, _log);
            ConnectionManager = new ConnectionManager(this, _threadPool);
        }

        public UvLoopHandle Loop { get { return _loop; } }

        public MemoryPool Memory { get; }

        public ConnectionManager ConnectionManager { get; }

        public WriteReqPool WriteReqPool { get; }

        public ExceptionDispatchInfo FatalError { get { return _closeError; } }

        public Action<Action<IntPtr>, IntPtr> QueueCloseHandle { get; }

        private Action<Action<IntPtr>, IntPtr> QueueCloseAsyncHandle { get; }

        public Task StartAsync()
        {
            var tcs = new TaskCompletionSource<int>();
            _thread.Start(tcs);
            return tcs.Task;
        }

        public async Task StopAsync(TimeSpan timeout)
        {
            lock (_startSync)
            {
                if (!_initCompleted)
                {
                    return;
                }
            }

            if (!_threadTcs.Task.IsCompleted)
            {
                // These operations need to run on the libuv thread so it only makes
                // sense to attempt execution if it's still running
                await DisposeConnectionsAsync().ConfigureAwait(false);

                var stepTimeout = TimeSpan.FromTicks(timeout.Ticks / 3);

                Post(t => t.AllowStop());
                if (!await WaitAsync(_threadTcs.Task, stepTimeout).ConfigureAwait(false))
                {
                    try
                    {
                        Post(t => t.OnStopRude());
                        if (!await WaitAsync(_threadTcs.Task, stepTimeout).ConfigureAwait(false))
                        {
                            Post(t => t.OnStopImmediate());
                            if (!await WaitAsync(_threadTcs.Task, stepTimeout).ConfigureAwait(false))
                            {
                                _log.LogError(0, null, "KestrelThread.StopAsync failed to terminate libuv thread.");
                            }
                        }
                    }
                    catch (ObjectDisposedException)
                    {
                        // REVIEW: Should we log something here?
                        // Until we rework this logic, ODEs are bound to happen sometimes.
                        if (!await WaitAsync(_threadTcs.Task, stepTimeout).ConfigureAwait(false))
                        {
                            _log.LogError(0, null, "KestrelThread.StopAsync failed to terminate libuv thread.");
                        }
                    }
                }
            }

            if (_closeError != null)
            {
                _closeError.Throw();
            }
        }

        private async Task DisposeConnectionsAsync()
        {
            try
            {
                // Close and wait for all connections
                if (!await ConnectionManager.WalkConnectionsAndCloseAsync(_shutdownTimeout).ConfigureAwait(false))
                {
                    _log.NotAllConnectionsClosedGracefully();
                }

                var result = await WaitAsync(PostAsync(state =>
                {
                    var listener = (KestrelThread)state;
                    listener.WriteReqPool.Dispose();
                },
                this), _shutdownTimeout).ConfigureAwait(false);

                if (!result)
                {
                    _log.LogError(0, null, "Disposing write requests failed");
                }
            }
            finally
            {
                Memory.Dispose();
            }
        }


        private void AllowStop()
        {
            _post.Unreference();
        }

        private void OnStopRude()
        {
            Walk(ptr =>
            {
                var handle = UvMemory.FromIntPtr<UvHandle>(ptr);
                if (handle != _post)
                {
                    // handle can be null because UvMemory.FromIntPtr looks up a weak reference
                    handle?.Dispose();
                }
            });

            // uv_unref is idempotent so it's OK to call this here and in AllowStop.
            _post.Unreference();
        }

        private void OnStopImmediate()
        {
            _stopImmediate = true;
            _loop.Stop();
        }

        public void Post(Action<object> callback, object state)
        {
            _workAdding.Enqueue(new Work
            {
                CallbackAdapter = _postCallbackAdapter,
                Callback = callback,
                State = state
            });
            if (Interlocked.CompareExchange(ref _posted, 0, 1) == 0)
            {
                _post.Send();
            }
        }

        private void Post(Action<KestrelThread> callback)
        {
            Post(thread => callback((KestrelThread)thread), this);
        }

        public Task PostAsync(Action<object> callback, object state)
        {
            var tcs = new TaskCompletionSource<object>();
            _workAdding.Enqueue(new Work
            {
                CallbackAdapter = _postAsyncCallbackAdapter,
                Callback = callback,
                State = state,
                Completion = tcs
            });
            if (Interlocked.CompareExchange(ref _posted, 0, 1) == 0)
            {
                _post.Send();
            }
            return tcs.Task;
        }

        public void Walk(Action<IntPtr> callback)
        {
            _engine.Libuv.walk(
                _loop,
                (ptr, arg) =>
                {
                    callback(ptr);
                },
                IntPtr.Zero);
        }

        private void PostCloseHandle(Action<IntPtr> callback, IntPtr handle)
        {
            EnqueueCloseHandle(callback, handle);
            _post.Send();
        }

        private void EnqueueCloseHandle(Action<IntPtr> callback, IntPtr handle)
        {
            lock (_workSync)
            {
                _closeHandleAdding.Enqueue(new CloseHandle { Callback = callback, Handle = handle });
            }
        }

        private void ThreadStart(object parameter)
        {
            lock (_startSync)
            {
                var tcs = (TaskCompletionSource<int>) parameter;
                try
                {
                    _loop.Init(_engine.Libuv);
                    _post.Init(_loop, OnPost, EnqueueCloseHandle);
                    _initCompleted = true;
                    tcs.SetResult(0);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                    return;
                }
            }

            try
            {
                var ran1 = _loop.Run();
                if (_stopImmediate)
                {
                    // thread-abort form of exit, resources will be leaked
                    return;
                }

                // run the loop one more time to delete the open handles
                _post.Reference();
                _post.Dispose();

                // Ensure the Dispose operations complete in the event loop.
                var ran2 = _loop.Run();

                _loop.Dispose();
            }
            catch (Exception ex)
            {
                _closeError = ExceptionDispatchInfo.Capture(ex);
                // Request shutdown so we can rethrow this exception
                // in Stop which should be observable.
                _appLifetime.StopApplication();
            }
            finally
            {
                _threadTcs.SetResult(null);
            }
        }

        private void OnPost()
        {
            Interlocked.CompareExchange(ref _posted, 1, 0);
            var loopsRemaining = _maxLoops;
            bool wasWork;
            do
            {
                wasWork = DoPostWork();
                wasWork = DoPostCloseHandle() || wasWork;
                loopsRemaining--;
            } while (wasWork && loopsRemaining > 0);
        }

        private bool DoPostWork()
        {
            _workRunning = Interlocked.Exchange(ref _workAdding, _workRunning);

            bool wasWork = false;

            Work work;
            while (_workRunning.TryDequeue(out work))
            {
                wasWork = true;
                try
                {
                    work.CallbackAdapter(work.Callback, work.State);
                    if (work.Completion != null)
                    {
                        _threadPool.Complete(work.Completion);
                    }
                }
                catch (Exception ex)
                {
                    if (work.Completion != null)
                    {
                        _threadPool.Error(work.Completion, ex);
                    }
                    else
                    {
                        _log.LogError(0, ex, "KestrelThread.DoPostWork");
                        throw;
                    }
                }
            }

            return wasWork;
        }

        private bool DoPostCloseHandle()
        {
            Queue<CloseHandle> queue;
            lock (_workSync)
            {
                queue = _closeHandleAdding;
                _closeHandleAdding = _closeHandleRunning;
                _closeHandleRunning = queue;
            }

            bool wasWork = queue.Count > 0;

            while (queue.Count != 0)
            {
                var closeHandle = queue.Dequeue();
                try
                {
                    closeHandle.Callback(closeHandle.Handle);
                }
                catch (Exception ex)
                {
                    _log.LogError(0, ex, "KestrelThread.DoPostCloseHandle");
                    throw;
                }
            }

            return wasWork;
        }

        private static async Task<bool> WaitAsync(Task task, TimeSpan timeout)
        {
            return await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false) == task;
        }

        private struct Work
        {
            public Action<object, object> CallbackAdapter;
            public object Callback;
            public object State;
            public TaskCompletionSource<object> Completion;
        }

        private struct CloseHandle
        {
            public Action<IntPtr> Callback;
            public IntPtr Handle;
        }
    }
}