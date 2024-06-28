
using DbgX;
using DbgX.Interfaces.Services.Internal;
using Microsoft.Extensions.ObjectPool;
using System.Collections.Concurrent;
using System.Text;
using WindowsDebugger.DbgEng;

namespace WinDbgKernel
{
    public static class WinDbgThread
    {
        private static readonly Thread _debuggerThread;
        private static readonly SynchronizationContext _syncContext;
        private static readonly BlockingCollection<Action> _workQueue = new BlockingCollection<Action>();
        private static readonly DebuggerTaskScheduler _scheduler;
        private static readonly TaskCompletionSource<DebugEngine> _engineTcs = new TaskCompletionSource<DebugEngine>();
        private static readonly ObjectPool<StringBuilder> _builderPool = new DefaultObjectPoolProvider().CreateStringBuilderPool();
        private static DebuggerOutput? _output;
        private static DbgEngPath? _dbgengPath;
        public static DebuggerOutput CaptureOutput() => new();

        static WinDbgThread()
        {
            _syncContext = new DebuggerSynchronizationContext(_workQueue);
            _scheduler = new DebuggerTaskScheduler(_syncContext);

            _debuggerThread = new Thread(ThreadProc) { IsBackground = false, Name = "DbgEng Controller Thread" };
            _debuggerThread.Start();
        }
        
        public static void SetDbgEngPath(string path)
        {
            _dbgengPath = string.IsNullOrWhiteSpace(path) ? null : new(path);
        }

        public static Task<DebugEngine> GetDebugEngine() => _engineTcs.Task;

        private static void ThreadProc()
        {
            SynchronizationContext.SetSynchronizationContext(_syncContext);

            var engine = new DebugEngine(_dbgengPath, null, null, null, false, null, _syncContext);
            engine.DmlOutput += RecieveOutput;
            _engineTcs.SetResult(engine);

            foreach (var workItem in _workQueue.GetConsumingEnumerable())
                workItem();
        }

        private static void RecieveOutput(object? sender, OutputEventArgs e)
        {
            _output?.AddOutput(e.Mask, e.Output);
        }

        public static Task Queue(Func<Task> workItem)
        {
            TaskCompletionSource tcs = new();
            _workQueue.Add(async () =>
            {
                await workItem().ConfigureAwait(true);
                tcs.SetResult();
            });
            return tcs.Task;
        }

        public static Task<T> Queue<T>(Func<Task<T>> workItem)
        {
            TaskCompletionSource<T> tcs = new();
            _workQueue.Add(async () =>
            {
                T t = await workItem().ConfigureAwait(true);
                tcs.SetResult(t);
            });

            return tcs.Task;
        }

        private class DebuggerSynchronizationContext(BlockingCollection<Action> workQueue) : SynchronizationContext
        {
            private readonly BlockingCollection<Action> _workQueue = workQueue;

            public override void Post(SendOrPostCallback d, object? state)
            {
                _workQueue.Add(() => d(state));
            }

            public override void Send(SendOrPostCallback d, object? state)
            {
                var done = new ManualResetEvent(false);
                _workQueue.Add(() =>
                {
                    d(state);
                    done.Set();
                });
                done.WaitOne();
            }
        }

        private class DebuggerTaskScheduler(SynchronizationContext syncContext) : TaskScheduler
        {
            private readonly SynchronizationContext _syncContext = syncContext;

            protected override IEnumerable<Task> GetScheduledTasks()
            {
                return [];
            }

            protected override void QueueTask(Task task)
            {
                _syncContext.Post(_ => TryExecuteTask(task), null);
            }

            protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
            {
                if (SynchronizationContext.Current == _syncContext)
                {
                    return TryExecuteTask(task);
                }
                return false;
            }
        }

        class DbgEngPath(string path) : IDbgEnginePathCustomization
        {
            public string HomeDirectory { get; } = path;

            public string GetEngHostPath(string architecture)
            {
                return Path.Combine(HomeDirectory, "EngHost.exe");
            }

            public string GetEnginePath(string architecture)
            {
                return HomeDirectory;
            }
        }

        public sealed class DebuggerOutput : IDisposable
        {
            private readonly Dictionary<DEBUG_OUTPUT, StringBuilder> _buffers = [];

            public event Action<string>? OutputReceived;

            public DebuggerOutput()
            {
                _output = this;
            }

            public void AddOutput(DEBUG_OUTPUT mask, string output)
            {
                output = output.Replace("&lt;", "<").Replace("&gt;", ">");
                if (mask == DEBUG_OUTPUT.PROMPT)
                    AddOutput(DEBUG_OUTPUT.NORMAL, output);

                lock (_buffers)
                {
                    if (!_buffers.TryGetValue(mask, out StringBuilder? buffer))
                        _buffers[mask] = buffer = _builderPool.Get();

                    buffer.Append(output);

                    if (mask == DEBUG_OUTPUT.NORMAL)
                        OutputReceived?.Invoke(output);
                }
            }

            public void Dispose()
            {
                foreach (var buffer in _buffers.Values)
                    _builderPool.Return(buffer);
                _buffers.Clear();
                _output = null;
            }
            
            public string GetOutput(DEBUG_OUTPUT mask)
            {
                lock (_buffers)
                {
                    if (_buffers.TryGetValue(mask, out StringBuilder? buffer))
                        return buffer.ToString();
                }

                return "";
            }

            public string Output => GetOutput(DEBUG_OUTPUT.NORMAL);
            public string Errors => GetOutput(DEBUG_OUTPUT.ERROR);
            public string Warnings => GetOutput(DEBUG_OUTPUT.WARNING);
            public string Symbols => GetOutput(DEBUG_OUTPUT.SYMBOLS);
        }
    }
}
