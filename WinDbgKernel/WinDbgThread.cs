
using DbgX;
using DbgX.Requests;
using System.Collections.Concurrent;
using System.Text;

namespace WinDbgKernel
{
    public static class WinDbgThread
    {
        private static readonly Thread _debuggerThread;
        private static readonly SynchronizationContext _syncContext;
        private static readonly BlockingCollection<Action> _workQueue = new BlockingCollection<Action>();
        private static readonly DebuggerTaskScheduler _scheduler;
        private static readonly TaskCompletionSource<DebugEngine> _engineTcs = new TaskCompletionSource<DebugEngine>();
        private static readonly StringBuilder _output = new();

        static WinDbgThread()
        {
            _syncContext = new DebuggerSynchronizationContext(_workQueue);
            _scheduler = new DebuggerTaskScheduler(_syncContext);

            _debuggerThread = new Thread(ThreadProc) { IsBackground = true, Name = "Debug Scheduler" };
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
            Console.WriteLine($"{e.Mask} - {e.Output}");
            lock (_output)
                _output.Append(e.Output);
        }

        public static Task Queue(Action workItem)
        {
            TaskCompletionSource tcs = new();
            _workQueue.Add(() =>
            {
                workItem();
                tcs.SetResult();

                lock (_output)
                    _output.Clear();
            });
            return tcs.Task;
        }

        public static Task<string> QueueWithOutput(Action workItem)
        {
            TaskCompletionSource<string> tcs = new();
            _workQueue.Add(() =>
            {
                lock (_output)
                    _output.Clear();

                workItem();

                lock (_output)
                {
                    tcs.SetResult(_output.ToString());
                    _output.Clear();
                }
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
                return null;
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