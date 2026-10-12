using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using LogCollector.Common;

namespace LogCollector.Server
{
    public sealed class LogServer : IDisposable
    {
        private sealed class Session
        {
            public TcpClient Client;
            public Thread Worker;
            public string Source;
        }

        private readonly object gate = new object();
        private readonly TcpListener listener;
        private readonly RotatingLogWriter writer;
        private readonly string adminToken;
        private readonly int maximumConnections;
        private readonly int frameTimeoutMilliseconds;
        private readonly Dictionary<TcpClient, Session> sessions = new Dictionary<TcpClient, Session>();
        private readonly Dictionary<string, LogLevel> filters = new Dictionary<string, LogLevel>(StringComparer.Ordinal);
        private readonly long[] received = new long[5], stored = new long[5], filtered = new long[5], failed = new long[5];
        private volatile bool running;
        private bool disposed;
        private Thread acceptThread;

        public LogServer(IPAddress address, int port, string directory, long maximumBytes, string adminToken, int maximumConnections,
            int frameTimeoutMilliseconds = 5000)
        {
            if (String.IsNullOrEmpty(adminToken) || Encoding.UTF8.GetByteCount(adminToken) > 1024)
                throw new ArgumentException("Administrator token required (at most 1024 bytes).", "adminToken");
            if (maximumConnections < 1 || maximumConnections > 64)
                throw new ArgumentOutOfRangeException("maximumConnections", "Allowed range: 1-64.");
            if (frameTimeoutMilliseconds < 1 || frameTimeoutMilliseconds > 60000)
                throw new ArgumentOutOfRangeException("frameTimeoutMilliseconds");
            this.adminToken = adminToken;
            this.maximumConnections = maximumConnections;
            this.frameTimeoutMilliseconds = frameTimeoutMilliseconds;
            listener = new TcpListener(address, port);
            writer = new RotatingLogWriter(directory, maximumBytes);
        }

        public int Port { get { return ((IPEndPoint)listener.LocalEndpoint).Port; } }
        public long RecoveredBytes { get { return writer.RecoveredBytes; } }

        public void Start()
        {
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException("LogServer");
                if (running) throw new InvalidOperationException("Server already started.");
                listener.Start();
                running = true;
                acceptThread = new Thread(AcceptLoop);
                acceptThread.IsBackground = true;
                acceptThread.Name = "log-accept";
                acceptThread.Start();
            }
        }

