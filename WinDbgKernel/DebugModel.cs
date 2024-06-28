using System.Xml;
using DbgX;
using DbgX.Interfaces.Enums;
using DbgX.Requests;

namespace WinDbgKernel
{
    public class DebugModelProcess
    {
        private readonly Task<DebugModelThread[]> _threadsTask;

        /// <summary>
        /// Process name.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// The OS process identifier for this process (PID)
        /// </summary>
        public int Id { get; }

        public DebugModelThread[] Threads => _threadsTask.Result;

        public static async Task<DebugModelProcess> CreateAsync()
        {
            string xml = await WinDbgThread.Queue(async () =>
            {
                DebugEngine engine = await WinDbgThread.GetDebugEngine().ConfigureAwait(true);
                return await engine.SendRequestAsync(new ModelQueryRequest("@$curprocess", false, ModelQueryFlags.Default, recursionDepth: 2)).ConfigureAwait(true);
            });

            XmlDocument xmlDoc = new();
            xmlDoc.LoadXml(xml);

            XmlNode nameNode = xmlDoc.SelectSingleNode("/Data/Element/Element[@Name='Name']") ?? throw new InvalidOperationException("Name not found");
            XmlNode idNode = xmlDoc.SelectSingleNode("/Data/Element/Element[@Name='Id']") ?? throw new InvalidOperationException("Id not found");
            XmlNode handleNode = xmlDoc.SelectSingleNode("/Data/Element/Element[@Name='Handle']") ?? throw new InvalidOperationException("Handle not found");

            string? name = nameNode?.Attributes?["DisplayValue"]?.InnerText;
            string? idStr = idNode?.Attributes?["DisplayValue"]?.InnerText;
            int id = string.IsNullOrWhiteSpace(idStr) ? 0 : int.Parse(idStr[2..], System.Globalization.NumberStyles.HexNumber);
            
            return new DebugModelProcess(name ?? "", id);
        }

        internal DebugModelProcess(string name, int id)
        {
            Name = name;
            Id = id;

            _threadsTask = LoadThreadsAsync();
        }

        private static async Task<DebugModelThread[]> LoadThreadsAsync()
        {
            string xml = await WinDbgThread.Queue(async () =>
            {
                DebugEngine engine = await WinDbgThread.GetDebugEngine().ConfigureAwait(true);
                return await engine.SendRequestAsync(new ModelQueryRequest("@$curprocess.Threads", false, ModelQueryFlags.Default, recursionDepth: 2)).ConfigureAwait(true);
            });

            List<DebugModelThread> threads = [];

            XmlDocument xmlDoc = new();
            xmlDoc.LoadXml(xml);
            XmlNodeList? threadNodes = xmlDoc.SelectNodes("/Data/Element/Element[@Iterated='true']");

            if (threadNodes is not null)
            {
                foreach (XmlNode threadNode in threadNodes)
                {
                    string? idStr = threadNode.SelectSingleNode("Element[@Name='Id']")?.Attributes?["DisplayValue"]?.InnerText;
                    string? name = threadNode.SelectSingleNode("Element[@Name='Name']")?.Attributes?["DisplayValue"]?.InnerText;

                    int id = string.IsNullOrWhiteSpace(idStr) ? 0 : int.Parse(idStr[2..], System.Globalization.NumberStyles.HexNumber);
                    threads.Add(new DebugModelThread(id, name));
                }
            }

            return threads.ToArray();
        }
    }

    public class DebugModelThread(int id, string? name)
    {
        public string? Name { get; } = name;
        public int Id { get; } = id;

        public async Task<DebugModelFrame[]> GetStackTraceAsync()
        {
            string xml = await WinDbgThread.Queue(async () =>
            {
                DebugEngine engine = await WinDbgThread.GetDebugEngine().ConfigureAwait(true);
                return await engine.SendRequestAsync(new ModelQueryRequest($"@$curprocess.Threads[{Id}].Stack.Frames", false, ModelQueryFlags.Default, recursionDepth: 3)).ConfigureAwait(true);
            });

            XmlDocument xmlDoc = new();
            xmlDoc.LoadXml(xml);

            List<DebugModelFrame> framesList = [];

            XmlNodeList? frameNodes = xmlDoc.SelectNodes("/Data/Element/Element[@Iterated='true']");

            if (frameNodes is not null)
            {
                foreach (XmlNode frameNode in frameNodes)
                {
                    List<DebugModelVariable> locals = [];
                    List<DebugModelVariable> parameters = [];

                    XmlNode? localVariablesNode = frameNode.SelectSingleNode("Element[@Name='LocalVariables']");
                    if (localVariablesNode != null)
                    {
                        XmlNodeList? varNodes = localVariablesNode.SelectNodes("Element");
                        if (varNodes is not null)
                            foreach (XmlNode varNode in varNodes)
                                locals.Add(GetVariable(varNode));
                    }

                    XmlNode? parametersNode = frameNode.SelectSingleNode("Element[@Name='Parameters']");
                    if (parametersNode != null)
                    {
                        XmlNodeList? varNodes = parametersNode.SelectNodes("Element");
                        if (varNodes is not null)
                            foreach (XmlNode varNode in varNodes)
                                parameters.Add(GetVariable(varNode));
                    }

                    string? frameDisplayValue = frameNode.Attributes?["DisplayValue"]?.InnerText;

                    DebugModelFrame frameInfo = new(frameDisplayValue, parameters.ToArray(), locals.ToArray());
                    framesList.Add(frameInfo);
                }
            }

            return framesList.ToArray();
        }

        private static DebugModelVariable GetVariable(XmlNode varNode)
        {
            string? name = varNode.Attributes?["Name"]?.InnerText;
            string? value = varNode.Attributes?["EditValue"]?.InnerText;
            string? typeName = varNode.Attributes?["TypeName"]?.InnerText;
            string? valueTypeStr = varNode.Attributes?["ValueType"]?.InnerText;

            int valueType = string.IsNullOrWhiteSpace(valueTypeStr) ? 0 : int.Parse(valueTypeStr);

            DebugModelVariable variable = new(name, value, typeName, valueType);
            return variable;
        }
    }

    public class DebugModelFrame(string? display, DebugModelVariable[] parameters, DebugModelVariable[] locals)
    {
        public string? Display { get; } = display;
        public DebugModelVariable[] Parameters { get; } = parameters;
        public DebugModelVariable[] Locals { get; } = locals;
    }

    public class DebugModelVariable(string? name, string? value, string? type, int valueKind)
    {
        public string? Name { get; set; } = name;
        public string? Type { get; set; } = type;
        public int ValueKind { get; set; } = valueKind;
        public string? Value { get; set; } = value;
        public bool IsUnavailable => Value == null;
    }
}
