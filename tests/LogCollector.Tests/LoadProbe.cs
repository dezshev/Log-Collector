using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using LogCollector.Client;
using LogCollector.Common;
using LogCollector.Server;

namespace LogCollector.Tests
{
    internal static partial class Program
    {
        // Длительная нагрузка запускается отдельно.
        private static int RunLoadProbe(string[] args)
        {
            int seconds;
            if (args.Length != 3 || args[0] != "--load" || !Int32.TryParse(args[1], out seconds) || seconds < 10 || seconds > 3600)
            { Console.Error.WriteLine("Usage: LogCollector.Tests.exe --load seconds(10..3600) output-directory"); return 2; }
            string output = Path.GetFullPath(args[2]);
            Directory.CreateDirectory(output);
            string folder = Folder();
            const int agentCount = 8;
            const long rotation = 262144;
            long[,] acknowledged = new long[5, 2];
            List<double> latencies = new List<double>();
            object metricsGate = new object();
            Exception failure = null;
            Stopwatch watch = new Stopwatch();
            Thread[] workers = new Thread[agentCount];
            ManualResetEvent ready = new ManualResetEvent(false);
            ManualResetEvent stop = new ManualResetEvent(false);
            LogServer server = null;
            try
            {
                server = new LogServer(IPAddress.Loopback, 0, folder, rotation, Token, 64);
                server.Start();
                using (AdminClient admin = new AdminClient("127.0.0.1", server.Port, Token))
                {
                    for (int i = 0; i < agentCount; i++)
                    {
                        int number = i;
                        workers[i] = new Thread(delegate()
                        {
                            try
                            {
                                using (RemoteLogger logger = new RemoteLogger("127.0.0.1", server.Port, "load-" + number))
                                {
                                    ready.WaitOne();
                                    long sequence = 0;
                                    string padding = new string('x', 192);
                                    while (!stop.WaitOne(0))
                                    {
                                        LogLevel level = (LogLevel)(sequence % 5);
                                        string text = number + ":" + sequence + ":" + padding;
                                        Stopwatch request = Stopwatch.StartNew();
                                        bool saved = logger.Log(level, text);
                                        request.Stop();
                                        lock (metricsGate)
                                        {
                                            acknowledged[(int)level, saved ? 0 : 1]++;
                                            // Храним первые 100000 измерений.
                                            if (latencies.Count < 100000) latencies.Add(request.Elapsed.TotalMilliseconds);
                                        }
                                        sequence++;
                                        Thread.Sleep(5); // Пауза задаёт равномерную нагрузку.
                                    }
                                }
                            }
                            catch (Exception error) { Interlocked.CompareExchange(ref failure, error, null); stop.Set(); }
                        });
                        workers[i].IsBackground = true;
                        workers[i].Start();
                    }
                    int changes = 0;
                    using (StreamWriter samples = new StreamWriter(Path.Combine(output, "load-samples.csv"), false, new UTF8Encoding(false)))
                    {
                        samples.WriteLine("elapsed_seconds,received,stored,filtered,failed,active_agents,private_bytes,threads");
                        watch.Start(); ready.Set();
                        while (watch.Elapsed.TotalSeconds < seconds && failure == null)
                        {
                            admin.SetFilter("load-" + (changes % agentCount), (LogLevel)(changes % 5)); changes++;
                            ServerStatus status = admin.GetSnapshot();
                            using (Process process = Process.GetCurrentProcess())
                            {
                                samples.WriteLine(String.Format(CultureInfo.InvariantCulture,
                                    "{0:F3},{1},{2},{3},{4},{5},{6},{7}", watch.Elapsed.TotalSeconds,
                                    status.TotalReceived, status.TotalStored, status.TotalFiltered, status.TotalFailed,
                                    status.ActiveConnections, process.PrivateMemorySize64, process.Threads.Count));
                            }
                            samples.Flush();
                            stop.WaitOne(1000);
                        }
                    }
                    stop.Set();
                    foreach (Thread worker in workers) Check(worker.Join(10000), "Load worker failed to finish.");
                    watch.Stop();
                    Check(failure == null, failure == null ? "" : failure.ToString());
                    ServerStatus final = admin.GetSnapshot();
                    string finalText = admin.GetStatus();
                    long total = 0;
                    for (int i = 0; i < 5; i++)
                    {
                        Check(final.Levels[i].Stored == acknowledged[i, 0] && final.Levels[i].Filtered == acknowledged[i, 1] &&
                            final.Levels[i].Failed == 0, "Acknowledgement totals differ from server at level " + i);
                        total += acknowledged[i, 0] + acknowledged[i, 1];
                    }
                    server.Dispose(); server = null;
                    HashSet<string> identifiers = new HashSet<string>(StringComparer.Ordinal);
                    long bytes = 0, lines = 0;
                    string[] segments = Directory.GetFiles(folder, "logs-*.log");
                    foreach (string segment in segments)
                    {
                        long length = new FileInfo(segment).Length;
                        Check(length <= rotation, "Rotation exceeded configured size."); bytes += length;
                        foreach (string line in File.ReadLines(segment, Encoding.UTF8))
                        {
                            string[] fields = line.Split('\t');
                            Check(fields.Length == 4, "Broken physical record.");
                            Check(identifiers.Add(fields[3]), "Duplicate stored message."); lines++;
                        }
                    }
                    Check(lines == final.TotalStored && segments.Length > 1 && final.TotalReceived == total,
                        "Stored files do not match accounting or rotation did not occur.");
                    latencies.Sort();
                    string report = String.Format(CultureInfo.InvariantCulture,
                        "# Протокол локального нагрузочного прогона\n\nДата отчёта: 05.10.2026. TCP loopback 127.0.0.1; сервер и 8 клиентов в одном процессе проверочной программы.\n\n" +
                        "- Environment.OSVersion (значение режима совместимости): {0}; CLR: {1}; разрядность процесса: {2}.\n" +
                        "- Длительность отправки и завершения: {3:F3} с; план: {4} с.\n" +
                        "- Агентов: 8; пауза после подтверждения: 5 мс; тело сообщения: 192 ASCII-символа + уникальные агент и номер.\n" +
                        "- Изменений порога: {5}; ротация: 262144 байт (уменьшенный тестовый предел).\n" +
                        "- Получено: {6}; сохранено: {7}; отфильтровано: {8}; ошибок хранения: {9}.\n" +
                        "- Средняя скорость подтверждённых операций: {10:F2} в секунду.\n" +
                        "- Выборка задержки: первые {11} ответов; p50: {12:F3} мс; p95: {13:F3} мс; максимум выборки: {14:F3} мс.\n" +
                        "- Файлов: {15}; байт: {16}; физических строк: {17}; повторных идентификаторов: 0.\n\n" +
                        "Проверка PASSED: подтверждения совпали со всеми пятью уровнями снимка, строки совпали с STORED, " +
                        "каждый сегмент в пределах ротации, идентификаторы сохранённых сообщений уникальны. " +
                        "График ресурсов относится ко всему процессу проверки, включая клиентов и сбор метрик. " +
                        "Это умеренная длительная локальная нагрузка, не предел производительности и не проверка между компьютерами.\n",
                        Environment.OSVersion, Environment.Version, Environment.Is64BitProcess ? 64 : 32,
                        watch.Elapsed.TotalSeconds, seconds, changes, total, final.TotalStored, final.TotalFiltered, final.TotalFailed,
                        total / watch.Elapsed.TotalSeconds, latencies.Count, Percentile(latencies, 0.50), Percentile(latencies, 0.95),
                        Percentile(latencies, 1.0), segments.Length, bytes, lines);
                    File.WriteAllText(Path.Combine(output, "LOAD_REPORT.md"), report, new UTF8Encoding(false));
                    File.WriteAllText(Path.Combine(output, "load-final-status.txt"), finalText, new UTF8Encoding(false));
                    Console.WriteLine(report);
                }
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
            finally
            {
                stop.Set(); ready.Set();
                if (server != null) server.Dispose();
                foreach (Thread worker in workers) if (worker != null) worker.Join(10000);
                ready.Dispose(); stop.Dispose();
                string prefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar + "logcollector-test-";
                if (Path.GetFullPath(folder).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) Directory.Delete(folder, true);
            }
        }

        private static double Percentile(List<double> sorted, double fraction)
        {
            if (sorted.Count == 0) throw new InvalidOperationException("No latency measurements.");
            return sorted[Math.Max(0, (int)Math.Ceiling(sorted.Count * fraction) - 1)];
        }
    }
}
