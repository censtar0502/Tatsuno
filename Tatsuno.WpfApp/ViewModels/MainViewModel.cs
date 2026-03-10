using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Tatsuno.Model;
using Tatsuno.Protocol;
using Tatsuno.Transport.Serial;
using Tatsuno.WpfApp.Infrastructure;

namespace Tatsuno.WpfApp.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private static readonly TimeSpan CommsTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PeriodicTotalsInterval = TimeSpan.FromSeconds(15);

    private readonly object _sync = new();
    private readonly TatsunoStreamDecoder _decoder = new();
    private readonly DispatcherTimer _watchdog;
    private readonly DispatcherTimer _periodicTotals;
    private SerialPortSession? _session;
    private CancellationTokenSource? _pollCts;
    private StreamWriter? _fileLog;
    private PostViewModel? _lastTxPost;
    private int _roundRobinIndex = -1;

    private string? _selectedPort;
    private int _baudRate = 19200;
    private Parity _parity = Parity.Even;
    private int _dataBits = 8;
    private StopBits _stopBits = StopBits.One;
    private int _postsCount = 1;
    private bool _isConnected;
    private string _pollState = "Отключено";
    private string _liftedInfo = "—";
    private int _framesOk;
    private int _framesBadBcc;
    private DateTime? _lastRx;
    private string _logFilePath = "—";
    private PostViewModel? _selectedPost;
    private PostViewModel? _dashboardPost;

    public MainViewModel()
    {
        AvailablePorts = new ObservableCollection<string>();
        Posts = new ObservableCollection<PostViewModel>();
        Log = new ObservableCollection<LogLine>();
        ParityValues = new ObservableCollection<Parity>(Enum.GetValues<Parity>().Where(p => p is Parity.None or Parity.Even or Parity.Odd));
        StopBitsValues = new ObservableCollection<StopBits>(Enum.GetValues<StopBits>().Where(s => s is StopBits.One or StopBits.Two));

        RefreshPortsCommand = new RelayCommand(RefreshPorts);
        ApplyPostsCommand = new RelayCommand(ApplyPosts, () => !IsConnected);
        ConnectCommand = new RelayCommand(Connect, () => !IsConnected && !string.IsNullOrWhiteSpace(SelectedPort));
        DisconnectCommand = new RelayCommand(Disconnect, () => IsConnected);
        CancelCommand = new RelayCommand(QueueCancel, () => DashboardPost is not null);
        RequestStatusCommand = new RelayCommand(QueueStatus, () => DashboardPost is not null);
        RequestTotalsCommand = new RelayCommand(QueueTotals, () => DashboardPost is not null);
        LockPumpCommand = new RelayCommand(QueueLockPump, () => DashboardPost is not null);
        ReleasePumpCommand = new RelayCommand(QueueReleasePump, () => DashboardPost is not null);

        _watchdog = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _watchdog.Tick += OnWatchdogTick;

        // Reference program sends A20 (totals) every ~15 seconds during idle
        _periodicTotals = new DispatcherTimer { Interval = PeriodicTotalsInterval };
        _periodicTotals.Tick += OnPeriodicTotalsTick;

        RefreshPorts();
        ApplyPosts();
    }

    public ObservableCollection<string> AvailablePorts { get; }
    public ObservableCollection<PostViewModel> Posts { get; }
    public ObservableCollection<LogLine> Log { get; }
    public ObservableCollection<Parity> ParityValues { get; }
    public ObservableCollection<StopBits> StopBitsValues { get; }

    public RelayCommand RefreshPortsCommand { get; }
    public RelayCommand ApplyPostsCommand { get; }
    public RelayCommand ConnectCommand { get; }
    public RelayCommand DisconnectCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand RequestStatusCommand { get; }
    public RelayCommand RequestTotalsCommand { get; }
    public RelayCommand LockPumpCommand { get; }
    public RelayCommand ReleasePumpCommand { get; }

    public string? SelectedPort
    {
        get => _selectedPort;
        set
        {
            if (SetProperty(ref _selectedPort, value))
            {
                ConnectCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public int BaudRate
    {
        get => _baudRate;
        set => SetProperty(ref _baudRate, value);
    }

    public Parity Parity
    {
        get => _parity;
        set => SetProperty(ref _parity, value);
    }

    public int DataBits
    {
        get => _dataBits;
        set => SetProperty(ref _dataBits, value);
    }

    public StopBits StopBits
    {
        get => _stopBits;
        set => SetProperty(ref _stopBits, value);
    }

    public int PostsCount
    {
        get => _postsCount;
        set => SetProperty(ref _postsCount, Math.Max(1, value));
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (SetProperty(ref _isConnected, value))
            {
                ConnectCommand.RaiseCanExecuteChanged();
                DisconnectCommand.RaiseCanExecuteChanged();
                ApplyPostsCommand.RaiseCanExecuteChanged();
                Raise(nameof(ConnectionStatusBrush));
            }
        }
    }

    public string PollState
    {
        get => _pollState;
        private set => SetProperty(ref _pollState, value);
    }

    public string LiftedInfo
    {
        get => _liftedInfo;
        private set => SetProperty(ref _liftedInfo, value);
    }

    public int FramesOk
    {
        get => _framesOk;
        private set => SetProperty(ref _framesOk, value);
    }

    public int FramesBadBcc
    {
        get => _framesBadBcc;
        private set => SetProperty(ref _framesBadBcc, value);
    }

    public DateTime? LastRx
    {
        get => _lastRx;
        private set => SetProperty(ref _lastRx, value);
    }

    public string LogFilePath
    {
        get => _logFilePath;
        private set => SetProperty(ref _logFilePath, value);
    }

    public PostViewModel? SelectedPost
    {
        get => _selectedPost;
        set
        {
            if (SetProperty(ref _selectedPost, value))
            {
                foreach (PostViewModel post in Posts)
                {
                    post.IsSelected = ReferenceEquals(post, value);
                }

                UpdateDashboardPost();
            }
        }
    }

    public PostViewModel? DashboardPost
    {
        get => _dashboardPost;
        private set
        {
            if (SetProperty(ref _dashboardPost, value))
            {
                Raise(nameof(DashboardTitle));
                Raise(nameof(DashboardStateText));
                Raise(nameof(DashboardAmountText));
                Raise(nameof(DashboardVolumeText));
                Raise(nameof(DashboardPriceText));
                Raise(nameof(DashboardNozzles));
                Raise(nameof(DashboardActiveNozzleText));
                Raise(nameof(DashboardLastPayload));
                Raise(nameof(DashboardLastUpdateText));
                Raise(nameof(DashboardControllabilityText));
                CancelCommand.RaiseCanExecuteChanged();
                RequestStatusCommand.RaiseCanExecuteChanged();
                RequestTotalsCommand.RaiseCanExecuteChanged();
                LockPumpCommand.RaiseCanExecuteChanged();
                ReleasePumpCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string DashboardTitle => DashboardPost is null ? "Нет активного поста" : DashboardPost.Header;
    public string DashboardStateText => DashboardPost?.StatusText ?? "—";
    public string DashboardAmountText => DashboardPost?.LiveAmountText ?? "0";
    public string DashboardVolumeText => DashboardPost?.LiveVolumeText ?? "0,00";
    public string DashboardPriceText => DashboardPost?.LivePriceText ?? "0";
    public string DashboardActiveNozzleText => DashboardPost?.ActiveNozzleText ?? "—";
    public string DashboardLastPayload => DashboardPost?.LastPayload ?? string.Empty;
    public string DashboardLastUpdateText => DashboardPost?.LastUpdateText ?? "—";
    public string DashboardControllabilityText => DashboardPost?.ControllabilityText ?? "—";
    public ObservableCollection<NozzleViewModel> DashboardNozzles => DashboardPost?.Nozzles ?? new ObservableCollection<NozzleViewModel>();

    public Brush ConnectionStatusBrush => IsConnected
        ? new SolidColorBrush(Color.FromRgb(34, 197, 94))
        : new SolidColorBrush(Color.FromRgb(239, 68, 68));

    public void OnWindowClosing()
    {
        Disconnect();
    }

    private void RefreshPorts()
    {
        AvailablePorts.Clear();
        foreach (string port in SerialPort.GetPortNames().OrderBy(x => x))
        {
            AvailablePorts.Add(port);
        }

        if (SelectedPort is null || !AvailablePorts.Contains(SelectedPort))
        {
            SelectedPort = AvailablePorts.FirstOrDefault();
        }
    }

    private void ApplyPosts()
    {
        Posts.Clear();
        // 1 ТРК (side A) with 3 nozzles, all at ONE RS-485 address
        // Only one nozzle can dispense at a time
        var post = new PostViewModel(1, "1", 3);
        post.Selected += HandlePostSelected;
        post.StartRequested += HandlePostStart;
        post.CancelRequested += HandlePostCancel;
        post.StatusRequested += HandlePostStatus;
        post.TotalsRequested += HandlePostTotals;
        post.LockRequested += HandlePostLock;
        post.ReleaseRequested += HandlePostRelease;
        post.SavePricesRequested += HandlePostSavePrices;
        post.ApplySnapshot();
        Posts.Add(post);

        SelectedPost = post;
        UpdateDashboardPost();
    }

    private void HandlePostSelected(PostViewModel post)
    {
        SelectedPost = post;
    }

    private void Connect()
    {
        if (IsConnected || string.IsNullOrWhiteSpace(SelectedPort))
        {
            return;
        }

        try
        {
            _session = new SerialPortSession();
            _session.BytesReceived += OnBytesReceived;
            _session.ReceiveFault += OnReceiveFault;
            _session.Open(new SerialPortSettings
            {
                PortName = SelectedPort!,
                BaudRate = BaudRate,
                Parity = Parity,
                DataBits = DataBits,
                StopBits = StopBits,
                Handshake = Handshake.None
            });

            OpenFileLog(SelectedPort!);
            IsConnected = true;
            PollState = "Опрос активен";
            _watchdog.Start();
            _periodicTotals.Start();
            AddLog("SYS", $"Connected {SelectedPort}");

            // Startup sequence per reference program:
            // 1. First poll → may get Q00 (handled automatically by engine → auto-queues A00)
            // 2. A15 — request status (gets Q61 with current state)
            // 3. A20 — request totals (gets Q65 with accumulated values)
            PostViewModel? trk = Posts.FirstOrDefault();
            if (trk is not null)
            {
                trk.Engine.Enqueue(TatsunoCodec.BuildRequestStatusPayload(), TatsunoCommandKind.RequestStatus, "initial status", allowedWhenUncontrollable: true);
                trk.Engine.Enqueue(TatsunoCodec.BuildRequestTotalsPayload(), TatsunoCommandKind.RequestTotals, "initial totals", allowedWhenUncontrollable: true);
                AddLog("SYS", "Queued startup: A15 (status) + A20 (totals)");
            }

            StartPolling();
        }
        catch (Exception ex)
        {
            AddLog("SYS", $"Connect failed: {ex.Message}");
            Disconnect();
        }
    }

    private void Disconnect()
    {
        _watchdog.Stop();
        _periodicTotals.Stop();
        StopPolling();
        CloseFileLog();

        if (_session is not null)
        {
            try { _session.BytesReceived -= OnBytesReceived; } catch { }
            try { _session.ReceiveFault -= OnReceiveFault; } catch { }
            try { _session.Close(); } catch { }
            _session = null;
        }

        IsConnected = false;
        PollState = "Отключено";
    }

    private void StartPolling()
    {
        if (_session is null)
        {
            return;
        }

        StopPolling();
        _pollCts = new CancellationTokenSource();
        CancellationToken token = _pollCts.Token;

        // BUG FIX: capture a snapshot of the Posts list on the UI thread before the loop;
        // ObservableCollection is not thread-safe for reads from background threads.
        // Posts can only be modified while disconnected (command guard), so this snapshot
        // is stable for the entire lifetime of the polling session.
        PostViewModel[] postSnapshot = Application.Current.Dispatcher.Invoke(() => Posts.ToArray());

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (postSnapshot.Length == 0 || _session is null)
                    {
                        await Task.Delay(150, token);
                        continue;
                    }

                    _roundRobinIndex = (_roundRobinIndex + 1) % postSnapshot.Length;
                    PostViewModel post = postSnapshot[_roundRobinIndex];
                    TatsunoControllerAction action;
                    lock (_sync)
                    {
                        action = post.Engine.GetNextAction(DateTime.UtcNow);
                        if (action.Kind != TatsunoControllerActionKind.None)
                        {
                            _lastTxPost = post;
                        }
                    }

                    if (action.Kind != TatsunoControllerActionKind.None)
                    {
                        _session.Send(action.Bytes);
                        WriteRawToFile("TX", action.Bytes);
                        Application.Current.Dispatcher.Invoke(() => AddLog("TX", action.Description + "  " + RenderBytes(action.Bytes)));
                    }

                    await Task.Delay(120, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Application.Current.Dispatcher.Invoke(() => AddLog("SYS", $"Poll loop: {ex.Message}"));
                    await Task.Delay(300, token);
                }
            }
        }, token);
    }

    private void StopPolling()
    {
        try { _pollCts?.Cancel(); } catch { }
        _pollCts?.Dispose();
        _pollCts = null;
    }

    private void OnReceiveFault(Exception ex)
    {
        Application.Current.Dispatcher.Invoke(() => AddLog("SYS", $"RX fault: {ex.Message}"));
    }

    private void OnBytesReceived(ReadOnlyMemory<byte> bytes)
    {
        WriteRawToFile("RX", bytes.Span);
        var items = _decoder.Feed(bytes.Span);
        if (items.Count == 0)
        {
            return;
        }

        Application.Current.Dispatcher.Invoke(() =>
        {
            foreach (TatsunoRxItem item in items)
            {
                LastRx = item.TimestampLocal;

                PostViewModel? owner;
                lock (_sync)
                {
                    owner = _lastTxPost;
                }

                if (owner is null)
                {
                    continue;
                }

                // Mark comms alive on ANY successful RX from this ТРК
                owner.MarkCommsReceived();

                switch (item.Type)
                {
                    case TatsunoRxItemType.ControlByte:
                        owner.Engine.HandleControlByte(item.Control, DateTime.UtcNow);
                        if (item.Control == TatsunoControlBytes.EOT)
                        {
                            // EOT = ТРК idle, update UI to show "ГОТОВ"
                            owner.ApplySnapshot();
                            UpdateDashboardPost();
                            AddLog("RX", $"{owner.Header} <EOT>");
                        }
                        else if (item.Control == TatsunoControlBytes.NAK)
                        {
                            AddLog("RX", $"{owner.Header} <NAK> — ТРК отвергла кадр!");
                        }
                        // Log any diagnostic info from the engine
                        if (owner.Engine.LastControlByteInfo is not null)
                        {
                            AddLog("SYS", $"{owner.Header}: {owner.Engine.LastControlByteInfo}");
                        }
                        break;

                    case TatsunoRxItemType.Dle0:
                        if (_session is not null)
                        {
                            byte[]? frame = owner.Engine.HandleDle0(DateTime.UtcNow, out string description);
                            if (frame is not null)
                            {
                                _session.Send(frame);
                                WriteRawToFile("TX", frame);
                                AddLog("TX", description + "  " + RenderBytes(frame));
                            }
                        }
                        break;

                    case TatsunoRxItemType.Dle1:
                        owner.Engine.HandleDle1(DateTime.UtcNow);
                        AddLog("RX", $"{owner.Header} <DLE>1");
                        // Log confirmation of which command was accepted
                        if (owner.Engine.LastAcceptedCommandInfo is not null)
                        {
                            AddLog("TXN", $"{owner.Header}: {owner.Engine.LastAcceptedCommandInfo}");
                        }
                        break;

                    case TatsunoRxItemType.Frame:
                      AddLog("RX", $"{owner.Header} {(item.BccOk ? "OK" : "BAD")}: {item.PayloadString}");
                     if (item.BccOk && item.PayloadString is not null)
                     {
                         FramesOk++;
                         owner.Engine.HandlePayload(item.PayloadString, item.TimestampLocal);
                        owner.ApplySnapshot();
                        UpdateDashboardPost();
                        UpdateLiftedInfo();
                        AckFrame();
                        
                       // Check if correct nozzle was lifted after Start
                       owner.TriggerAutoAuthorize();
                     }
                    else
                     {
                         FramesBadBcc++;
                     }
                    break;
                }
            }
        });
    }

    private void AckFrame()
    {
        if (_session is null)
        {
            return;
        }

        byte[] ack = TatsunoCodec.BuildAck1();
        _session.Send(ack);
        WriteRawToFile("TX", ack);
        AddLog("TX", RenderBytes(ack));
    }

    private void QueueCancel()
    {
        if (DashboardPost is null) return;
        DashboardPost.Engine.Enqueue(TatsunoCodec.BuildCancelAuthorizationPayload(), TatsunoCommandKind.CancelAuthorization, "cancel authorization");
        AddLog("SYS", $"Queue {DashboardPost.Header}: cancel authorization");
    }

    private void QueueStatus()
    {
        if (DashboardPost is null) return;
        DashboardPost.Engine.Enqueue(TatsunoCodec.BuildRequestStatusPayload(), TatsunoCommandKind.RequestStatus, "request status", allowedWhenUncontrollable: true);
        AddLog("SYS", $"Queue {DashboardPost.Header}: request status");
    }

    private void QueueTotals()
    {
        if (DashboardPost is null) return;
        DashboardPost.Engine.Enqueue(TatsunoCodec.BuildRequestTotalsPayload(), TatsunoCommandKind.RequestTotals, "request totals", allowedWhenUncontrollable: true);
        AddLog("SYS", $"Queue {DashboardPost.Header}: request totals");
    }

    private void QueueLockPump()
    {
        if (DashboardPost is null) return;
        DashboardPost.Engine.Enqueue(TatsunoCodec.BuildLockPumpPayload(), TatsunoCommandKind.LockPump, "lock pump");
        AddLog("SYS", $"Queue {DashboardPost.Header}: lock pump");
    }

    private void QueueReleasePump()
    {
        if (DashboardPost is null) return;
        DashboardPost.Engine.Enqueue(TatsunoCodec.BuildReleasePumpLockPayload(), TatsunoCommandKind.ReleasePumpLock, "release pump lock");
        AddLog("SYS", $"Queue {DashboardPost.Header}: release lock");
    }

    /// <summary>
    /// Build product price list for A11 multi-price command.
    /// ONLY the selected nozzle gets Cash flag with real price, all others get Invalid flag with 0.
    /// </summary>
    private static List<(TatsunoUnitPriceFlag Flag, int UnitPriceRaw)> BuildSelectedNozzlePrice(PostViewModel post, int selectedNozzleNumber)
    {
        var products = new List<(TatsunoUnitPriceFlag Flag, int UnitPriceRaw)>();

        foreach (NozzleViewModel n in post.Nozzles.OrderBy(x => x.Number))
        {
            if (n.Number == selectedNozzleNumber)
            {
                products.Add((TatsunoUnitPriceFlag.Cash, n.ConfiguredPriceRaw));
            }
            else
            {
                products.Add((TatsunoUnitPriceFlag.Invalid, 0));
            }
        }

        while (products.Count < 6)
        {
            products.Add((TatsunoUnitPriceFlag.Invalid, 0));
        }

        return products;
    }

    private void HandlePostStart(PostViewModel post)
    {
        NozzleViewModel? selectedNozzle = post.SelectedNozzle;
        if (selectedNozzle is null) return;

        int priceRaw = selectedNozzle.ConfiguredPriceRaw;
        if (priceRaw <= 0)
        {
            AddLog("SYS", $"ERROR: Price is zero for nozzle {selectedNozzle.Number}, cannot calculate volume");
            return;
        }

        int volumeRaw;
      string presetKindLabel;

        if (post.LastEditWasVolume)
        {
            // User entered volume directly
            volumeRaw = TatsunoValueFormatter.ParseDisplayedVolumeToRaw(post.PresetVolumeText);
          presetKindLabel = "Volume";
        }
        else
        {
            // User entered amount → convert to volume: volumeRaw = amountRaw * 100 / priceRaw
            int amountRaw = TatsunoValueFormatter.ParseDisplayedMoneyToRaw(post.PresetAmountText);
            volumeRaw = amountRaw * 100 / priceRaw;
          presetKindLabel = "Volume(from Amount)";
            AddLog("TXN", $"Amount→Volume conversion: amount={post.PresetAmountText} amountRaw={amountRaw} priceRaw={priceRaw} → volumeRaw={volumeRaw} ({volumeRaw / 100.0:F2}L)");
        }

        // Build price list with ONLY the selected nozzle active
        var products = BuildSelectedNozzlePrice(post, selectedNozzle.Number);
      string payload = TatsunoCodec.BuildAuthorizeMultiPricePayload(
            TatsunoAuthorizationTerm.VolumeLimited,
            TatsunoPresetKind.Volume,
            volumeRaw,
          products);

      string presetText = post.LastEditWasVolume ? post.PresetVolumeText: post.PresetAmountText;
        LogTransactionDetails("HandlePostStart", post, selectedNozzle, presetKindLabel, presetText, volumeRaw, payload);
        
        // Send A11 IMMEDIATELY to show preset on display
        post.Engine.Enqueue(payload, TatsunoCommandKind.AuthorizeMultiPrice, $"authorize nozzle {selectedNozzle.Number} volume={volumeRaw}");
        
        // Store pending nozzle number for later lift verification
        post.PendingStartNozzleNumber = selectedNozzle.Number;
        
        AddLog("SYS", $"A11 sent for nozzle {selectedNozzle.Number}. Waiting for correct nozzle lift...");
    }

    private void HandlePostCancel(PostViewModel post)
    {
        post.Engine.Enqueue(TatsunoCodec.BuildCancelAuthorizationPayload(), TatsunoCommandKind.CancelAuthorization, "cancel authorization");
        AddLog("SYS", $"Queue {post.Header}: cancel authorization");
    }

    private void HandlePostStatus(PostViewModel post)
    {
        post.Engine.Enqueue(TatsunoCodec.BuildRequestStatusPayload(), TatsunoCommandKind.RequestStatus, "request status", allowedWhenUncontrollable: true);
        AddLog("SYS", $"Queue {post.Header}: request status");
    }

    private void HandlePostTotals(PostViewModel post)
    {
        post.Engine.Enqueue(TatsunoCodec.BuildRequestTotalsPayload(), TatsunoCommandKind.RequestTotals, "request totals", allowedWhenUncontrollable: true);
        AddLog("SYS", $"Queue {post.Header}: request totals");
    }

    private void HandlePostLock(PostViewModel post)
    {
        post.Engine.Enqueue(TatsunoCodec.BuildLockPumpPayload(), TatsunoCommandKind.LockPump, "lock pump");
        AddLog("SYS", $"Queue {post.Header}: lock pump");
    }

    private void HandlePostRelease(PostViewModel post)
    {
        post.Engine.Enqueue(TatsunoCodec.BuildReleasePumpLockPayload(), TatsunoCommandKind.ReleasePumpLock, "release pump lock");
        AddLog("SYS", $"Queue {post.Header}: release lock");
    }

    private void HandlePostSavePrices(PostViewModel post)
    {
        AddLog("SYS", $"Prices saved for {post.Header}:");
        foreach (NozzleViewModel n in post.Nozzles)
        {
            AddLog("SYS", $"  Nozzle {n.Number} ({n.ProductName}): {n.PriceText} (raw={n.ConfiguredPriceRaw})");
        }
    }

    /// <summary>
    /// Log full transaction details for diagnostics: all nozzle prices, selected nozzle, preset, payload.
    /// </summary>
    private void LogTransactionDetails(string method, PostViewModel post, NozzleViewModel selectedNozzle, string presetKind, string presetText, int presetRaw, string payload)
    {
        AddLog("TXN", $"--- {method} ---");
        AddLog("TXN", $"  Post: {post.Header}");
        AddLog("TXN", $"  Selected nozzle: #{selectedNozzle.Number} ({selectedNozzle.ProductName})");
        AddLog("TXN", $"  Preset: {presetKind} text=\"{presetText}\" raw={presetRaw}");

        foreach (NozzleViewModel n in post.Nozzles)
        {
            string marker = n.Number == selectedNozzle.Number ? " <<<" : "";
            AddLog("TXN", $"  Nozzle {n.Number}: PriceText=\"{n.PriceText}\" ConfiguredPriceRaw={n.ConfiguredPriceRaw}{marker}");
        }

        AddLog("TXN", $"  A11 payload: {payload}");
        AddLog("TXN", $"  Payload length: {payload.Length} chars");
    }

    private void UpdateLiftedInfo()
    {
        PostViewModel? lifted = Posts.FirstOrDefault(p => p.HasLiftedNozzle);
        LiftedInfo = lifted is null ? "—" : lifted.Header;
    }

    private void UpdateDashboardPost()
    {
        PostViewModel? next = Posts.FirstOrDefault(p => p.HasLiftedNozzle) ?? SelectedPost;
        if (!ReferenceEquals(DashboardPost, next))
        {
            DashboardPost = next;
            return;
        }

        RaiseDashboardProperties();
    }

    private void RaiseDashboardProperties()
    {
        Raise(nameof(DashboardTitle));
        Raise(nameof(DashboardStateText));
        Raise(nameof(DashboardAmountText));
        Raise(nameof(DashboardVolumeText));
        Raise(nameof(DashboardPriceText));
        // DashboardNozzles is NOT bound in UI — nozzles bind directly from PostViewModel.
        // Raising it here every 250ms was creating new ObservableCollection instances for nothing.
        Raise(nameof(DashboardActiveNozzleText));
        Raise(nameof(DashboardLastPayload));
        Raise(nameof(DashboardLastUpdateText));
        Raise(nameof(DashboardControllabilityText));
    }

    private void OnWatchdogTick(object? sender, EventArgs e)
    {
        if (!IsConnected) return;

        foreach (PostViewModel post in Posts)
        {
            post.CheckComms(CommsTimeout);
        }

        UpdateDashboardPost();
    }

    /// <summary>
    /// Reference program sends A20 (totals request) every ~15 seconds during idle.
    /// This keeps totals up-to-date and ensures ТРК data consistency.
    /// IMPORTANT: Do NOT send A20 during active transactions (NozzleLifted / Fuelling)
    /// as it may interfere with A11 authorization.
    /// </summary>
    private void OnPeriodicTotalsTick(object? sender, EventArgs e)
    {
        if (!IsConnected) return;

        foreach (PostViewModel post in Posts)
        {
            // Only request totals when engine is idle (no pending commands)
            // AND ТРК is not in an active transaction state.
            // Sending A20 between A11 and fueling start could clear authorization.
            bool isActive = post.Engine.Snapshot.Condition is TatsunoPumpCondition.NozzleLifted or TatsunoPumpCondition.Fuelling;
            if (post.Engine.PendingCount == 0 && !isActive)
            {
                post.Engine.Enqueue(TatsunoCodec.BuildRequestTotalsPayload(), TatsunoCommandKind.RequestTotals, "periodic totals", allowedWhenUncontrollable: true);
            }
        }
    }

    private void AddLog(string direction, string text)
    {
        Log.Insert(0, new LogLine
        {
            Time = DateTime.Now,
            Direction = direction,
            Text = text
        });

        while (Log.Count > 500)
        {
            Log.RemoveAt(Log.Count - 1);
        }
    }

    private void OpenFileLog(string portName)
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"tatsuno_{portName}_{DateTime.Now:yyyyMMdd_HHmmss}.log");
        _fileLog = new StreamWriter(path, false, System.Text.Encoding.UTF8) { AutoFlush = true };
        LogFilePath = path;

        // Write connection header (matches reference log format)
        _fileLog.WriteLine($"COM {portName}");
        _fileLog.WriteLine($"Скорость {BaudRate}");
        _fileLog.WriteLine($"Биты данных={DataBits}, Стоп биты={StopBits}, Чётность={Parity}");
    }

    private void CloseFileLog()
    {
        try { _fileLog?.Dispose(); } catch { }
        _fileLog = null;
        LogFilePath = "—";
    }

    private void WriteRawToFile(string prefix, ReadOnlySpan<byte> bytes)
    {
        // BUG FIX: WriteRawToFile is called from 3 threads (RX task, poll task, dispatcher)
        // so we must lock around _fileLog writes to prevent interleaved/corrupt output.
        string line = $"<{DateTime.Now:yyyyMMddHHmmss.fff} {prefix}>\n{RenderBytes(bytes)}";
        try
        {
            lock (_sync)
            {
                _fileLog?.WriteLine(line);
            }
        }
        catch
        {
        }
    }

    private static string RenderBytes(ReadOnlySpan<byte> bytes)
    {
        return string.Concat(bytes.ToArray().Select(RenderByte));
    }

    /// <summary>
    /// Render a byte as a human-readable string matching reference log format.
    /// All standard ASCII control characters use their mnemonic names.
    /// </summary>
    private static string RenderByte(byte value) => value switch
    {
        0x00 => "<NUL>",
        0x01 => "<SOH>",
        0x02 => "<STX>",
        0x03 => "<ETX>",
        0x04 => "<EOT>",
        0x05 => "<ENQ>",
        0x06 => "<ACK>",
        0x07 => "<BEL>",
        0x08 => "<BS>",
        0x09 => "<HT>",
        0x0A => "<LF>",
        0x0B => "<VT>",
        0x0C => "<FF>",
        0x0D => "<CR>",
        0x0E => "<SO>",
        0x0F => "<SI>",
        0x10 => "<DLE>",
        0x11 => "<DC1>",
        0x12 => "<DC2>",
        0x13 => "<DC3>",
        0x14 => "<DC4>",
        0x15 => "<NAK>",
        0x16 => "<SYN>",
        0x17 => "<ETB>",
        0x18 => "<CAN>",
        0x19 => "<EM>",
        0x1A => "<SUB>",
        0x1B => "<ESC>",
        0x1C => "<FS>",
        0x1D => "<GS>",
        0x1E => "<RS>",
        0x1F => "<US>",
        0x7F => "<DEL>",
        _ when value >= 0x20 && value <= 0x7E => ((char)value).ToString(),
        _ => $"<0x{value:X2}>"
    };
}
