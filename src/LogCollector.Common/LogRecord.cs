using System;
using System.Text;

namespace LogCollector.Common
{
    public enum LogLevel : byte { DEBUG = 0, INFO = 1, WARN = 2, ERROR = 3, CRITICAL = 4 }

    public sealed class LogRecord
    {
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
        public DateTime TimestampUtc { get; private set; }
        public LogLevel Level { get; private set; }
        public string Source { get; private set; }
        public string Message { get; private set; }

        public LogRecord(DateTime timestampUtc, LogLevel level, string source, string message)
        {
            if (timestampUtc.Kind != DateTimeKind.Utc)
                throw new ArgumentException("Timestamp must have UTC kind.", "timestampUtc");
            ValidateLevel(level);
            ValidateSource(source);
            if (message == null || StrictUtf8.GetByteCount(message) > 65536)
                throw new ArgumentException("Message must be non-null and at most 65536 UTF-8 bytes.", "message");
            TimestampUtc = timestampUtc;
            Level = level;
            Source = source;
            Message = message;
        }

        public static void ValidateLevel(LogLevel level)
        {
            if (!Enum.IsDefined(typeof(LogLevel), level))
                throw new ArgumentException("Unknown log level.", "level");
        }

        public static void ValidateSource(string source)
        {
            if (String.IsNullOrWhiteSpace(source) || source.Length > 128)
                throw new ArgumentException("Source must contain 1-128 characters.", "source");
            foreach (char c in source)
                if (Char.IsControl(c)) throw new ArgumentException("Control characters in source.", "source");
            StrictUtf8.GetByteCount(source);
        }
    }
}
