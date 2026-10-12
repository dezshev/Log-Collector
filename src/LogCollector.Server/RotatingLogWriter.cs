using System;
using System.Globalization;
using System.IO;
using System.Text;
using LogCollector.Common;

namespace LogCollector.Server
{
    public sealed class RotatingLogWriter : IDisposable
    {
        private readonly string directory;
        private readonly long maximumBytes;
        private readonly object gate = new object();
        private FileStream stream;
        private int index;
        private bool disposed;
        private bool faulted;
        public long RecoveredBytes { get; private set; }
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        public RotatingLogWriter(string directory, long maximumBytes)
        {
            if (maximumBytes <= 0) throw new ArgumentOutOfRangeException("maximumBytes");
            this.directory = directory;
            this.maximumBytes = maximumBytes;
            Directory.CreateDirectory(directory);
            // После перезапуска продолжаем последний журнал.
            foreach (string path in Directory.GetFiles(directory, "logs-*.log"))
            {
                int parsed;
                string name = Path.GetFileNameWithoutExtension(path);
                if (Int32.TryParse(name.Substring(5), out parsed) && parsed > index) index = parsed;
            }
            if (index == 0) index = 1;
            OpenCurrent();
        }

        private void OpenCurrent()
        {
            string name = "logs-" + index.ToString("D6", CultureInfo.InvariantCulture) + ".log";
            FileStream candidate = new FileStream(Path.Combine(directory, name), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            try
            {
                long end = candidate.Length;
                if (end > 0)
                {
                    candidate.Position = end - 1;
                    if (candidate.ReadByte() != '\n')
                    {
                        // Удаляем только незавершённый хвост.
                        long validEnd = 0;
                        byte[] buffer = new byte[4096];
                        long cursor = end;
                        bool found = false;
                        while (cursor > 0 && !found)
                        {
                            int count = (int)Math.Min(cursor, buffer.Length);
                            cursor -= count;
                            candidate.Position = cursor;
                            int offset = 0;
                            while (offset < count)
                            {
                                int read = candidate.Read(buffer, offset, count - offset);
                                if (read == 0) throw new EndOfStreamException("Log changed during recovery.");
                                offset += read;
                            }
                            for (int i = count - 1; i >= 0; i--)
                                if (buffer[i] == '\n') { validEnd = cursor + i + 1; found = true; break; }
                        }
                        candidate.SetLength(validEnd);
                        candidate.Flush(true);
                        RecoveredBytes += end - validEnd;
                    }
                }
                candidate.Position = candidate.Length;
                stream = candidate;
            }
            catch { candidate.Dispose(); throw; }
        }

        private static string Escape(string value)
        {
            // Экранируем разделители, чтобы запись занимала одну строку.
            StringBuilder result = new StringBuilder();
            foreach (char c in value)
            {
                if (c == '\\') result.Append("\\\\");
                else if (c == '\t') result.Append("\\t");
                else if (c == '\r') result.Append("\\r");
                else if (c == '\n') result.Append("\\n");
                else if (Char.IsControl(c)) result.Append("\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else result.Append(c);
            }
            return result.ToString();
        }

        public void Append(LogRecord record)
        {
            string line = record.TimestampUtc.ToString("o", CultureInfo.InvariantCulture) + "\t" + record.Level +
                "\t" + Escape(record.Source) + "\t" + Escape(record.Message) + "\n";
            byte[] bytes = Utf8.GetBytes(line);
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException("RotatingLogWriter");
                if (faulted) throw new IOException("Log writer is faulted; fix storage and restart the server.");
                try
                {
                    if (stream == null) OpenCurrent();
                    if (stream.Length > 0 && (stream.Length >= maximumBytes || bytes.Length > maximumBytes - stream.Length))
                    {
                        stream.Dispose();
                        stream = null;
                        index = checked(index + 1);
                        OpenCurrent();
                    }
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush();
                }
                catch
                {
                    faulted = true; // После частичной ошибки больше не пишем.
                    if (stream != null) { stream.Dispose(); stream = null; }
                    throw;
                }
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (!disposed) { disposed = true; if (stream != null) stream.Dispose(); }
            }
        }
    }
}
