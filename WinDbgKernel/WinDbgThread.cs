
using DbgX;
using DbgX.Requests;
using Microsoft.Extensions.ObjectPool;
using System.Collections.Concurrent;
using System.Diagnostics;
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

        public class DebuggerOutput : IDisposable
        {
            private readonly Dictionary<DEBUG_OUTPUT, StringBuilder> _buffers = [];

            public event Action<string>? OutputReceived;

            public DebuggerOutput()
            {
                _output = this;
            }

            public void AddOutput(DEBUG_OUTPUT mask, string output)
            {
                if (mask == DEBUG_OUTPUT.PROMPT)
                {
                    output = output.Replace("&lt;", "<").Replace("&gt;", ">");
                    AddOutput(DEBUG_OUTPUT.NORMAL, output);
                }

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

        public static DebuggerOutput CaptureOutput() => new DebuggerOutput();

        static WinDbgThread()
        {
            _syncContext = new DebuggerSynchronizationContext(_workQueue);
            _scheduler = new DebuggerTaskScheduler(_syncContext);

            _debuggerThread = new Thread(ThreadProc) { IsBackground = true, Name = "Debug Scheduler" };
            _debuggerThread.SetApartmentState(ApartmentState.STA);
            _debuggerThread.Start();
        }

        public static Task<DebugEngine> GetDebugEngine() => _engineTcs.Task;

        private static void ThreadProc()
        {
            SynchronizationContext.SetSynchronizationContext(_syncContext);

            // Initialize the DebugEngine and set the TaskCompletionSource
            var engine = new DebugEngine(null, null, null, null, false, null, _syncContext);
            engine.SendRequestAsync(new ExecuteRequest(".prefer_dml 0"));
            engine.DmlOutput += RecieveOutput;
            _engineTcs.SetResult(engine);

            // Pump the queue of work items
            foreach (var workItem in _workQueue.GetConsumingEnumerable())
            {
                workItem();
            }
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

        private class DebuggerSynchronizationContext : SynchronizationContext
        {
            private readonly BlockingCollection<Action> _workQueue;

            public DebuggerSynchronizationContext(BlockingCollection<Action> workQueue)
            {
                _workQueue = workQueue;
            }

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

        private class DebuggerTaskScheduler : TaskScheduler
        {
            private readonly SynchronizationContext _syncContext;

            public DebuggerTaskScheduler(SynchronizationContext syncContext)
            {
                _syncContext = syncContext;
            }

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
    }
}