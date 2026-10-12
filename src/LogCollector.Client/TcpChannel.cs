using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using LogCollector.Common;

namespace LogCollector.Client
{
    internal sealed class TcpChannel : IDisposable
    {
        private readonly TcpClient client;
        private readonly NetworkStream stream;
        private readonly object gate = new object();
        private int disposed;

        public TcpChannel(string host, int port)
        {
            client = new TcpClient();
            try
            {
                IAsyncResult result = client.BeginConnect(host, port, null, null);
                using (System.Threading.WaitHandle handle = result.AsyncWaitHandle)
                {
                    if (!handle.WaitOne(5000)) throw new TimeoutException("Connection timed out.");
                    client.EndConnect(result);
                }
                client.NoDelay = true;
                client.ReceiveTimeout = 5000;
                client.SendTimeout = 5000;
                stream = client.GetStream();
            }
            catch { client.Close(); throw; }
        }

        public string Exchange(Frame frame)
        {
            lock (gate)
            {
                if (Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException("TcpChannel");
                try
                {
                    Protocol.WriteFrame(stream, frame);
                    Frame response = Protocol.ReadFrame(stream, 5000);
                    if (response == null) throw new EndOfStreamException("Server closed the connection.");
                    if (response.Type != MessageType.Response && response.Type != MessageType.Error)
                        throw new InvalidDataException("Unexpected server response.");
                    string text = Protocol.ReadText(response);
                    if (response.Type == MessageType.Error) throw new IOException("Server: " + text);
                    return text;
                }
                catch { Dispose(); throw; }
            }
        }

        public void Dispose()
        {
            // Закрытие сокета прерывает ожидание ответа.
            // Не ждём сетевую блокировку при отключении.
            if (Interlocked.Exchange(ref disposed, 1) == 0) client.Close();
        }
    }
}
