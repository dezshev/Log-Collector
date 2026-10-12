using System;
using System.Net;
using System.Threading;

namespace LogCollector.Server
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                string token = Environment.GetEnvironmentVariable("LOGCOLLECTOR_ADMIN_TOKEN");
                if (String.IsNullOrEmpty(token)) throw new ArgumentException("Set LOGCOLLECTOR_ADMIN_TOKEN before starting.");
                int port = args.Length > 0 ? Int32.Parse(args[0]) : 5050;
                string directory = args.Length > 1 ? args[1] : "data";
                IPAddress bind = args.Length > 2 ? IPAddress.Parse(args[2]) : IPAddress.Loopback;
                using (ManualResetEvent stop = new ManualResetEvent(false))
                using (LogServer server = new LogServer(bind, port, directory, 10L * 1024 * 1024, token, 64))
                {
                    ConsoleCancelEventHandler handler = delegate(object sender, ConsoleCancelEventArgs e)
                    { e.Cancel = true; stop.Set(); };
                    Console.CancelKeyPress += handler;
                    try
                    {
                        server.Start();
                        if (server.RecoveredBytes > 0)
                            Console.Error.WriteLine("Recovered incomplete log tail: {0} bytes removed.", server.RecoveredBytes);
                        Console.WriteLine("TCP log server: {0}:{1}; directory: {2}. Ctrl+C to stop.", bind, server.Port, directory);
                        stop.WaitOne();
                    }
                    finally { Console.CancelKeyPress -= handler; }
                }
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
        }
    }
}