        private void AcceptLoop()
        {
            while (running)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch (SocketException error)
                {
                    if (!running) return;
                    Console.Error.WriteLine("Accept failed: " + error.SocketErrorCode);
                    Thread.Sleep(100);
                    continue;
                }
                catch (ObjectDisposedException) { return; }
                lock (gate)
                {
                    if (!running || sessions.Count >= maximumConnections) { client.Close(); continue; }
                    Session session = new Session { Client = client };
                    session.Worker = new Thread(delegate() { HandleSession(session); });
                    session.Worker.IsBackground = true;
                    session.Worker.Name = "log-session";
                    sessions.Add(client, session);
                    session.Worker.Start();
                }
            }
        }

        private void HandleSession(Session session)
        {
            NetworkStream stream = null;
            try
            {
                session.Client.NoDelay = true;
                session.Client.ReceiveTimeout = 5000; // Ограничиваем ожидание приветствия.
                session.Client.SendTimeout = 5000;
                stream = session.Client.GetStream();
                Frame hello = Protocol.ReadFrame(stream, frameTimeoutMilliseconds);
                if (hello == null) return;
                bool administrator;
                if (hello.Type == MessageType.HelloAgent)
                {
                    string source = Protocol.ReadText(hello);
                    LogRecord.ValidateSource(source);
                    lock (gate) session.Source = source;
                    administrator = false;
                }
                else if (hello.Type == MessageType.HelloAdmin)
                {
                    if (Protocol.ReadText(hello) != adminToken) throw new InvalidDataException("Invalid administrator token.");
                    administrator = true;
                }
                else throw new InvalidDataException("Role greeting required.");
                Reply(stream, "READY");
                stream.ReadTimeout = Timeout.Infinite; // Срок чтения задаётся через поток.

                Frame frame;
                while (running && (frame = ReadEstablishedFrame(stream)) != null)
                {
                    if (!administrator && frame.Type == MessageType.Log)
                    {
                        LogRecord record = Protocol.DecodeLog(frame);
                        if (record.Source != session.Source) throw new InvalidDataException("Source differs from greeting.");
                        Reply(stream, ProcessLog(record) ? "STORED" : "FILTERED");
                    }
                    else if (administrator && frame.Type == MessageType.SetFilter)
                    {
                        string source;
                        LogLevel level;
                        Protocol.DecodeFilter(frame, out source, out level);
                        lock (gate)
                        {
                            if (!filters.ContainsKey(source) && filters.Count >= 256)
                                throw new InvalidDataException("At most 256 configured sources are supported.");
                            filters[source] = level;
                        }
                        Reply(stream, "UPDATED");
                    }
                    else if (administrator && frame.Type == MessageType.GetStatus && frame.Body.Length == 0)
                        Reply(stream, GetStatus());
                    else throw new InvalidDataException("Command is not allowed for this role.");
                }
            }
            catch (Exception error)
            {
                // Ошибка клиента не останавливает сервер.
                if (!(error is IOException) && !(error is InvalidDataException) && !(error is SocketException) && !(error is ArgumentException) &&
                    !(error is ObjectDisposedException) && !(error is UnauthorizedAccessException))
                    Console.Error.WriteLine("Session failure: " + error.GetType().Name + ": " + error.Message);
                if (running && stream != null)
                {
                    try { Protocol.WriteFrame(stream, Protocol.Text(MessageType.Error, "Request failed: " + error.GetType().Name)); }
                    catch (IOException) { }
                    catch (ObjectDisposedException) { }
                    catch (SocketException) { }
                }
            }
            finally
            {
                session.Client.Close();
                lock (gate) sessions.Remove(session.Client);
            }
        }

        private static void Reply(Stream stream, string text)
        { Protocol.WriteFrame(stream, Protocol.Text(MessageType.Response, text)); }

        private Frame ReadEstablishedFrame(NetworkStream stream)
        {
            int type = stream.ReadByte(); // Срок отсчитываем с первого байта кадра.
            if (type < 0) return null;
            try { return Protocol.ReadFrameAfterType(stream, type, frameTimeoutMilliseconds); }
            finally
            {
                try { stream.ReadTimeout = Timeout.Infinite; }
                catch (ObjectDisposedException) { }
                catch (SocketException) { }
                catch (IOException) { }
            }
        }

        private bool ProcessLog(LogRecord record)
        {
            lock (gate)
            {
                int level = (int)record.Level;
                received[level]++;
                LogLevel minimum;
                if (!filters.TryGetValue(record.Source, out minimum)) minimum = LogLevel.DEBUG;
                if ((int)record.Level < (int)minimum) { filtered[level]++; return false; }
                try { writer.Append(record); stored[level]++; return true; }
                catch (Exception error)
                {
                    failed[level]++;
                    Console.Error.WriteLine("Log storage failed: " + error.GetType().Name + ". Fix storage and restart the server.");
                    throw;
                }
            }
        }

        // Снимок и счётчики читаем под общей блокировкой.
        private string GetStatus()
        {
            lock (gate)
            {
                StringBuilder result = new StringBuilder("LEVEL\tRECEIVED\tSTORED\tFILTERED\tFAILED\n");
                for (int i = 0; i < 5; i++)
                    result.AppendFormat(System.Globalization.CultureInfo.InvariantCulture,
                        "{0}\t{1}\t{2}\t{3}\t{4}\n", (LogLevel)i, received[i], stored[i], filtered[i], failed[i]);
                result.Append("SOURCE\tMINIMUM\tACTIVE_CONNECTIONS\n");
                SortedDictionary<string, int> counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
                foreach (Session session in sessions.Values)
                {
                    if (session.Source == null) continue;
                    int count;
                    counts.TryGetValue(session.Source, out count);
                    counts[session.Source] = count + 1;
                }
                foreach (string source in filters.Keys)
                    if (!counts.ContainsKey(source)) counts[source] = 0;
                foreach (KeyValuePair<string, int> entry in counts)
                {
                    LogLevel minimum;
                    if (!filters.TryGetValue(entry.Key, out minimum)) minimum = LogLevel.DEBUG;
                    result.AppendFormat(System.Globalization.CultureInfo.InvariantCulture,
                        "{0}\t{1}\t{2}\n", entry.Key, minimum, entry.Value);
                }
                return result.ToString();
            }
        }

        public void Dispose()
        {
            List<Session> pending;
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                running = false;
                listener.Stop();
                pending = new List<Session>(sessions.Values);
            }
            foreach (Session session in pending) session.Client.Close();
            if (acceptThread != null) acceptThread.Join();
            foreach (Session session in pending) session.Worker.Join();
            writer.Dispose();
        }
    }
}
