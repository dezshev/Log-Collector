using System;
using System.IO;
using LogCollector.Common;

namespace LogCollector.Client
{
    public sealed class AdminClient : IDisposable
    {
        private readonly TcpChannel channel;

        public AdminClient(string host, int port, string token)
        {
            if (String.IsNullOrEmpty(token)) throw new ArgumentException("Administrator token required.", "token");
            channel = new TcpChannel(host, port);
            try
            {
                if (channel.Exchange(Protocol.Text(MessageType.HelloAdmin, token)) != "READY")
                    throw new InvalidDataException("Invalid administrator handshake.");
            }
            catch { channel.Dispose(); throw; }
        }

        public void SetFilter(string source, LogLevel minimum)
        {
            if (channel.Exchange(Protocol.EncodeFilter(source, minimum)) != "UPDATED")
            {
                channel.Dispose();
                throw new InvalidDataException("Invalid filter acknowledgement.");
            }
        }

        public string GetStatus()
        {
            return channel.Exchange(new Frame(MessageType.GetStatus, new byte[0]));
        }

        public ServerStatus GetSnapshot()
        {
            try { return ServerStatus.Parse(GetStatus()); }
            catch { channel.Dispose(); throw; }
        }

        public void Dispose() { channel.Dispose(); }
    }
}
