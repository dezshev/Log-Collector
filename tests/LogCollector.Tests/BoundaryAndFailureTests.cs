using System;
using System.Diagnostics;
using System.Globalization;
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
        private static void TestMessageBoundaries()
        {
            string maximum = new string('я', 32768);
            foreach (string message in new string[] { String.Empty, maximum })
                Check(Protocol.DecodeLog(Protocol.EncodeLog(Record(LogLevel.INFO, "web", message))).Message == message,
                    "Boundary message did not round-trip.");
            string folder = Folder();
            using (LogServer server = Server(folder))
            using (RemoteLogger logger = new RemoteLogger("127.0.0.1", server.Port, "web"))
            {
                Check(logger.Log(LogLevel.INFO, String.Empty), "Empty message rejected.");
                Check(logger.Log(LogLevel.CRITICAL, maximum), "Maximum UTF-8 message rejected.");
            }
            Check(Lines(folder).Length == 2 && Lines(folder)[1].EndsWith(maximum, StringComparison.Ordinal), "Boundary log damaged.");
        }

        private static void TestRotationBoundaries()
        {
            string folder = Folder();
            LogRecord record = Record(LogLevel.INFO, "web", "exact");
            string line = record.TimestampUtc.ToString("o", CultureInfo.InvariantCulture) + "\tINFO\tweb\texact\n";
            int limit = Encoding.UTF8.GetByteCount(line);
            using (RotatingLogWriter writer = new RotatingLogWriter(folder, limit))
            {
                writer.Append(record);
                Check(new FileInfo(Path.Combine(folder, "logs-000001.log")).Length == limit, "Boundary not reached.");
                writer.Append(record);
            }
            Check(Directory.GetFiles(folder, "*.log").Length == 2 && Lines(folder).Length == 2, "Exact-size rotation incorrect.");
            File.WriteAllBytes(Path.Combine(folder, "logs-000003.log"), new byte[0]);
            using (RotatingLogWriter writer = new RotatingLogWriter(folder, limit)) writer.Append(record);
            Check(Lines(folder).Length == 3 && new FileInfo(Path.Combine(folder, "logs-000003.log")).Length == limit,
                "Empty final segment not reused.");
        }

        private static void TestGreetingTimeout()
        {
            using (LogServer server = new LogServer(IPAddress.Loopback, 0, Folder(), 1000, Token, 64, 200))
            {
                server.Start();
                using (TcpClient idle = new TcpClient("127.0.0.1", server.Port))
                {
                    idle.ReceiveTimeout = 3000;
                    Check(Protocol.ReadFrame(idle.GetStream()).Type == MessageType.Error, "Greeting did not expire.");
                }
                using (RemoteLogger good = new RemoteLogger("127.0.0.1", server.Port, "good"))
                    Check(good.Log(LogLevel.INFO, "after greeting timeout"), "Server stopped after greeting timeout.");
            }
        }

        private static void TestSlowDrip()
        {
            using (LogServer server = new LogServer(IPAddress.Loopback, 0, Folder(), 1000, Token, 64, 250))
            {
                server.Start();
                using (TcpClient agent = RawAgent(server.Port, "drip"))
                {
                    agent.NoDelay = true;
                    byte[] bytes;
                    using (MemoryStream encoded = new MemoryStream())
                    { Protocol.WriteFrame(encoded, Protocol.EncodeLog(Record(LogLevel.INFO, "drip", "unfinished"))); bytes = encoded.ToArray(); }
                    Thread sender = new Thread(delegate()
                    {
                        try
                        {
                            foreach (byte value in bytes) { agent.GetStream().WriteByte(value); Thread.Sleep(40); }
                        }
                        catch (IOException) { }
                        catch (ObjectDisposedException) { }
                        catch (InvalidOperationException) { }
                    });
                    sender.IsBackground = true;
                    Stopwatch elapsed = Stopwatch.StartNew();
                    sender.Start();
                    Check(Protocol.ReadFrame(agent.GetStream()).Type == MessageType.Error, "Slow drip extended frame deadline.");
                    Check(elapsed.ElapsedMilliseconds < 2000, "Deadline renewed for each byte.");
                    agent.Close();
                    Check(sender.Join(2000), "Drip sender remained alive.");
                }
            }
        }

        private static void TestFilterLimit()
        {
            using (LogServer server = Server(Folder()))
            {
                using (AdminClient admin = new AdminClient("127.0.0.1", server.Port, Token))
                {
                    for (int i = 0; i < 256; i++) admin.SetFilter("source-" + i, LogLevel.INFO);
                    Throws<IOException>(delegate { admin.SetFilter("excess", LogLevel.ERROR); });
                }
                using (AdminClient admin = new AdminClient("127.0.0.1", server.Port, Token))
                {
                    admin.SetFilter("source-0", LogLevel.CRITICAL);
                    ServerStatus status = admin.GetSnapshot();
                    Check(status.Sources.Count == 256, "Source cap changed existing rules.");
                    bool updated = false;
                    foreach (SourceStatus source in status.Sources)
                        if (source.Name == "source-0") updated = source.Minimum == LogLevel.CRITICAL;
                    Check(updated, "An existing rule cannot be updated at the limit.");
                }
            }
        }

        private sealed class ScriptedPeer : IDisposable
        {
            private readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            private readonly Thread worker;
            private readonly object gate = new object();
            private TcpClient accepted;
            public readonly ManualResetEvent Received = new ManualResetEvent(false);
            public readonly ManualResetEvent Release = new ManualResetEvent(false);
            public int Port { get { return ((IPEndPoint)listener.LocalEndpoint).Port; } }
            public ScriptedPeer(Action<ScriptedPeer, TcpClient> script)
            {
                listener.Start();
                worker = new Thread(delegate()
                {
                    try
                    {
                        TcpClient connection = listener.AcceptTcpClient();
                        lock (gate) accepted = connection;
                        using (connection) script(this, connection);
                    }
                    catch (IOException) { }
                    catch (SocketException) { }
                    catch (ObjectDisposedException) { }
                });
                worker.IsBackground = true;
                worker.Start();
            }
            public void Dispose()
            {
                Release.Set(); listener.Stop();
                lock (gate) if (accepted != null) accepted.Close();
                Check(worker.Join(3000), "Scripted peer failed to stop.");
                Received.Dispose(); Release.Dispose();
            }
        }

        private static void TestInterruptRequest()
        {
            using (ScriptedPeer peer = new ScriptedPeer(delegate(ScriptedPeer state, TcpClient connection)
            {
                Protocol.ReadFrame(connection.GetStream());
                Protocol.WriteFrame(connection.GetStream(), Protocol.Text(MessageType.Response, "READY"));
                Protocol.ReadFrame(connection.GetStream());
                state.Received.Set(); state.Release.WaitOne();
            }))
            using (AdminClient admin = new AdminClient("127.0.0.1", peer.Port, Token))
            {
                Exception failure = null;
                Thread request = new Thread(delegate() { try { admin.GetStatus(); } catch (Exception error) { failure = error; } });
                request.IsBackground = true; request.Start();
                Check(peer.Received.WaitOne(3000), "Request did not reach peer.");
                Stopwatch elapsed = Stopwatch.StartNew(); admin.Dispose();
                Check(elapsed.ElapsedMilliseconds < 1000, "Dispose waited for the 5-second network timeout.");
                Check(request.Join(2000) && failure != null, "Blocked request was not interrupted.");
            }
        }

        private static void TestLostAcknowledgement()
        {
            string folder = Folder();
            using (ScriptedPeer peer = new ScriptedPeer(delegate(ScriptedPeer state, TcpClient connection)
            {
                Protocol.ReadFrame(connection.GetStream());
                Protocol.WriteFrame(connection.GetStream(), Protocol.Text(MessageType.Response, "READY"));
                LogRecord record = Protocol.DecodeLog(Protocol.ReadFrame(connection.GetStream()));
                using (RotatingLogWriter writer = new RotatingLogWriter(folder, 1000)) writer.Append(record);
                state.Received.Set(); // Закрываем связь без подтверждения записи.
            }))
            using (RemoteLogger logger = new RemoteLogger("127.0.0.1", peer.Port, "web"))
            {
                Throws<IOException>(delegate { logger.Log(LogLevel.INFO, "written without ack"); });
                Check(peer.Received.WaitOne(2000), "Peer did not write record.");
                Throws<ObjectDisposedException>(delegate { logger.Log(LogLevel.INFO, "no hidden reconnect"); });
            }
            Check(Lines(folder).Length == 1, "Lost acknowledgement caused a hidden retry / duplicate.");
        }

        private static void TestServerRestart()
        {
            string folder = Folder();
            using (LogServer server = Server(folder))
            using (AdminClient admin = new AdminClient("127.0.0.1", server.Port, Token))
            using (RemoteLogger logger = new RemoteLogger("127.0.0.1", server.Port, "web"))
            {
                Check(logger.Log(LogLevel.INFO, "before"), "Initial record.");
                admin.SetFilter("web", LogLevel.ERROR);
                Check(!logger.Log(LogLevel.INFO, "filtered before restart"), "Initial filter.");
            }
            using (LogServer server = Server(folder))
            using (AdminClient admin = new AdminClient("127.0.0.1", server.Port, Token))
            {
                Check(admin.GetSnapshot().TotalReceived == 0 && admin.GetSnapshot().Sources.Count == 0, "Runtime state persisted unexpectedly.");
                using (RemoteLogger logger = new RemoteLogger("127.0.0.1", server.Port, "web"))
                    Check(logger.Log(LogLevel.INFO, "after"), "Default DEBUG not restored.");
                Check(admin.GetSnapshot().TotalStored == 1, "Restart counter incorrect.");
            }
            Check(Lines(folder).Length == 2, "Restart lost completed records.");
        }

        private static string EmptyStatus()
        {
            StringBuilder status = new StringBuilder("LEVEL\tRECEIVED\tSTORED\tFILTERED\tFAILED\n");
            for (int i = 0; i < 5; i++) status.Append((LogLevel)i).Append("\t0\t0\t0\t0\n");
            return status.Append("SOURCE\tMINIMUM\tACTIVE_CONNECTIONS\n").ToString();
        }

        private static void TestSnapshot()
        {
            ServerStatus status = ServerStatus.Parse(EmptyStatus() + "Русский источник\tERROR\t2\n");
            Check(status.Levels.Count == 5 && status.Sources.Count == 1 && status.ActiveConnections == 2, "Snapshot data lost.");
            Check(status.Sources[0].Minimum == LogLevel.ERROR && status.TotalStored == 0, "Snapshot values incorrect.");
            Throws<NotSupportedException>(delegate { ((System.Collections.Generic.IList<SourceStatus>)status.Sources).Clear(); });
        }

        private static void TestInvalidSnapshots()
        {
            string valid = EmptyStatus();
            foreach (string invalid in new string[]
            {
                "", valid.Replace("RECEIVED", "TOTAL"), valid.Replace("INFO\t0", "DEBUG\t0"),
                valid.Replace("INFO\t0\t0\t0\t0", "INFO\t0\t1\t0\t0"),
                valid.Replace("INFO\t0\t0", "INFO\t-1\t0"), valid + "web\tINFO\t1\nweb\tERROR\t2\n",
                valid + "web\t9\t1\n", valid + "web\tINFO\t65\n",
                valid + "\n", valid + "web\t0\t1\n",
                valid.Replace("\t0\t0\t0\t0", "\t9223372036854775807\t9223372036854775807\t0\t0")
            })
                Throws<InvalidDataException>(delegate { ServerStatus.Parse(invalid); });
        }

        private static void TestConcurrentFilters()
        {
            string folder = Folder();
            using (LogServer server = Server(folder))
            using (AdminClient admin = new AdminClient("127.0.0.1", server.Port, Token))
            {
                int stored = 0;
                Exception failure = null;
                Thread[] agents = new Thread[3];
                for (int i = 0; i < agents.Length; i++)
                {
                    int number = i;
                    agents[i] = new Thread(delegate()
                    {
                        try
                        {
                            using (RemoteLogger logger = new RemoteLogger("127.0.0.1", server.Port, "shared"))
                                for (int j = 0; j < 200; j++)
                                    if (logger.Log((LogLevel)(j % 5), number + ":" + j)) Interlocked.Increment(ref stored);
                        }
                        catch (Exception error) { Interlocked.CompareExchange(ref failure, error, null); }
                    });
                    agents[i].IsBackground = true; agents[i].Start();
                }
                for (int i = 0; i < 100; i++) admin.SetFilter("shared", (LogLevel)(i % 5));
                foreach (Thread agent in agents) Check(agent.Join(10000), "Concurrent agent hung.");
                Check(failure == null, failure == null ? "" : failure.ToString());
                ServerStatus status = admin.GetSnapshot();
                Check(status.TotalReceived == 600 && status.TotalStored == stored && status.TotalFiltered == 600 - stored &&
                    status.TotalFailed == 0 && Lines(folder).Length == stored, "Filter race violated accounting.");
            }
        }

        private sealed class PartialWriteStream : FileStream
        {
            public PartialWriteStream(string path) : base(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read)
            { Position = Length; }
            public override void Write(byte[] buffer, int offset, int count)
            {
                base.Write(buffer, offset, count / 2);
                base.Flush();
                throw new IOException("Injected failure after partial file write.");
            }
        }

        private static void TestPartialWriteFailure()
        {
            string folder = Folder();
            string path = Path.Combine(folder, "logs-000001.log");
            long completeLength;
            using (LogServer server = Server(folder))
            using (AdminClient admin = new AdminClient("127.0.0.1", server.Port, Token))
            {
                using (RemoteLogger logger = new RemoteLogger("127.0.0.1", server.Port, "web"))
                {
                    Check(logger.Log(LogLevel.INFO, "whole record"), "Initial record.");
                    completeLength = new FileInfo(path).Length;
                    // В тесте заменяем поток между запросами.
                    // Используем только временный журнал теста.
                    System.Reflection.FieldInfo writerField = typeof(LogServer).GetField("writer",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    object writer = writerField.GetValue(server);
                    System.Reflection.FieldInfo streamField = typeof(RotatingLogWriter).GetField("stream",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    ((FileStream)streamField.GetValue(writer)).Dispose();
                    streamField.SetValue(writer, new PartialWriteStream(path));
                    Throws<IOException>(delegate { logger.Log(LogLevel.INFO, "partial record"); });
                }
                long failedLength = new FileInfo(path).Length;
                Check(failedLength > completeLength, "No partial write injected.");
                using (RemoteLogger another = new RemoteLogger("127.0.0.1", server.Port, "web"))
                    Throws<IOException>(delegate { another.Log(LogLevel.INFO, "must not append"); });
                Check(new FileInfo(path).Length == failedLength, "Appended after partial write.");
                ServerStatus snapshot = admin.GetSnapshot();
                Check(snapshot.TotalReceived == 3 && snapshot.TotalStored == 1 && snapshot.TotalFailed == 2,
                    "Partial storage error accounting.");
            }
            using (LogServer restarted = Server(folder))
            using (RemoteLogger logger = new RemoteLogger("127.0.0.1", restarted.Port, "web"))
            {
                Check(restarted.RecoveredBytes > 0 && new FileInfo(path).Length == completeLength,
                    "Restart did not discard only the partial suffix.");
                Check(logger.Log(LogLevel.INFO, "after recovery"), "Restart write failed.");
            }
            string[] lines = Lines(folder);
            Check(lines.Length == 2 && lines[0].EndsWith("whole record") && lines[1].EndsWith("after recovery"),
                "Recovery changed a complete record.");
        }

        private static void TestInvalidFilterAcknowledgement()
        {
            using (ScriptedPeer peer = new ScriptedPeer(delegate(ScriptedPeer state, TcpClient connection)
            {
                Protocol.ReadFrame(connection.GetStream());
                Protocol.WriteFrame(connection.GetStream(), Protocol.Text(MessageType.Response, "READY"));
                Protocol.ReadFrame(connection.GetStream());
                Protocol.WriteFrame(connection.GetStream(), Protocol.Text(MessageType.Response, "STORED"));
            }))
            using (AdminClient admin = new AdminClient("127.0.0.1", peer.Port, Token))
            {
                Throws<InvalidDataException>(delegate { admin.SetFilter("web", LogLevel.ERROR); });
                Throws<ObjectDisposedException>(delegate { admin.GetStatus(); });
            }
        }
    }
}
