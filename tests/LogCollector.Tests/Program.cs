using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using LogCollector.Client;
using LogCollector.Common;
using LogCollector.Server;

namespace LogCollector.Tests
{
    internal static partial class Program
    {
        private static int passed, failures;
        private static readonly List<string> folders = new List<string>();
        private const string Token = "test-only-administrator-token";

        private static int Main(string[] args)
        {
            if (args.Length > 0) return RunLoadProbe(args);
            Run("R03 validation and five ordered levels", TestValidation);
            Run("R03 R08 Unicode, timestamp and multiline round trip", TestRoundTrip);
            Run("R08 fragmented and coalesced stream frames", TestStreamBoundaries);
            Run("R08 truncated frame is not clean EOF", TestTruncated);
            Run("R08 invalid type, length, UTF-8 and trailing payload", TestInvalidFrames);
            Run("R05 common log rotation and escaped physical lines", TestRotation);
            Run("R05 restart appends without truncation", TestWriterRestart);
            Run("R05 recovery removes only incomplete final record", TestTailRecovery);
            Run("R05 oversized record remains whole", TestOversizedRecord);
            Run("R06 all thresholds and isolation of sources", TestFilters);
            Run("R04 duplicate names and disconnection cleanup", TestPool);
            Run("R01 R09 R10 three simultaneous agents and exact totals", TestConcurrency);
            Run("R02 roles and administrator authentication", TestRoles);
            Run("R03 agent cannot impersonate another source", TestSourceMismatch);
            Run("R08 invalid peer does not stop the server", TestMalformedPeer);
            Run("R09 unfinished greeting does not block an agent", TestIdlePeer);
            Run("R08 R09 incomplete established frame has a deadline", TestFrameDeadline);
            Run("R05 R10 storage failure is not acknowledged as stored", TestStorageFailure);
            Run("R09 bounded connections", TestConnectionLimit);
            Run("R09 shutdown releases connected clients", TestShutdown);
            Run("R07 library reports unavailable server", TestUnavailable);
            Run("R03 R08 empty and exact maximum UTF-8 messages", TestMessageBoundaries);
            Run("R05 exact rotation limit and empty last segment", TestRotationBoundaries);
            Run("R09 unfinished greeting expires", TestGreetingTimeout);
            Run("R08 total deadline under a slow byte drip", TestSlowDrip);
            Run("R06 configured source limit and existing updates", TestFilterLimit);
            Run("R07 blocked request can be interrupted by Dispose", TestInterruptRequest);
            Run("R07 lost acknowledgement is not silently retried", TestLostAcknowledgement);
            Run("R05 R06 R10 explicit restart semantics", TestServerRestart);
            Run("R10 validated immutable server snapshot", TestSnapshot);
            Run("R10 malformed snapshots rejected before presentation", TestInvalidSnapshots);
            Run("R01 R06 R10 concurrent rule changes and exact accounting", TestConcurrentFilters);
            Run("R06 invalid filter acknowledgement closes the channel", TestInvalidFilterAcknowledgement);
            Run("R05 R10 partial write faults storage and restart recovers tail", TestPartialWriteFailure);
            Console.WriteLine("RESULT: {0} passed, {1} failed", passed, failures);
            foreach (string folder in folders)
            {
                // Удаляем только временные папки этого запуска.
                try
                {
                    string expectedPrefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                        + Path.DirectorySeparatorChar + "logcollector-test-";
                    if (!Path.GetFullPath(folder).StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Unexpected cleanup path.");
                    Directory.Delete(folder, true);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return failures == 0 ? 0 : 1;
        }

        private static void Run(string name, Action test)
        {
            try { test(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception error) { failures++; Console.WriteLine("FAIL " + name + ": " + error); }
        }

        private static void Check(bool condition, string message)
        { if (!condition) throw new Exception(message); }

        private static void Throws<T>(Action action) where T : Exception
        {
            try { action(); }
            catch (T) { return; }
            throw new Exception("Expected " + typeof(T).Name);
        }

        private static string Folder()
        {
            string folder = Path.Combine(Path.GetTempPath(), "logcollector-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            folders.Add(folder);
            return folder;
        }

        private static LogRecord Record(LogLevel level, string source, string message)
        { return new LogRecord(new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc), level, source, message); }

        private static LogServer Server(string folder)
        {
            LogServer server = new LogServer(IPAddress.Loopback, 0, folder, 2000, Token, 64);
            server.Start();
            return server;
        }

        private static void TestValidation()
        {
            Check(Enum.GetValues(typeof(LogLevel)).Length == 5, "Exactly five levels required.");
            for (int i = 0; i < 5; i++) Check((int)(LogLevel)i == i && Enum.IsDefined(typeof(LogLevel), (byte)i), "Level order.");
            Throws<ArgumentException>(delegate { Record((LogLevel)9, "web", "x"); });
            Throws<ArgumentException>(delegate { Record(LogLevel.INFO, "\tweb", "x"); });
            Throws<ArgumentException>(delegate { Record(LogLevel.INFO, " ", "x"); });
            Throws<ArgumentException>(delegate { Record(LogLevel.INFO, new string('x', 129), "x"); });
            Throws<ArgumentException>(delegate { Record(LogLevel.INFO, "web", null); });
            Throws<ArgumentException>(delegate { Record(LogLevel.INFO, "web", new string('я', 32769)); });
            Throws<ArgumentException>(delegate { new LogRecord(DateTime.Now, LogLevel.INFO, "web", "x"); });
            Throws<EncoderFallbackException>(delegate { Record(LogLevel.INFO, "web", "\ud800"); });
        }

        private static void TestRoundTrip()
        {
            LogRecord expected = Record(LogLevel.CRITICAL, "Источник 🖥", "Строка 1\nСтрока 2\t|\\\0");
            LogRecord actual = Protocol.DecodeLog(Protocol.EncodeLog(expected));
            Check(actual.TimestampUtc == expected.TimestampUtc && actual.TimestampUtc.Kind == DateTimeKind.Utc,
                "Timestamp changed.");
            Check(actual.Source == expected.Source && actual.Message == expected.Message && actual.Level == expected.Level,
                "Payload changed.");
        }

        private sealed class FragmentedStream : MemoryStream
        {
            public FragmentedStream(byte[] bytes) : base(bytes) { }
            public override int Read(byte[] buffer, int offset, int count)
            { return base.Read(buffer, offset, Math.Min(count, 1)); }
        }

        private static void TestStreamBoundaries()
        {
            using (MemoryStream output = new MemoryStream())
            {
                Protocol.WriteFrame(output, Protocol.Text(MessageType.HelloAgent, "web"));
                Protocol.WriteFrame(output, Protocol.EncodeLog(Record(LogLevel.WARN, "web", "message")));
                using (FragmentedStream input = new FragmentedStream(output.ToArray()))
                {
                    Check(Protocol.ReadText(Protocol.ReadFrame(input)) == "web", "Greeting frame.");
                    Check(Protocol.DecodeLog(Protocol.ReadFrame(input)).Message == "message", "Second frame.");
                    Check(Protocol.ReadFrame(input) == null, "EOF between frames.");
                }
            }
        }

        private static void TestTruncated()
        {
            Throws<EndOfStreamException>(delegate { Protocol.ReadFrame(new MemoryStream(new byte[] { 1, 1 })); });
            Throws<EndOfStreamException>(delegate { Protocol.ReadFrame(new MemoryStream(new byte[] { 1, 2, 0, 0, 0, 1 })); });
        }

        private static void TestInvalidFrames()
        {
            Throws<InvalidDataException>(delegate { Protocol.ReadFrame(new MemoryStream(new byte[] { 99 })); });
            Throws<InvalidDataException>(delegate { Protocol.ReadFrame(new MemoryStream(new byte[] { 1, 255, 255, 255, 255 })); });
            Throws<InvalidDataException>(delegate { Protocol.ReadFrame(new MemoryStream(new byte[] { 1, 0, 0, 16, 0 })); });
            Throws<InvalidDataException>(delegate { Protocol.ReadText(new Frame(MessageType.Response, new byte[] { 2, 0, 0, 0, 65 })); });
            Throws<DecoderFallbackException>(delegate { Protocol.ReadText(new Frame(MessageType.Response, new byte[] { 1, 0, 0, 0, 255 })); });
            Throws<InvalidDataException>(delegate { Protocol.ReadText(new Frame(MessageType.Response, new byte[] { 0, 0, 0, 0, 42 })); });
        }

        private static string[] Lines(string folder)
        {
            List<string> lines = new List<string>();
            string[] files = Directory.GetFiles(folder, "logs-*.log");
            Array.Sort(files, StringComparer.Ordinal);
            foreach (string file in files)
            {
                using (FileStream stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null) lines.Add(line);
                }
            }
            return lines.ToArray();
        }

        private static void TestRotation()
        {
            string folder = Folder();
            using (RotatingLogWriter writer = new RotatingLogWriter(folder, 180))
                for (int i = 0; i < 12; i++) writer.Append(Record(LogLevel.INFO, i % 2 == 0 ? "web" : "db", "row" + i + "\n\t\\\0"));
            Check(Directory.GetFiles(folder, "*.log").Length > 1, "Rotation did not happen.");
            foreach (string file in Directory.GetFiles(folder, "*.log"))
                Check(new FileInfo(file).Length <= 180, "Normal record exceeds rotation limit.");
            string[] lines = Lines(folder);
            Check(lines.Length == 12, "Multiline messages split records or records lost.");
            for (int i = 0; i < 12; i++) Check(lines[i].Contains("row" + i + "\\n\\t\\\\\\u0000"), "Order / escaping changed.");
        }

        private static void TestWriterRestart()
        {
            string folder = Folder();
            using (RotatingLogWriter writer = new RotatingLogWriter(folder, 500)) writer.Append(Record(LogLevel.INFO, "web", "before"));
            using (RotatingLogWriter writer = new RotatingLogWriter(folder, 500)) writer.Append(Record(LogLevel.INFO, "db", "after"));
            string[] lines = Lines(folder);
            Check(lines.Length == 2 && lines[0].Contains("before") && lines[1].Contains("after"), "Restart truncated log.");
        }

        private static void TestOversizedRecord()
        {
            string folder = Folder();
            using (RotatingLogWriter writer = new RotatingLogWriter(folder, 20))
            {
                writer.Append(Record(LogLevel.INFO, "web", new string('x', 100)));
                writer.Append(Record(LogLevel.INFO, "db", "next"));
            }
            Check(Directory.GetFiles(folder, "*.log").Length == 2 && Lines(folder).Length == 2, "Large record split / lost.");
        }

        private static void TestTailRecovery()
        {
            string folder = Folder();
            using (RotatingLogWriter writer = new RotatingLogWriter(folder, 10000))
                writer.Append(Record(LogLevel.INFO, "web", "complete"));
            string file = Path.Combine(folder, "logs-000001.log");
            File.AppendAllText(file, "incomplete" + new string('x', 5000), new UTF8Encoding(false));
            using (RotatingLogWriter writer = new RotatingLogWriter(folder, 10000))
            {
                Check(writer.RecoveredBytes == 5010, "Unexpected recovery count.");
                writer.Append(Record(LogLevel.ERROR, "db", "after recovery"));
            }
            string[] lines = Lines(folder);
            Check(lines.Length == 2 && lines[0].Contains("complete") && lines[1].Contains("after recovery"),
                "Recovery lost valid records or retained incomplete suffix.");
        }

        private static void TestFilters()
        {
            string folder = Folder();
            using (LogServer server = Server(folder))
            using (AdminClient admin = new AdminClient("127.0.0.1", server.Port, Token))
            using (RemoteLogger web = new RemoteLogger("127.0.0.1", server.Port, "WebServer"))
            using (RemoteLogger db = new RemoteLogger("127.0.0.1", server.Port, "Database"))
            {
                int expectedStored = 0;
                for (int minimum = 0; minimum < 5; minimum++)
                {
                    admin.SetFilter("WebServer", (LogLevel)minimum);
                    for (int level = 0; level < 5; level++)
                    {
                        bool expected = level >= minimum;
                        Check(web.Log((LogLevel)level, "threshold-" + minimum) == expected, "Wrong threshold.");
                        if (expected) expectedStored++;
                    }
                    Check(db.Log(LogLevel.DEBUG, "unaffected"), "Other source filter changed.");
                    expectedStored++;
                }
                Check(Lines(folder).Length == expectedStored, "File and acknowledgements differ.");
                Check(admin.GetStatus().Contains("WebServer\tCRITICAL\t1"), "Dynamic threshold not visible.");
                long[,] totals = Totals(admin.GetStatus());
                for (int i = 0; i < 5; i++)
                    Check(totals[i, 0] == totals[i, 1] + totals[i, 2] + totals[i, 3], "Accounting identity.");
            }
        }

        private static void Eventually(Func<bool> condition)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline)
            {
                if (condition()) return;
                Thread.Sleep(20);
            }
            throw new Exception("Expected asynchronous state did not arrive.");
        }

        private static void TestPool()
        {
            using (LogServer server = Server(Folder()))
            using (AdminClient admin = new AdminClient("127.0.0.1", server.Port, Token))
            using (RemoteLogger first = new RemoteLogger("127.0.0.1", server.Port, "same"))
            {
                RemoteLogger second = new RemoteLogger("127.0.0.1", server.Port, "same");
                try { Check(admin.GetStatus().Contains("same\tDEBUG\t2"), "Duplicate names overwrite connection."); }
                finally { second.Dispose(); }
                Eventually(delegate { return admin.GetStatus().Contains("same\tDEBUG\t1"); });
                TcpClient abrupt = new TcpClient("127.0.0.1", server.Port);
                Protocol.WriteFrame(abrupt.GetStream(), Protocol.Text(MessageType.HelloAgent, "abrupt"));
                Check(Protocol.ReadText(Protocol.ReadFrame(abrupt.GetStream())) == "READY", "Raw agent greeting.");
                abrupt.Client.LingerState = new LingerOption(true, 0);
                abrupt.Close();
                Eventually(delegate { return !admin.GetStatus().Contains("abrupt\t"); });
            }
        }

        private static long[,] Totals(string status)
        {
            string[] rows = status.Split('\n');
            long[,] result = new long[5, 4];
            for (int i = 0; i < 5; i++)
            {
                string[] values = rows[i + 1].Split('\t');
                Check(values[0] == ((LogLevel)i).ToString(), "Snapshot order.");
                for (int j = 0; j < 4; j++) result[i, j] = Int64.Parse(values[j + 1]);
            }
            return result;
        }

        private static void TestConcurrency()
        {
            string folder = Folder();
            using (LogServer server = Server(folder))
            using (AdminClient admin = new AdminClient("127.0.0.1", server.Port, Token))
            {
                List<Exception> errors = new List<Exception>();
                Thread[] workers = new Thread[3];
                for (int agent = 0; agent < workers.Length; agent++)
                {
                    int number = agent;
                    workers[agent] = new Thread(delegate()
                    {
                        try
                        {
                            using (RemoteLogger logger = new RemoteLogger("127.0.0.1", server.Port, "agent-" + number))
                                for (int i = 0; i < 50; i++) Check(logger.Log((LogLevel)(i % 5), number + ":" + i), "Unexpected filtering.");
                        }
                        catch (Exception error) { lock (errors) errors.Add(error); }
                    });
                    workers[agent].Start();
                }
                foreach (Thread worker in workers) Check(worker.Join(10000), "Agent hung.");
                Check(errors.Count == 0, errors.Count == 0 ? "" : errors[0].ToString());
                long[,] totals = Totals(admin.GetStatus());
                for (int level = 0; level < 5; level++)
                    Check(totals[level, 0] == 30 && totals[level, 1] == 30 && totals[level, 2] == 0 && totals[level, 3] == 0,
                        "Parallel counters do not match the 150 sent records.");
                string[] lines = Lines(folder);
                Check(lines.Length == 150 && new HashSet<string>(lines).Count == 150, "Missing or duplicate records.");
            }
        }

        private static TcpClient RawAgent(int port, string source)
        {
            TcpClient client = new TcpClient("127.0.0.1", port);
            client.ReceiveTimeout = 3000;
            Protocol.WriteFrame(client.GetStream(), Protocol.Text(MessageType.HelloAgent, source));
            Check(Protocol.ReadText(Protocol.ReadFrame(client.GetStream())) == "READY", "Raw greeting failed.");
            return client;
        }

        private static void TestRoles()
        {
            using (LogServer server = Server(Folder()))
            {
                Throws<IOException>(delegate { using (AdminClient wrong = new AdminClient("127.0.0.1", server.Port, "wrong")) { } });
                using (TcpClient agent = RawAgent(server.Port, "web"))
                {
                    Protocol.WriteFrame(agent.GetStream(), Protocol.EncodeFilter("web", LogLevel.ERROR));
                    Check(Protocol.ReadFrame(agent.GetStream()).Type == MessageType.Error, "Agent changed administrator filter.");
                }
                using (AdminClient correct = new AdminClient("127.0.0.1", server.Port, Token))
                    Check(correct.GetStatus().Contains("LEVEL\t"), "Server stopped after wrong role.");
            }
        }

        private static void TestSourceMismatch()
        {
            string folder = Folder();
            using (LogServer server = Server(folder))
            using (TcpClient agent = RawAgent(server.Port, "web"))
            {
                Protocol.WriteFrame(agent.GetStream(), Protocol.EncodeLog(Record(LogLevel.INFO, "other", "fake")));
                Check(Protocol.ReadFrame(agent.GetStream()).Type == MessageType.Error, "Source spoofing accepted.");
                Check(Lines(folder).Length == 0, "Spoofed log stored.");
            }
        }

        private static void TestMalformedPeer()
        {
            using (LogServer server = Server(Folder()))
            {
                using (TcpClient bad = new TcpClient("127.0.0.1", server.Port))
                {
                    bad.ReceiveTimeout = 3000;
                    bad.GetStream().Write(new byte[] { 1, 255, 255, 255, 255 }, 0, 5);
                    Check(Protocol.ReadFrame(bad.GetStream()).Type == MessageType.Error, "Invalid peer not rejected.");
                }
                using (RemoteLogger good = new RemoteLogger("127.0.0.1", server.Port, "good"))
                    Check(good.Log(LogLevel.INFO, "alive"), "Server died after malformed input.");
            }
        }

        private static void TestIdlePeer()
        {
            using (LogServer server = Server(Folder()))
            using (TcpClient idle = new TcpClient("127.0.0.1", server.Port))
            using (RemoteLogger active = new RemoteLogger("127.0.0.1", server.Port, "active"))
                Check(active.Log(LogLevel.ERROR, "still working"), "Partial greeting blocked other client.");
        }

        private static void TestConnectionLimit()
        {
            using (LogServer server = new LogServer(IPAddress.Loopback, 0, Folder(), 1000, Token, 1))
            {
                server.Start();
                using (RemoteLogger first = new RemoteLogger("127.0.0.1", server.Port, "first"))
                {
                    Throws<IOException>(delegate { using (RemoteLogger excess = new RemoteLogger("127.0.0.1", server.Port, "excess")) { } });
                    Check(first.Log(LogLevel.INFO, "within limit"), "Limit disconnected existing agent.");
                }
            }
        }

        private static void TestFrameDeadline()
        {
            using (LogServer server = new LogServer(IPAddress.Loopback, 0, Folder(), 1000, Token, 64, 200))
            {
                server.Start();
                using (TcpClient partial = RawAgent(server.Port, "partial"))
                {
                    byte[] startedFrame = { (byte)MessageType.Log, 100, 0, 0, 0, 1 };
                    partial.GetStream().Write(startedFrame, 0, startedFrame.Length);
                    Check(Protocol.ReadFrame(partial.GetStream()).Type == MessageType.Error, "Incomplete frame never timed out.");
                }
                using (RemoteLogger healthy = new RemoteLogger("127.0.0.1", server.Port, "healthy"))
                    Check(healthy.Log(LogLevel.INFO, "after timeout"), "Timeout damaged server.");
            }
        }

        private static void TestStorageFailure()
        {
            string folder = Folder();
            using (LogServer server = new LogServer(IPAddress.Loopback, 0, folder, 80, Token, 64))
            {
                server.Start();
                using (AdminClient admin = new AdminClient("127.0.0.1", server.Port, Token))
                {
                    using (RemoteLogger logger = new RemoteLogger("127.0.0.1", server.Port, "web"))
                    {
                        Check(logger.Log(LogLevel.INFO, new string('x', 30)), "Initial write failed.");
                        Directory.CreateDirectory(Path.Combine(folder, "logs-000002.log"));
                        Throws<IOException>(delegate { logger.Log(LogLevel.INFO, new string('y', 30)); });
                    }
                    using (RemoteLogger second = new RemoteLogger("127.0.0.1", server.Port, "web"))
                        Throws<IOException>(delegate { second.Log(LogLevel.INFO, "after fault"); });
                    long[,] totals = Totals(admin.GetStatus());
                    Check(totals[1, 0] == 3 && totals[1, 1] == 1 && totals[1, 2] == 0 && totals[1, 3] == 2,
                        "Storage error acknowledged as success or not counted.");
                }
            }
            Check(Lines(folder).Length == 1, "Writer appended after storage failure.");
        }

        private static void TestShutdown()
        {
            LogServer server = Server(Folder());
            using (RemoteLogger client = new RemoteLogger("127.0.0.1", server.Port, "idle"))
            {
                server.Dispose();
                server.Dispose();
                Throws<IOException>(delegate { client.Log(LogLevel.INFO, "after stop"); });
            }
        }

        private static void TestUnavailable()
        {
            TcpListener temporary = new TcpListener(IPAddress.Loopback, 0);
            temporary.Start();
            int port = ((IPEndPoint)temporary.LocalEndpoint).Port;
            temporary.Stop();
            Throws<SocketException>(delegate { using (RemoteLogger client = new RemoteLogger("127.0.0.1", port, "web")) { } });
        }
    }
}
