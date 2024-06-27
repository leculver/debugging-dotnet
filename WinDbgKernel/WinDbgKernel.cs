using Microsoft.DotNet.Interactive;
using Microsoft.DotNet.Interactive.Commands;
using System.CommandLine;
using DbgX;
using DbgX.Requests.Initialization;
using DbgX.Requests;
using System.Runtime.InteropServices;

namespace WinDbgKernel
{
    public class WinDbgKernel : Kernel, IKernelCommandHandler<SubmitCode>
    {
        const string LoadDumpCommand = "#!loadDump";
        const string SymPathCommand = "#!sympath";
        const string DbgEngPathCommand = "#!dbgengPath";
        private string? _sympath;

        public WinDbgKernel() : base("windbg")
        {
            RegisterSymPathCommand();
            RegisterLoadDumpCommand();
            RegisterWindbgPathCommand();

            _sympath = Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH");
            if (string.IsNullOrWhiteSpace(_sympath))
                _sympath = null;
        }

        private void RegisterSymPathCommand()
        {
            var path = new Argument<string>("path", "Path to the WinDbg executable");
            var setSymPathCommand = new Command(SymPathCommand) { path };
            setSymPathCommand.SetHandler(SetSymPath, path);
            AddDirective(setSymPathCommand);
        }

        private void RegisterLoadDumpCommand()
        {
            var dumpFile = new Argument<string>("dumpFile", "Path to the dump file");
            var loadDumpCommand = new Command(LoadDumpCommand) { dumpFile };
            loadDumpCommand.SetHandler(LoadDump, dumpFile);
            AddDirective(loadDumpCommand);
        }

        private void RegisterWindbgPathCommand()
        {
            var path = new Argument<string>("path", "Path to the WinDbg executable");
            var setDbgEngPathCommand = new Command(DbgEngPathCommand) { path };
            setDbgEngPathCommand.SetHandler(SetDbgEngPath, path);
            AddDirective(setDbgEngPathCommand);
        }

        public async Task HandleAsync(SubmitCode command, KernelInvocationContext context)
        {
            foreach (var line in command.Code.Split('\n').Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()))
                await RunOneCommand(line, context);
        }

        private static async Task RunOneCommand(string line, KernelInvocationContext context)
        {
            await WinDbgThread.Queue(async () =>
            {
                DebugEngine engine = await WinDbgThread.GetDebugEngine().ConfigureAwait(true);
                using var output = WinDbgThread.CaptureOutput();
                output.OutputReceived += (str) => context.DisplayStandardOut(str);
                bool res = await engine.SendRequestAsync(new ExecuteRequest(line)).ConfigureAwait(true);

                WriteErrorsAndSymbols(context, output);
            });
        }

        private static void WriteErrorsAndSymbols(KernelInvocationContext context, WinDbgThread.DebuggerOutput output)
        {
            string symbols = output.Symbols;
            if (!string.IsNullOrWhiteSpace(symbols))
                context.DisplayStandardOut("Symbol requests:\n" + symbols);

            string errors = output.Errors;
            string warnings = output.Warnings;

            if (!string.IsNullOrWhiteSpace(errors))
                context.DisplayStandardError(errors);

            if (!string.IsNullOrWhiteSpace(warnings))
                context.DisplayStandardError(warnings);
        }

        public async Task LoadDump(string dumpFile)
        {
            await WinDbgThread.Queue(async () =>
            {
                DebugEngine engine = await WinDbgThread.GetDebugEngine().ConfigureAwait(true);
                using var output = WinDbgThread.CaptureOutput();

                bool res = await engine.SendRequestAsync(new OpenDumpFileRequest(dumpFile, new())).ConfigureAwait(true);
                if (res)
                    Console.WriteLine($"Dump file '{dumpFile}' loaded successfully.");
                else
                    Console.WriteLine($"Failed to load dump file '{dumpFile}'.");

                if (_sympath is not null)
                {
                    res = await engine.SendRequestAsync(new SetSymbolPathRequest(_sympath)).ConfigureAwait(true);
                    if (res)
                        Console.WriteLine($"Symbol path '{_sympath}' set successfully.");
                    else
                        Console.WriteLine($"Failed to set symbol path '{_sympath}'.");
                }
            });
        }

        private Task SetSymPath(string path)
        {
            _sympath = path;
            return Task.CompletedTask;
        }

        private static Task SetDbgEngPath(string path)
        {
            WinDbgThread.SetDbgEngPath(path);
            return Task.CompletedTask;
        }
    }
}
