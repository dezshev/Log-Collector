using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Reflection;
using LogCollector.Client;
using LogCollector.Common;

namespace LogCollector.AdminWpf
{
    public sealed class MainWindow : Window, IDisposable
    {
        private enum OperationKind { Connect, Refresh, Filter }
        private sealed class Operation
        {
            public OperationKind Kind;
            public int Generation;
            public string Host, Token, Source;
            public int Port;
            public LogLevel Minimum;
            public bool FilterAcknowledged;
        }

        private readonly object lifetimeGate = new object();
        private readonly BackgroundWorker worker = new BackgroundWorker();
        private readonly DispatcherTimer refreshTimer = new DispatcherTimer();
        private AdminClient client;
        private Operation operation;
        private int generation;
        private bool connected, connecting, applyingSnapshot, operationPending;
        private volatile bool closing;
        private readonly Grid layout;
        private readonly TextBox hostBox, portBox, sourceBox, eventBox;
        private readonly PasswordBox tokenBox;
        private readonly Button connectButton, refreshButton, applyButton;
        private readonly CheckBox autoRefreshBox;
        private readonly ComboBox minimumBox;
        private readonly DataGrid sourceGrid, levelGrid;
        private readonly TextBlock totalsLabel, connectionLabel, statusLabel, updatedLabel;
        public bool IsDisposed { get { return closing; } }

        public MainWindow()
        {
            Title = "Система сбора логов";
            Width = 1180; Height = 880; MinWidth = 1040; MinHeight = 780;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Segoe UI"); FontSize = 14;
            Background = Brushes.White;
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("LogCollector.AdminWpf.MainView.xaml"))
                layout = (Grid)XamlReader.Load(stream);
            Content = layout;
            hostBox = FindControl<TextBox>("HostBox"); portBox = FindControl<TextBox>("PortBox");
            sourceBox = FindControl<TextBox>("SourceBox"); eventBox = FindControl<TextBox>("EventBox");
            tokenBox = FindControl<PasswordBox>("TokenBox");
            connectButton = FindControl<Button>("ConnectButton"); refreshButton = FindControl<Button>("RefreshButton");
            applyButton = FindControl<Button>("ApplyButton"); autoRefreshBox = FindControl<CheckBox>("AutoRefreshBox");
            minimumBox = FindControl<ComboBox>("MinimumBox");
            sourceGrid = FindControl<DataGrid>("SourceGrid"); levelGrid = FindControl<DataGrid>("LevelGrid");
            totalsLabel = FindControl<TextBlock>("TotalsLabel"); connectionLabel = FindControl<TextBlock>("ConnectionLabel");
            statusLabel = FindControl<TextBlock>("StatusLabel"); updatedLabel = FindControl<TextBlock>("UpdatedLabel");
            tokenBox.Password = Environment.GetEnvironmentVariable("LOGCOLLECTOR_ADMIN_TOKEN") ?? String.Empty;
            minimumBox.ItemsSource = Enum.GetValues(typeof(LogLevel)); minimumBox.SelectedIndex = 0;
            worker.DoWork += WorkerDoWork;
            worker.RunWorkerCompleted += WorkerCompleted;
            connectButton.Click += ConnectClicked;
            refreshButton.Click += delegate { StartRefresh(); };
            applyButton.Click += ApplyClicked;
            sourceGrid.SelectionChanged += SourceSelected;
            refreshTimer.Interval = TimeSpan.FromSeconds(1);
            refreshTimer.Tick += delegate { if (autoRefreshBox.IsChecked == true) StartRefresh(); };
            refreshTimer.Start();
            Closed += delegate { Dispose(); };
            UpdateControls();
        }

        public T FindControl<T>(string name) where T : FrameworkElement
        { return (T)layout.FindName(name); }

