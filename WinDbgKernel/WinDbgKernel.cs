using Microsoft.DotNet.Interactive;
using Microsoft.DotNet.Interactive.Commands;
using System.CommandLine;
using DbgX;
using DbgX.Requests.Initialization;
using DbgX.Requests;
using DbgX.Interfaces.Services;
using DbgX.Interfaces.Enums;
using Microsoft.DotNet.Interactive.ValueSharing;
using Microsoft.DotNet.Interactive.Events;

namespace WinDbgKernel
{
    public class WinDbgKernel :
                    Kernel,
                    IKernelCommandHandler<SubmitCode>,
                    IKernelCommandHandler<RequestValueInfos>,
                    IKernelCommandHandler<RequestValue>
    {
        const string SymbolOutputCommand = "#!verboseSymbols";
        const string LoadDumpCommand = "#!loadDump";
        const string SymPathCommand = "#!sympath";
        const string DbgEngPathCommand = "#!dbgengPath";
        private string? _sympath;
        private static bool _displaySymbols;
        private bool _isRunning;
        private DebugModelProcess? _curprocess;

        public WinDbgKernel() : base("windbg")
        {
            RegisterSymPathCommand();
            RegisterLoadDumpCommand();
            RegisterWindbgPathCommand();
            RegisterSymbolOutputCommand();

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

        private void RegisterSymbolOutputCommand()
        {
            var displaySymbols = new Argument<bool>("display", "Display symbol requests in the output.");
            var symbolsCommand = new Command(SymbolOutputCommand) { displaySymbols };
            symbolsCommand.SetHandler(SetSymbolStatus, displaySymbols);
            AddDirective(symbolsCommand);
        }

        public async Task HandleAsync(SubmitCode command, KernelInvocationContext context)
        {
            var commands = from line in command.Code.Split('\n')
                            let trimmed = line.Trim()
                            where !string.IsNullOrWhiteSpace(trimmed)
                            where !trimmed.StartsWith("#!") && !trimmed.StartsWith('*')
                            select new string(trimmed);

            foreach (var line in commands)
                if (!await RunOneCommand(line, context))
                    break;
        }

        private async Task<bool> RunOneCommand(string line, KernelInvocationContext context)
        {
            if (!_isRunning)
            {
                context.DisplayStandardError($"No dump file loaded. Use the '{LoadDumpCommand}' directive to load a dump file.");
                return false;
            }

            await WinDbgThread.Queue(async () =>
            {
                DebugEngine engine = await WinDbgThread.GetDebugEngine().ConfigureAwait(true);
                using var output = WinDbgThread.CaptureOutput();
                output.OutputReceived += (str) => context.DisplayStandardOut(str);
                bool res = await engine.SendRequestAsync(new ExecuteRequest(line)).ConfigureAwait(true);

                WriteErrorsAndSymbols(context, output);
            });

            return true;
        }

        private static void WriteErrorsAndSymbols(KernelInvocationContext context, WinDbgThread.DebuggerOutput output)
        {
            if (_displaySymbols)
            {
                string symbols = output.Symbols;
                if (!string.IsNullOrWhiteSpace(symbols))
                    context.DisplayStandardOut("\nSymbol requests:\n" + symbols);
            }

            string errors = output.Errors;
            string warnings = output.Warnings;

            if (!string.IsNullOrWhiteSpace(errors))
                context.DisplayStandardError(errors);

            if (!string.IsNullOrWhiteSpace(warnings))
                context.DisplayStandardError(warnings);
        }

        public async Task LoadDump(string dumpFile)
        {
            if (_isRunning)
                throw new InvalidOperationException("A dump file is already loaded.");

            await WinDbgThread.Queue(async () =>
            {
                DebugEngine engine = await WinDbgThread.GetDebugEngine().ConfigureAwait(true);
                using var output = WinDbgThread.CaptureOutput();

                EngineOptions options = new()
                {
                    SymPath = _sympath  
                };

                bool res = await engine.SendRequestAsync(new OpenDumpFileRequest(dumpFile, new())).ConfigureAwait(true);
                if (res)
                {
                    _isRunning = true;
                    Console.WriteLine($"Dump file '{dumpFile}' loaded successfully.");
                }
                else
                    Console.WriteLine($"Failed to load dump file '{dumpFile}'.");

                await engine.SendRequestAsync(new ExecuteRequest(".prefer_dml 0"));
            });
        }

        private async Task SetSymPath(string path)
        {
            _sympath = path;
            if (_isRunning)
            {
                await WinDbgThread.Queue(async () =>
                {
                    DebugEngine engine = await WinDbgThread.GetDebugEngine().ConfigureAwait(true);
                    await engine.SendRequestAsync(new SetSymbolPathRequest(path));
                });
            }
        }

        private static Task SetDbgEngPath(string path)
        {
            WinDbgThread.SetDbgEngPath(path);
            return Task.CompletedTask;
        }

        private static Task SetSymbolStatus(bool enabled)
        {
            _displaySymbols = enabled;
            return Task.CompletedTask;
        }

        public async Task HandleAsync(RequestValueInfos command, KernelInvocationContext context)
        {
            _curprocess ??= await DebugModelProcess.CreateAsync();
            KernelValueInfo kvi = new("curprocess", FormattedValue.CreateSingleFromObject(_curprocess), typeof(DebugModelProcess), nameof(DebugModelProcess));
            context.Publish(new ValueInfosProduced([kvi], command));
        }

        public async Task HandleAsync(RequestValue command, KernelInvocationContext context)
        {
            if (command.Name == "curprocess")
            {
                _curprocess ??= await DebugModelProcess.CreateAsync();
                context.PublishValueProduced(command, _curprocess);
            }
            else
            {
                context.Fail(command, message: $"Value '{command.Name}' not found in kernel {Name}");
            }
        }
    }
}
