using System;
using LogCollector.Client;
using LogCollector.Common;

namespace LogCollector.AdminConsole
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                string host = args.Length > 0 ? args[0] : "127.0.0.1";
                int port = args.Length > 1 ? Int32.Parse(args[1]) : 5050;
                using (AdminClient admin = new AdminClient(host, port, Environment.GetEnvironmentVariable("LOGCOLLECTOR_ADMIN_TOKEN")))
                {
                    if (args.Length == 4)
                        admin.SetFilter(args[2], (LogLevel)Enum.Parse(typeof(LogLevel), args[3], false));
                    else if (args.Length > 2) throw new ArgumentException("Usage: host port [source LEVEL]");
                    Console.Write(admin.GetStatus());
                }
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
        }
    }
}
