using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LogCollector.AdminWpf;
using LogCollector.Client;
using LogCollector.Common;
using LogCollector.Server;

namespace LogCollector.UiTests
{
    internal static class Program
    {
        private const string Token = "test-only-ui-token";
        private static int passed, failed;
        private static string screenshotDirectory;
        private static readonly List<string> temporary = new List<string>();

        [STAThread]
        private static int Main(string[] args)
        {
            Application app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            screenshotDirectory = args.Length == 0 ? null : Path.GetFullPath(args[0]);
            if (screenshotDirectory != null) Directory.CreateDirectory(screenshotDirectory);
            Run("R11 connect, three agents, filter, statistics and resizing", TestFullWindow);
            Run("R11 invalid inputs do not start a connection", TestValidation);
            Run("R11 unavailable server and wrong token", TestConnectionFailures);
            Run("R11 server loss clears current data", TestServerLoss);
            Run("R11 delayed server leaves UI message loop responsive", TestResponsiveness);
            Run("R11 cancel connection discards a late result", TestCancel);
            Run("R11 close during a blocked request returns immediately", TestClose);
            Run("R11 malformed snapshot is never displayed", TestInvalidSnapshot);
            Run("R11 periodic refresh receives new records", TestAutoRefresh);
            Run("R11 source selection and invalid source validation", TestSourceSelection);
            Run("R11 disconnect interrupts a refresh and ignores its result", TestDisconnectPending);
            Run("R11 reconnect starts with a fresh server snapshot", TestReconnect);
            Console.WriteLine("UI RESULT: {0} passed, {1} failed", passed, failed);
            foreach (string folder in temporary)
            {
                string prefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar + "logcollector-ui-";
                if (!Path.GetFullPath(folder).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unexpected test cleanup path.");
                Directory.Delete(folder, true);
            }
            app.Shutdown();
            return failed == 0 ? 0 : 1;
        }

        private static void Run(string name, Action test)
        {
            try { test(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error); }
        }

        private static void Check(bool value, string message)
        { if (!value) throw new Exception(message); }

        private static void PumpUntil(Func<bool> condition, int milliseconds)
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (!condition())
            {
                if (clock.ElapsedMilliseconds > milliseconds) throw new Exception("UI state did not arrive within deadline.");
                Pump(); Thread.Sleep(10);
            }
            Pump();
        }

