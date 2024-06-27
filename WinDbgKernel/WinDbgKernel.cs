using Microsoft.DotNet.Interactive;
using Microsoft.DotNet.Interactive.Commands;
using System.CommandLine;
using DbgX;
using DbgX.Requests.Initialization;
using DbgX.Interfaces.Services;
using DbgX.Requests;
using System.IO;
using Nito.AsyncEx;

namespace WinDbgKernel
{
    public class WinDbgKernel : Kernel, IKernelCommandHandler<SubmitCode>
    {
        const string LoadDumpCommand = "#!loadDump";
        const string SetSymPathCommand = "#!sympath";
        private DbgEngWrapper? _dbgeng;
        private string? _sympath;

        public WinDbgKernel() : base("windbg")
        {
            RegisterSymPathCommand();
            RegisterLoadDumpCommand();

            _sympath = Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH");
            if (string.IsNullOrWhiteSpace(_sympath))
                _sympath = null;
        }

        private void RegisterSymPathCommand()
        {
            var path = new Argument<string>("path", "Path to the WinDbg executable");
            var setSymPathCommand = new Command(SetSymPathCommand) { path };
            setSymPathCommand.SetHandler(symPath => SetSymPath(symPath), path);
            AddDirective(setSymPathCommand);
        }

        private void RegisterLoadDumpCommand()
        {
            var dumpFile = new Argument<string>("dumpFile", "Path to the dump file");
            var loadDumpCommand = new Command(LoadDumpCommand) { dumpFile };
            loadDumpCommand.SetHandler(dumpFile => LoadDump(dumpFile), dumpFile);
            AddDirective(loadDumpCommand);
        }

        public async Task HandleAsync(SubmitCode command, KernelInvocationContext context)
        {
            if (_dbgeng is null)
            {
                context.Fail(command, message: $"No engine is loaded.  Use {LoadDumpCommand} first.");
                return;
            }

            string result = await _dbgeng.Execute(command.Code);
            Console.WriteLine(result);
            Console.WriteLine("complete");
        }

        public Task LoadDump(string dumpFile)
        {
            if (_dbgeng is not null)
            {
                _dbgeng.Dispose();
                _dbgeng = null;
            }

            _dbgeng = DbgEngWrapper.LoadDump(dumpFile);
            return Task.CompletedTask;
        }

        private Task SetSymPath(string path)
        {
            _sympath = path;
            return Task.CompletedTask;
        }
    }
}
