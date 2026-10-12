using System;
using System.Threading;
using LogCollector.Client;
using LogCollector.Common;

namespace LogCollector.DemoAgent
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                string source = args.Length > 0 ? args[0] : "WebServer";
                string host = args.Length > 1 ? args[1] : "127.0.0.1";
                int port = args.Length > 2 ? Int32.Parse(args[2]) : 5050;
                int rounds = args.Length > 3 ? Int32.Parse(args[3]) : 10;
                using (RemoteLogger logger = new RemoteLogger(host, port, source))
                    for (int round = 0; round < rounds; round++)
                        foreach (LogLevel level in Enum.GetValues(typeof(LogLevel)))
                        {
                            bool stored = logger.Log(level, "Демонстрационная запись " + round + " уровня " + level);
                            Console.WriteLine("{0}: {1} -> {2}", source, level, stored ? "stored" : "filtered");
                            Thread.Sleep(300);
                        }
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
        }
    }
}
