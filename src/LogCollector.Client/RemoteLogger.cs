using System;
using System.IO;
using LogCollector.Common;

namespace LogCollector.Client
{
    public sealed class RemoteLogger : IDisposable
    {
        private readonly string source;
        private readonly TcpChannel channel;

        public RemoteLogger(string host, int port, string source)
        {
            LogRecord.ValidateSource(source);
            this.source = source;
            channel = new TcpChannel(host, port);
            try
            {
                if (channel.Exchange(Protocol.Text(MessageType.HelloAgent, source)) != "READY")
                    throw new InvalidDataException("Invalid agent handshake.");
            }
            catch { channel.Dispose(); throw; }
        }

        // Результат отличает запись от отсеивания.
        public bool Log(LogLevel level, string message)
        {
            return Log(new LogRecord(DateTime.UtcNow, level, source, message));
        }

        public bool Log(LogRecord record)
        {
            if (record == null || record.Source != source)
                throw new ArgumentException("Record source differs from the registered source.", "record");
            string result = channel.Exchange(Protocol.EncodeLog(record));
            if (result == "STORED") return true;
            if (result == "FILTERED") return false;
            channel.Dispose();
            throw new InvalidDataException("Invalid log acknowledgement.");
        }

        public void Dispose() { channel.Dispose(); }
    }
}