        private static void Pump()
        {
            DispatcherFrame frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate { frame.Continue = false; }));
            Dispatcher.PushFrame(frame);
        }

        private static T Find<T>(MainWindow root, string name) where T : FrameworkElement
        {
            T control = root.FindControl<T>(name);
            Check(control != null, "Missing UI control: " + name);
            return control;
        }

        private static MainWindow Form(int port)
        {
            MainWindow form = new MainWindow();
            form.WindowStartupLocation = WindowStartupLocation.Manual;
            form.Left = -20000; form.Top = -20000;
            form.ShowInTaskbar = false;
            Find<PasswordBox>(form, "TokenBox").Password = Token;
            Find<TextBox>(form, "PortBox").Text = port.ToString();
            Find<CheckBox>(form, "AutoRefreshBox").IsChecked = false;
            form.Show(); Pump();
            return form;
        }

        private static string Folder()
        {
            string path = Path.Combine(Path.GetTempPath(), "logcollector-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path); temporary.Add(path); return path;
        }

        private static LogServer Server()
        {
            LogServer server = new LogServer(IPAddress.Loopback, 0, Folder(), 1000000, Token, 64);
            server.Start(); return server;
        }

        private static void Connect(MainWindow form)
        {
            Find<Button>(form, "ConnectButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(delegate { return Find<Button>(form, "RefreshButton").IsEnabled; }, 5000);
        }

        private static string Minimum(MainWindow form, string name)
        {
            foreach (SourceStatus row in Find<DataGrid>(form, "SourceGrid").Items)
                if (row.Name == name) return row.Minimum.ToString();
            return null;
        }

        private static void Save(MainWindow form, string name)
        {
            if (screenshotDirectory == null) return;
            form.UpdateLayout(); Pump();
            FrameworkElement view = (FrameworkElement)form.Content;
            RenderTargetBitmap image = new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth), (int)Math.Ceiling(view.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            image.Render(view);
            PngBitmapEncoder encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using (Stream stream = File.Create(Path.Combine(screenshotDirectory, name))) encoder.Save(stream);
        }

        private static void TestFullWindow()
        {
            using (LogServer server = Server())
            using (RemoteLogger web = new RemoteLogger("127.0.0.1", server.Port, "WebServer"))
            using (RemoteLogger db = new RemoteLogger("127.0.0.1", server.Port, "Database"))
            using (RemoteLogger service = new RemoteLogger("127.0.0.1", server.Port, "WorkerService"))
            using (MainWindow form = Form(server.Port))
            {
                foreach (LogLevel level in Enum.GetValues(typeof(LogLevel)))
                { web.Log(level, "web"); db.Log(level, "db"); service.Log(level, "worker"); }
                Connect(form);
                Check(Find<DataGrid>(form, "SourceGrid").Items.Count == 3 && Find<DataGrid>(form, "LevelGrid").Items.Count == 5,
                    "Three sources / five levels missing.");
                Find<TextBox>(form, "SourceBox").Text = "WebServer";
                Find<ComboBox>(form, "MinimumBox").SelectedItem = LogLevel.ERROR;
                Find<Button>(form, "ApplyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(delegate { return Find<Button>(form, "ApplyButton").IsEnabled && Minimum(form, "WebServer") == "ERROR"; }, 5000);
                Check(!web.Log(LogLevel.INFO, "filtered") && web.Log(LogLevel.CRITICAL, "retained"), "UI filter not applied to active agent.");
                Find<Button>(form, "RefreshButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(delegate { return Find<Button>(form, "RefreshButton").IsEnabled; }, 5000);
                Check(((LevelStatistics)Find<DataGrid>(form, "LevelGrid").Items[1]).Filtered.ToString() == "1", "Filtered count incorrect.");
                Check(Find<TextBlock>(form, "ReceivedValue").Text == "17", "Total does not match 17 records.");
                Check(!Find<TextBox>(form, "EventBox").Text.Contains(Token), "Secret token appeared in history.");
                Save(form, "admin-connected.png");
                for (int eventIndex = 0; eventIndex < 24; eventIndex++)
                {
                    LogLevel eventLevel = (LogLevel)(eventIndex % 5);
                    Find<ComboBox>(form, "MinimumBox").SelectedItem = eventLevel;
                    Find<Button>(form, "ApplyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    PumpUntil(delegate { return Find<Button>(form, "ApplyButton").IsEnabled && Minimum(form, "WebServer") == eventLevel.ToString(); }, 5000);
                }
                Find<TabControl>(form, "MainTabs").SelectedIndex = 1; Pump();
                Save(form, "admin-events.png");
                Find<TabControl>(form, "MainTabs").SelectedIndex = 0; Pump();
                form.Width = 1040; form.Height = 780; Pump();
                Check(Find<Button>(form, "ApplyButton").ActualWidth >= 130, "Apply button clipped.");
                Check(Find<DataGrid>(form, "LevelGrid").ActualHeight >= 208, "Not all five levels visible at minimum size.");
                Save(form, "admin-minimum-size.png");
                Find<Button>(form, "ConnectButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(Find<DataGrid>(form, "LevelGrid").Items.Count == 0 && Find<DataGrid>(form, "SourceGrid").Items.Count == 0,
                    "Disconnected UI displays stale data.");
                form.Close();
            }
        }

        private static void TestValidation()
        {
            using (MainWindow form = Form(5050))
            {
                Find<TextBox>(form, "HostBox").Text = " ";
                Find<Button>(form, "ConnectButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(Find<TextBlock>(form, "ConnectionLabel").Text.Contains("адрес") && Find<TextBox>(form, "HostBox").IsEnabled, "Empty address not rejected locally.");
                Find<TextBox>(form, "HostBox").Text = "127.0.0.1";
                Find<PasswordBox>(form, "TokenBox").Password = String.Empty;
                Find<Button>(form, "ConnectButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(Find<TextBlock>(form, "ConnectionLabel").Text.Contains("токен") && Find<TextBox>(form, "HostBox").IsEnabled, "Missing token not rejected locally.");
                Find<PasswordBox>(form, "TokenBox").Password = Token;
                foreach (string invalidPort in new[] { "", "0", "65536", "abc", "-1" })
                {
                    Find<TextBox>(form, "PortBox").Text = invalidPort;
                    Find<Button>(form, "ConnectButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(Find<TextBlock>(form, "ConnectionLabel").Text.Contains("Порт") && Find<TextBox>(form, "HostBox").IsEnabled,
                        "Invalid port started connection.");
                }
                form.Close();
            }
        }

        private static int UnusedPort()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
        }

        private static void TestConnectionFailures()
        {
            using (MainWindow form = Form(UnusedPort()))
            {
                Find<Button>(form, "ConnectButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(delegate { return Find<TextBox>(form, "HostBox").IsEnabled; }, 5000);
                Check(Find<DataGrid>(form, "LevelGrid").Items.Count == 0 && Find<TextBlock>(form, "ConnectionLabel").Text.Contains("TCP"), "Unavailable server not reported.");
                form.Close();
            }
            using (LogServer server = Server())
            using (MainWindow form = Form(server.Port))
            {
                Find<PasswordBox>(form, "TokenBox").Password = "wrong";
                Find<Button>(form, "ConnectButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(delegate { return Find<TextBox>(form, "HostBox").IsEnabled; }, 5000);
                Check(!Find<Button>(form, "RefreshButton").IsEnabled, "Wrong token established a UI connection.");
                form.Close();
            }
        }

        private static void TestServerLoss()
        {
            using (LogServer server = Server())
            using (MainWindow form = Form(server.Port))
            {
                Connect(form); server.Dispose();
                Find<Button>(form, "RefreshButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(delegate { return Find<TextBox>(form, "HostBox").IsEnabled; }, 5000);
                Check(Find<DataGrid>(form, "LevelGrid").Items.Count == 0, "Lost server leaves apparently current statistics.");
                Save(form, "admin-server-unavailable.png");
                form.Close();
            }
        }

        private sealed class Peer : IDisposable
        {
            private readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            private readonly Thread worker;
            private readonly object gate = new object();
            private TcpClient client;
            private bool disposed;
            public readonly ManualResetEvent Requested = new ManualResetEvent(false), Release = new ManualResetEvent(false);
            public int Port { get { return ((IPEndPoint)listener.LocalEndpoint).Port; } }
            public Peer(bool blocked, string response, bool blockAfterFirst = false)
            {
                listener.Start();
                worker = new Thread(delegate()
                {
                    try
                    {
                        TcpClient accepted = listener.AcceptTcpClient();
                        lock (gate) { if (disposed) { accepted.Close(); return; } client = accepted; }
                        using (accepted)
                        {
                            Protocol.ReadFrame(accepted.GetStream());
                            Protocol.WriteFrame(accepted.GetStream(), Protocol.Text(MessageType.Response, "READY"));
                            Protocol.ReadFrame(accepted.GetStream());
                            if (blockAfterFirst)
                            {
                                Protocol.WriteFrame(accepted.GetStream(), Protocol.Text(MessageType.Response, response));
                                Protocol.ReadFrame(accepted.GetStream());
                            }
                            Requested.Set();
                            if (blocked) Release.WaitOne();
                            Protocol.WriteFrame(accepted.GetStream(), Protocol.Text(MessageType.Response, response));
                        }
                    }
                    catch (IOException) { }
                    catch (SocketException) { }
                    catch (ObjectDisposedException) { }
                });
                worker.IsBackground = true; worker.Start();
            }
            public void Dispose()
            {
                lock (gate) { disposed = true; if (client != null) client.Close(); }
                Release.Set(); listener.Stop();
                Check(worker.Join(3000), "UI peer failed to stop.");
                Requested.Dispose(); Release.Dispose();
            }
        }

        private static string EmptyStatus()
        {
            return "LEVEL\tRECEIVED\tSTORED\tFILTERED\tFAILED\nDEBUG\t0\t0\t0\t0\nINFO\t0\t0\t0\t0\nWARN\t0\t0\t0\t0\nERROR\t0\t0\t0\t0\nCRITICAL\t0\t0\t0\t0\nSOURCE\tMINIMUM\tACTIVE_CONNECTIONS\n";
        }

        private static void TestResponsiveness()
        {
            using (Peer peer = new Peer(true, EmptyStatus()))
            using (MainWindow form = Form(peer.Port))
            {
                int ticks = 0;
                DispatcherTimer sentinel = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
                try
                {
                    sentinel.Tick += delegate { ticks++; }; sentinel.Start();
                    Find<Button>(form, "ConnectButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    PumpUntil(delegate { return peer.Requested.WaitOne(0) && ticks >= 5; }, 2000);
                    Check(!Find<Button>(form, "RefreshButton").IsEnabled, "Blocked request completed unexpectedly.");
                    peer.Release.Set(); PumpUntil(delegate { return Find<Button>(form, "RefreshButton").IsEnabled; }, 3000);
                }
                finally { sentinel.Stop(); }
                form.Close();
            }
        }

        private static void TestCancel()
        {
            using (Peer peer = new Peer(true, EmptyStatus()))
            using (MainWindow form = Form(peer.Port))
            {
                Find<Button>(form, "ConnectButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(delegate { return peer.Requested.WaitOne(0); }, 2000);
                Stopwatch elapsed = Stopwatch.StartNew(); Find<Button>(form, "ConnectButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(elapsed.ElapsedMilliseconds < 1000, "Cancel blocks UI.");
                peer.Release.Set(); PumpUntil(delegate { return Find<TextBox>(form, "HostBox").IsEnabled; }, 3000);
                Check(!Find<Button>(form, "RefreshButton").IsEnabled && Find<DataGrid>(form, "LevelGrid").Items.Count == 0, "Late response resurrected canceled connection.");
                form.Close();
            }
        }

        private static void TestClose()
        {
            using (Peer peer = new Peer(true, EmptyStatus()))
            using (MainWindow form = Form(peer.Port))
            {
                Find<Button>(form, "ConnectButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(delegate { return peer.Requested.WaitOne(0); }, 2000);
                Stopwatch elapsed = Stopwatch.StartNew(); form.Close();
                Check(elapsed.ElapsedMilliseconds < 1000 && form.IsDisposed, "Close waits for network timeout.");
                peer.Release.Set(); Pump();
            }
        }

        private static void TestInvalidSnapshot()
        {
            using (Peer peer = new Peer(false, EmptyStatus().Replace("INFO\t0\t0\t0\t0", "INFO\t0\t5\t0\t0")))
            using (MainWindow form = Form(peer.Port))
            {
                Find<Button>(form, "ConnectButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(delegate { return Find<TextBox>(form, "HostBox").IsEnabled; }, 3000);
                Check(Find<DataGrid>(form, "LevelGrid").Items.Count == 0 && Find<TextBlock>(form, "ConnectionLabel").Text.Contains("некорректное"), "Malformed data displayed.");
                form.Close();
            }
        }

        private static void TestAutoRefresh()
        {
            using (LogServer server = Server())
            using (RemoteLogger logger = new RemoteLogger("127.0.0.1", server.Port, "web"))
            using (MainWindow form = Form(server.Port))
            {
                Connect(form); Find<CheckBox>(form, "AutoRefreshBox").IsChecked = true;
                logger.Log(LogLevel.WARN, "periodic update");
                PumpUntil(delegate { return ((LevelStatistics)Find<DataGrid>(form, "LevelGrid").Items[2]).Received.ToString() == "1"; }, 3000);
                form.Close();
            }
        }

        private static void TestSourceSelection()
        {
            using (LogServer server = Server())
            using (RemoteLogger logger = new RemoteLogger("127.0.0.1", server.Port, "SelectedSource"))
            using (MainWindow form = Form(server.Port))
            {
                Connect(form);
                Find<DataGrid>(form, "SourceGrid").SelectedIndex = 0;
                Check(Find<TextBox>(form, "SourceBox").Text == "SelectedSource" &&
                    (LogLevel)Find<ComboBox>(form, "MinimumBox").SelectedItem == LogLevel.DEBUG, "Selection did not populate filter fields.");
                Find<TextBox>(form, "SourceBox").Text = String.Empty;
                Find<Button>(form, "ApplyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(Find<TextBlock>(form, "ConnectionLabel").Text.Contains("Имя источника") && Find<Button>(form, "RefreshButton").IsEnabled,
                    "Invalid source disconnected or sent a request.");
                form.Close();
            }
        }

        private static void TestDisconnectPending()
        {
            using (Peer peer = new Peer(true, EmptyStatus(), true))
            using (MainWindow form = Form(peer.Port))
            {
                Connect(form); Find<Button>(form, "RefreshButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(delegate { return peer.Requested.WaitOne(0); }, 2000);
                Stopwatch elapsed = Stopwatch.StartNew(); Find<Button>(form, "ConnectButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(elapsed.ElapsedMilliseconds < 1000, "Disconnect waited for blocked refresh.");
                peer.Release.Set(); PumpUntil(delegate { return Find<TextBox>(form, "HostBox").IsEnabled; }, 3000);
                Check(Find<DataGrid>(form, "LevelGrid").Items.Count == 0 && !Find<Button>(form, "RefreshButton").IsEnabled,
                    "Late refresh restored disconnected statistics.");
                form.Close();
            }
        }

        private static void TestReconnect()
        {
            using (LogServer first = Server())
            using (MainWindow form = Form(first.Port))
            {
                Connect(form); first.Dispose(); Find<Button>(form, "RefreshButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(delegate { return Find<TextBox>(form, "HostBox").IsEnabled; }, 3000);
                using (LogServer second = Server())
                {
                    Find<TextBox>(form, "PortBox").Text = (second.Port).ToString();
                    Connect(form);
                    Check(Find<DataGrid>(form, "LevelGrid").Items.Count == 5 &&
                        ((LevelStatistics)Find<DataGrid>(form, "LevelGrid").Items[0]).Received.ToString() == "0", "Reconnect did not get fresh data.");
                    form.Close();
                }
            }
        }
    }
}
