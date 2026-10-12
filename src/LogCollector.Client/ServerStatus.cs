using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using LogCollector.Common;

namespace LogCollector.Client
{
    public sealed class LevelStatistics
    {
        public LogLevel Level { get; private set; }
        public long Received { get; private set; }
        public long Stored { get; private set; }
        public long Filtered { get; private set; }
        public long Failed { get; private set; }
        internal LevelStatistics(LogLevel level, long received, long stored, long filtered, long failed)
        { Level = level; Received = received; Stored = stored; Filtered = filtered; Failed = failed; }
    }

    public sealed class SourceStatus
    {
        public string Name { get; private set; }
        public LogLevel Minimum { get; private set; }
        public int ActiveConnections { get; private set; }
        internal SourceStatus(string name, LogLevel minimum, int active)
        { Name = name; Minimum = minimum; ActiveConnections = active; }
    }

    // Проверяем снимок до его отображения.
    public sealed class ServerStatus
    {
        public ReadOnlyCollection<LevelStatistics> Levels { get; private set; }
        public ReadOnlyCollection<SourceStatus> Sources { get; private set; }
        public long TotalReceived { get; private set; }
        public long TotalStored { get; private set; }
        public long TotalFiltered { get; private set; }
        public long TotalFailed { get; private set; }
        public int ActiveConnections { get; private set; }

        private ServerStatus(List<LevelStatistics> levels, List<SourceStatus> sources)
        {
            Levels = levels.AsReadOnly();
            Sources = sources.AsReadOnly();
            checked
            {
                foreach (LevelStatistics level in levels)
                { TotalReceived += level.Received; TotalStored += level.Stored; TotalFiltered += level.Filtered; TotalFailed += level.Failed; }
                foreach (SourceStatus source in sources) ActiveConnections += source.ActiveConnections;
            }
            if (ActiveConnections > 64) throw new InvalidDataException("Too many active connections in status.");
        }

        public static ServerStatus Parse(string text)
        {
            if (text == null) throw new InvalidDataException("Missing status.");
            try
            {
                using (StringReader reader = new StringReader(text))
                {
                    if (reader.ReadLine() != "LEVEL\tRECEIVED\tSTORED\tFILTERED\tFAILED")
                        throw new InvalidDataException("Invalid statistics header.");
                    List<LevelStatistics> levels = new List<LevelStatistics>();
                    for (int i = 0; i < 5; i++)
                    {
                        string[] fields = Fields(reader.ReadLine(), 5);
                        LogLevel level = ParseLevel(fields[0]);
                        if ((int)level != i) throw new InvalidDataException("Incorrect order of levels.");
                        long received = Number(fields[1]), stored = Number(fields[2]), filtered = Number(fields[3]), failed = Number(fields[4]);
                        if (stored > received || filtered > received - stored || failed != received - stored - filtered)
                            throw new InvalidDataException("Inconsistent statistics.");
                        levels.Add(new LevelStatistics(level, received, stored, filtered, failed));
                    }
                    if (reader.ReadLine() != "SOURCE\tMINIMUM\tACTIVE_CONNECTIONS")
                        throw new InvalidDataException("Invalid sources header.");
                    List<SourceStatus> sources = new List<SourceStatus>();
                    HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        string[] fields = Fields(line, 3);
                        LogRecord.ValidateSource(fields[0]);
                        if (!names.Add(fields[0])) throw new InvalidDataException("Duplicate source in status.");
                        long active = Number(fields[2]);
                        if (active > 64 || sources.Count >= 320) throw new InvalidDataException("Invalid source limit.");
                        sources.Add(new SourceStatus(fields[0], ParseLevel(fields[1]), (int)active));
                    }
                    return new ServerStatus(levels, sources);
                }
            }
            catch (ArgumentException error) { throw new InvalidDataException("Invalid status fields.", error); }
            catch (OverflowException error) { throw new InvalidDataException("Status totals overflow.", error); }
        }

        private static string[] Fields(string line, int expected)
        {
            if (line == null) throw new InvalidDataException("Truncated status.");
            string[] fields = line.Split('\t');
            if (fields.Length != expected) throw new InvalidDataException("Invalid number of status fields.");
            return fields;
        }

        private static long Number(string field)
        {
            long value;
            if (!Int64.TryParse(field, NumberStyles.None, CultureInfo.InvariantCulture, out value))
                throw new InvalidDataException("Invalid nonnegative integer in status.");
            return value;
        }

        private static LogLevel ParseLevel(string value)
        {
            LogLevel level;
            if (!Enum.TryParse<LogLevel>(value, false, out level) || !Enum.IsDefined(typeof(LogLevel), level) || level.ToString() != value)
                throw new InvalidDataException("Invalid status level.");
            return level;
        }
    }
}