        private void ConnectClicked(object sender, EventArgs args)
        {
            if (connected || connecting) { Disconnect("Соединение закрыто пользователем."); return; }
            if (operationPending) return;
            string host = hostBox.Text.Trim();
            if (host.Length == 0 || host.Length > 255 || HasControl(host))
            { ShowValidation("Укажите корректный адрес сервера."); hostBox.Focus(); return; }
            bool validToken;
            try { validToken = !String.IsNullOrEmpty(tokenBox.Password) && new UTF8Encoding(false, true).GetByteCount(tokenBox.Password) <= 1024; }
            catch (EncoderFallbackException) { validToken = false; }
            if (!validToken)
            { ShowValidation("Укажите токен администратора, не более 1024 байт UTF-8."); tokenBox.Focus(); return; }
            int port;
            if (!Int32.TryParse(portBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port < 1 || port > 65535)
            { ShowValidation("Порт должен быть числом от 1 до 65535."); portBox.Focus(); return; }
            connecting = true;
            connectionLabel.Text = "Подключение к " + host + ":" + portBox.Text + "…";
            StartOperation(new Operation { Kind = OperationKind.Connect, Host = host, Port = port, Token = tokenBox.Password });
        }

        private static bool HasControl(string text)
        { foreach (char c in text) if (Char.IsControl(c)) return true; return false; }

        private void StartRefresh()
        { if (connected && !operationPending && !closing) StartOperation(new Operation { Kind = OperationKind.Refresh }); }

        private void ApplyClicked(object sender, EventArgs args)
        {
            if (!connected || operationPending) return;
            try { LogRecord.ValidateSource(sourceBox.Text); }
            catch (ArgumentException) { ShowValidation("Имя источника: 1–128 символов без управляющих символов."); sourceBox.Focus(); return; }
            if (minimumBox.SelectedItem == null) { ShowValidation("Выберите минимальный уровень."); return; }
            StartOperation(new Operation { Kind = OperationKind.Filter, Source = sourceBox.Text, Minimum = (LogLevel)minimumBox.SelectedItem });
        }

        private void StartOperation(Operation next)
        {
            lock (lifetimeGate) next.Generation = generation;
            operation = next;
            operationPending = true;
            worker.RunWorkerAsync(next);
            statusLabel.Text = next.Kind == OperationKind.Connect ? "Подключение…" : next.Kind == OperationKind.Filter ? "Применение фильтра…" : "Обновление…";
            UpdateControls();
        }

        private void WorkerDoWork(object sender, DoWorkEventArgs args)
        {
            Operation request = (Operation)args.Argument;
            AdminClient current;
            if (request.Kind == OperationKind.Connect)
            {
                current = new AdminClient(request.Host, request.Port, request.Token);
                try
                {
                    lock (lifetimeGate)
                    {
                        if (closing || request.Generation != generation) { current.Dispose(); return; }
                        client = current;
                    }
                    ServerStatus snapshot = current.GetSnapshot();
                    lock (lifetimeGate) if (closing || request.Generation != generation) { current.Dispose(); return; }
                    args.Result = snapshot;
                }
                catch { current.Dispose(); throw; }
            }
            else
            {
                lock (lifetimeGate)
                {
                    if (closing || request.Generation != generation) return;
                    current = client;
                }
                if (current == null) throw new IOException("Connection closed.");
                if (request.Kind == OperationKind.Filter)
                { current.SetFilter(request.Source, request.Minimum); request.FilterAcknowledged = true; }
                args.Result = current.GetSnapshot();
            }
        }

        private void WorkerCompleted(object sender, RunWorkerCompletedEventArgs args)
        {
            if (closing) return;
            if (!Dispatcher.CheckAccess())
            {
                // Ответ применяется только в потоке окна.
                Dispatcher.BeginInvoke(new Action<object, RunWorkerCompletedEventArgs>(WorkerCompleted), sender, args);
                return;
            }
            operationPending = false;
            bool current;
            // Поздний ответ отменённого подключения не обновляет окно.
            lock (lifetimeGate) current = operation.Generation == generation;
            if (!current) { UpdateControls(); return; }
            if (args.Error != null)
            {
                string detail = Explain(args.Error);
                if (operation.Kind == OperationKind.Filter)
                    detail = operation.FilterAcknowledged ? "Фильтр подтверждён, но снимок состояния не получен. " + detail :
                        "Подтверждение изменения не получено. После переподключения проверьте правило. " + detail;
                Disconnect(detail);
                statusLabel.Text = "Ошибка соединения";
                connectionLabel.Text = detail;
                connectionLabel.Foreground = Brushes.Firebrick;
                return;
            }
            if (args.Result == null) { UpdateControls(); return; }
            connected = true; connecting = false;
            connectionLabel.Text = "Подключено к " + hostBox.Text.Trim() + ":" + portBox.Text + ". Фильтры изменяются на работающем сервере.";
            connectionLabel.Foreground = Brushes.SlateGray;
            ApplySnapshot((ServerStatus)args.Result);
            if (operation.Kind == OperationKind.Connect) LogEvent("Подключение установлено.");
            if (operation.Kind == OperationKind.Filter) LogEvent("Подтверждено: " + operation.Source + ", минимум " + operation.Minimum + ".");
            statusLabel.Text = "Подключено";
            UpdateControls();
        }

        private void ApplySnapshot(ServerStatus snapshot)
        {
            SourceStatus previous = sourceGrid.SelectedItem as SourceStatus;
            applyingSnapshot = true;
            try
            {
                sourceGrid.ItemsSource = snapshot.Sources;
                levelGrid.ItemsSource = snapshot.Levels;
                if (previous != null)
                    foreach (SourceStatus source in snapshot.Sources)
                        if (source.Name == previous.Name) { sourceGrid.SelectedItem = source; break; }
            }
            finally { applyingSnapshot = false; }
            totalsLabel.Text = String.Format(CultureInfo.CurrentCulture,
                "Активных подключений: {0}   ·   Источников с правилами: {1}", snapshot.ActiveConnections, snapshot.Sources.Count);
            FindControl<TextBlock>("ReceivedValue").Text = snapshot.TotalReceived.ToString("N0");
            FindControl<TextBlock>("StoredValue").Text = snapshot.TotalStored.ToString("N0");
            FindControl<TextBlock>("FilteredValue").Text = snapshot.TotalFiltered.ToString("N0");
            FindControl<TextBlock>("FailedValue").Text = snapshot.TotalFailed.ToString("N0");
            updatedLabel.Text = "Обновлено в " + DateTime.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
        }

        private void SourceSelected(object sender, SelectionChangedEventArgs args)
        {
            if (applyingSnapshot) return;
            SourceStatus row = sourceGrid.SelectedItem as SourceStatus;
            if (row == null) return;
            sourceBox.Text = row.Name;
            minimumBox.SelectedItem = row.Minimum;
        }

        private void Disconnect(string reason)
        {
            AdminClient previous;
            lock (lifetimeGate) { generation++; previous = client; client = null; }
            if (previous != null) previous.Dispose();
            connected = false; connecting = false;
            applyingSnapshot = true;
            try { sourceGrid.ItemsSource = null; levelGrid.ItemsSource = null; }
            finally { applyingSnapshot = false; }
            foreach (string name in new[] { "ReceivedValue", "StoredValue", "FilteredValue", "FailedValue" })
                FindControl<TextBlock>(name).Text = "—";
            totalsLabel.Text = "Нет актуальных данных сервера";
            updatedLabel.Text = "Обновление: —";
            connectionLabel.Text = reason; connectionLabel.Foreground = Brushes.SlateGray;
            statusLabel.Text = "Отключено";
            LogEvent(reason);
            UpdateControls();
        }

        private void UpdateControls()
        {
            bool editable = !connected && !connecting && !operationPending;
            hostBox.IsEnabled = portBox.IsEnabled = tokenBox.IsEnabled = editable;
            connectButton.Content = connected ? "Отключить" : connecting ? "Отменить" : "Подключить";
            connectButton.IsEnabled = !closing && (connected || connecting || !operationPending);
            refreshButton.IsEnabled = connected && !operationPending;
            sourceBox.IsEnabled = minimumBox.IsEnabled = applyButton.IsEnabled = connected && !operationPending;
        }

        private void ShowValidation(string message)
        { statusLabel.Text = "Проверьте введённые данные"; connectionLabel.Text = message; connectionLabel.Foreground = Brushes.Firebrick; LogEvent(message); }

        private void LogEvent(string message)
        {
            // Ограничиваем историю и не записываем токен.
            if (eventBox.Text.Length > 24000) eventBox.Clear();
            eventBox.AppendText(DateTime.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture) + "  " + message + Environment.NewLine);
            eventBox.ScrollToEnd();
        }

        private static string Explain(Exception error)
        {
            if (error is TimeoutException) return "Сервер не ответил за отведённое время.";
            if (error is InvalidDataException) return "Сервер вернул некорректное состояние. Соединение закрыто.";
            if (error is SocketException) return "Не удалось установить TCP-соединение. Проверьте адрес, порт и работу сервера.";
            if (error is IOException) return "Связь прервана, истёк срок ответа или сервер отклонил запрос. Проверьте сервер и токен.";
            if (error is ArgumentException) return "Некорректные параметры подключения.";
            return "Операция завершилась ошибкой. Соединение закрыто; повторите подключение.";
        }

        protected override void OnClosing(CancelEventArgs args)
        {
            base.OnClosing(args);
            if (args.Cancel) return;
            lock (lifetimeGate) closing = true;
            refreshTimer.Stop();
            Disconnect("Окно закрыто.");
        }

        public void Dispose()
        {
            lock (lifetimeGate)
            {
                if (closing && client == null) { refreshTimer.Stop(); return; }
                closing = true; generation++;
                if (client != null) { client.Dispose(); client = null; }
            }
            refreshTimer.Stop();
            if (IsVisible) Close();
        }
    }
}
