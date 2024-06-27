using Microsoft.Diagnostics.Runtime.Utilities.DbgEng;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text;

namespace WinDbgKernel
{
    internal class DbgEngWrapper : IDisposable, IDebugOutputCallbacks
    {
        private readonly IDisposable _dbgeng;
        public IDebugClient IDebugClient { get; }
        public IDebugControl IDebugControl { get; }

        private StringBuilder _output = new();
        private static readonly BlockingCollection<Action> _workQueue = [];
        private readonly Thread _thread;

        public DbgEngWrapper()
        {
            _thread = new Thread(ThreadProc) { IsBackground = true, Name = "Debug Scheduler" };
            _thread.Start();

            string path = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!, "amd64");

            _dbgeng = IDebugClient.Create(path);
            IDebugClient = (IDebugClient)_dbgeng;
            IDebugControl = (IDebugControl)_dbgeng;

            IDebugClient.SetOutputCallbacks(this);

        }

        private void ThreadProc()
        {
            foreach (var workItem in _workQueue.GetConsumingEnumerable())
                workItem();
        }

        public void Dispose()
        {
            _dbgeng.Dispose();
            _workQueue.CompleteAdding();
        }

        public static DbgEngWrapper LoadDump(string dumpFile)
        {
            DbgEngWrapper wrapper = new();
            if (wrapper.IDebugClient.OpenDumpFile(dumpFile) < 0 || wrapper.IDebugControl.WaitForEvent(TimeSpan.FromMinutes(1)) < 0)
            {
                wrapper.Dispose();
                throw new InvalidOperationException("Failed to open dump file.");
            }

            return wrapper;
        }

        public Task<string> Execute(string command)
        {
            TaskCompletionSource<string> tcs = new();

            _workQueue.Add(() =>
            {
                lock (_output)
                    _output.Clear();
                
                IDebugControl.Execute(DEBUG_OUTCTL.THIS_CLIENT, command, DEBUG_EXECUTE.DEFAULT);
                
                lock (_output)
                {
                    tcs.SetResult(_output.ToString());
                    _output.Clear();
                }
            });

            return tcs.Task;
        }

        public void OnText(DEBUG_OUTPUT flags, string? text, ulong args)
        {
            lock(_output)
                _output.Append(text);
        }
    }
}
