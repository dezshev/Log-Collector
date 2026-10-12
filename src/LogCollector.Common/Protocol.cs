using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace LogCollector.Common
{
    public enum MessageType : byte
    {
        HelloAgent = 1, Log = 2, HelloAdmin = 3, SetFilter = 4, GetStatus = 5,
        Response = 100, Error = 101
    }

    public sealed class Frame
    {
        public MessageType Type { get; private set; }
        public byte[] Body { get; private set; }
        public Frame(MessageType type, byte[] body) { Type = type; Body = body; }
    }

    // Длину каждого поля передаём явно.
    public static class Protocol
    {
        public const int MaxFrameBytes = 262144;
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        public static Frame ReadFrame(Stream stream)
        {
            return ReadFrame(stream, 0);
        }

        public static Frame ReadFrame(Stream stream, int timeoutMilliseconds)
        {
            if (timeoutMilliseconds < 0) throw new ArgumentOutOfRangeException("timeoutMilliseconds");
            Stopwatch deadline = timeoutMilliseconds == 0 ? null : Stopwatch.StartNew();
            if (deadline != null && stream.CanTimeout) stream.ReadTimeout = timeoutMilliseconds;
            int type = stream.ReadByte();
            return ReadFrameBody(stream, type, deadline, timeoutMilliseconds);
        }

        // Срок общий для всего начатого кадра.
        public static Frame ReadFrameAfterType(Stream stream, int type, int timeoutMilliseconds)
        {
            if (timeoutMilliseconds <= 0) throw new ArgumentOutOfRangeException("timeoutMilliseconds");
            return ReadFrameBody(stream, type, Stopwatch.StartNew(), timeoutMilliseconds);
        }

        private static Frame ReadFrameBody(Stream stream, int type, Stopwatch deadline, int timeoutMilliseconds)
        {
            if (type < 0) return null; // Конец потока допустим только между кадрами.
            if (!Enum.IsDefined(typeof(MessageType), (byte)type))
                throw new InvalidDataException("Unknown frame type.");
            byte[] sizeBytes = ReadExactlyCore(stream, 4, deadline, timeoutMilliseconds);
            int size = sizeBytes[0] | (sizeBytes[1] << 8) | (sizeBytes[2] << 16) | (sizeBytes[3] << 24);
            if (size < 0 || size > MaxFrameBytes)
                throw new InvalidDataException("Invalid frame length.");
            return new Frame((MessageType)type, ReadExactlyCore(stream, size, deadline, timeoutMilliseconds));
        }

        public static void WriteFrame(Stream stream, Frame frame)
        {
            if (frame == null || frame.Body == null || frame.Body.Length > MaxFrameBytes ||
                !Enum.IsDefined(typeof(MessageType), frame.Type))
                throw new InvalidDataException("Invalid outgoing frame.");
            int size = frame.Body.Length;
            byte[] header = { (byte)frame.Type, (byte)size, (byte)(size >> 8), (byte)(size >> 16), (byte)(size >> 24) };
            stream.Write(header, 0, header.Length);
            stream.Write(frame.Body, 0, size);
            stream.Flush();
        }

        public static byte[] ReadExactly(Stream stream, int count)
        {
            return ReadExactlyCore(stream, count, null, 0);
        }

        private static byte[] ReadExactlyCore(Stream stream, int count, Stopwatch deadline, int timeoutMilliseconds)
        {
            if (count < 0 || count > MaxFrameBytes) throw new ArgumentOutOfRangeException("count");
            byte[] bytes = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                if (deadline != null)
                {
                    long remaining = timeoutMilliseconds - deadline.ElapsedMilliseconds;
                    if (remaining <= 0) throw new IOException("Frame deadline expired.");
                    if (stream.CanTimeout) stream.ReadTimeout = (int)remaining;
                }
                int read = stream.Read(bytes, offset, count - offset);
                if (read == 0) throw new EndOfStreamException("Connection ended inside a frame.");
                offset += read;
            }
            if (deadline != null && deadline.ElapsedMilliseconds >= timeoutMilliseconds)
                throw new IOException("Frame deadline expired.");
            return bytes;
        }

        private static Frame Pack(MessageType type, Action<BinaryWriter> write)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                using (BinaryWriter writer = new BinaryWriter(stream, Utf8, true)) write(writer);
                byte[] body = stream.ToArray();
                if (body.Length > MaxFrameBytes) throw new InvalidDataException("Payload too large.");
                return new Frame(type, body);
            }
        }

        public static void WriteString(BinaryWriter writer, string value)
        {
            byte[] bytes = Utf8.GetBytes(value);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        public static string ReadString(BinaryReader reader)
        {
            int length = reader.ReadInt32();
            if (length < 0 || length > MaxFrameBytes || length > reader.BaseStream.Length - reader.BaseStream.Position)
                throw new InvalidDataException("Invalid string length.");
            return Utf8.GetString(ReadExactly(reader.BaseStream, length));
        }

        public static void RequireEnd(BinaryReader reader)
        {
            if (reader.BaseStream.Position != reader.BaseStream.Length)
                throw new InvalidDataException("Unexpected trailing payload.");
        }

        public static Frame Text(MessageType type, string text)
        {
            return Pack(type, delegate(BinaryWriter writer) { WriteString(writer, text); });
        }

        public static string ReadText(Frame frame)
        {
            using (BinaryReader reader = new BinaryReader(new MemoryStream(frame.Body), Utf8))
            {
                string text = ReadString(reader);
                RequireEnd(reader);
                return text;
            }
        }

        public static Frame EncodeLog(LogRecord record)
        {
            return Pack(MessageType.Log, delegate(BinaryWriter writer)
            {
                writer.Write(record.TimestampUtc.Ticks);
                writer.Write((byte)record.Level);
                WriteString(writer, record.Source);
                WriteString(writer, record.Message);
            });
        }

        public static LogRecord DecodeLog(Frame frame)
        {
            using (BinaryReader reader = new BinaryReader(new MemoryStream(frame.Body), Utf8))
            {
                DateTime time = new DateTime(reader.ReadInt64(), DateTimeKind.Utc);
                LogLevel level = (LogLevel)reader.ReadByte();
                string source = ReadString(reader);
                string message = ReadString(reader);
                RequireEnd(reader);
                return new LogRecord(time, level, source, message);
            }
        }

        public static Frame EncodeFilter(string source, LogLevel level)
        {
            LogRecord.ValidateSource(source);
            LogRecord.ValidateLevel(level);
            return Pack(MessageType.SetFilter, delegate(BinaryWriter writer)
            { WriteString(writer, source); writer.Write((byte)level); });
        }

        public static void DecodeFilter(Frame frame, out string source, out LogLevel level)
        {
            using (BinaryReader reader = new BinaryReader(new MemoryStream(frame.Body), Utf8))
            {
                source = ReadString(reader);
                level = (LogLevel)reader.ReadByte();
                RequireEnd(reader);
                LogRecord.ValidateSource(source);
                LogRecord.ValidateLevel(level);
            }
        }
    }
}
