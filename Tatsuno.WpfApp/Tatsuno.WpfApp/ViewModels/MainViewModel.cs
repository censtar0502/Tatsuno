using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text.Json;
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
        // Load saved prices from previous session (if any)
        LoadPricesFromFile(post);
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
                            // Capture condition AND nozzle BEFORE processing to detect transitions
                            TatsunoPumpCondition prevCondition = owner.Engine.Snapshot.Condition;
                            int prevNozzleNumber = owner.Engine.Snapshot.ActiveNozzleNumber;
                            owner.Engine.HandlePayload(item.PayloadString, item.TimestampLocal);
                            owner.ApplySnapshot();
                            UpdateDashboardPost();
                            UpdateLiftedInfo();
                            AckFrame();

                            // Auto-send A11 when nozzle is first lifted (reference program behavior)
                            // Also trigger if nozzle NUMBER changed within NozzleLifted state
                            // (handles quick nozzle swap without intermediate idle/finished state)
                            TatsunoPumpCondition newCondition = owner.Engine.Snapshot.Condition;
                            int newNozzleNumber = owner.Engine.Snapshot.ActiveNozzleNumber;

                            // Reset pending-start lock when pump returns to idle after any active state.
                            // This is done HERE (not in ApplySnapshot) so that pressing Start sets a lock
                            // that persists until the pump actually completes a cycle and goes idle.
                            if (prevCondition != TatsunoPumpCondition.NozzleStored &&
                                newCondition == TatsunoPumpCondition.NozzleStored)
                            {
                                owner.PendingStartNozzleNumber = 0;
                            }

                            if (newCondition == TatsunoPumpCondition.NozzleLifted &&
                                (prevCondition != TatsunoPumpCondition.NozzleLifted ||
                                 newNozzleNumber != prevNozzleNumber))
                            {
                                TriggerAutoAuthorize(owner);
                            }
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
    /// Reference program sends price ONLY for the selected nozzle's position, all others = 0.
    /// </summary>
    /// <summary>
    /// Build A11 product slots with price ONLY in the lifted nozzle's slot.
    /// All other slots are zeroed out. This matches the reference program (tatsunofuling.txt):
    ///   @A1111000600000003125000000000000000000000
    ///   slot 0 = 00000 (empty), slot 1 = 03125 (nozzle 2 price), rest = 00000
    /// The pump reads the slot corresponding to the physically lifted nozzle (slot = nozzle - 1).
    /// Sending prices in ALL slots caused the pump to pick the wrong nozzle.
    /// </summary>
    private static List<(TatsunoUnitPriceFlag Flag, int UnitPriceRaw)> BuildLiftedNozzlePrice(PostViewModel post, int liftedNozzleNumber)
    {
        var products = new List<(TatsunoUnitPriceFlag Flag, int UnitPriceRaw)>();
        foreach (NozzleViewModel n in post.Nozzles)
        {
            if (n.Number == liftedNozzleNumber)
            {
                // Price in the lifted nozzle's slot only. Flag=0 (Unknown) matches reference program.
                products.Add((TatsunoUnitPriceFlag.Unknown, n.ConfiguredPriceRaw));
            }
            else
            {
                products.Add((TatsunoUnitPriceFlag.Unknown, 0));
            }
        }
        return products;
    }

    private void HandlePostStart(PostViewModel post)
    {
        // DESIGN NOTE (reference-program behavior):
        // A11 authorization is NEVER pre-queued here. Instead it is always sent automatically
        // the moment a NozzleLifted transition is detected (TriggerAutoAuthorize).
        // This matches tatsunofuling.txt exactly and fixes the "wrong nozzle" problem:
        // the pump reports the ACTUAL lifted nozzle number in Q61, and TriggerAutoAuthorize
        // uses that number to put the price in the correct A11 slot.
        //
        // Physical nozzle → protocol nozzle mapping is determined by the pump hardware.
        // If physical nozzle 2 is reported as nozzle 3 in Q61, set the price on nozzle 3.
        //
        // The Start button now validates the preset and shows a "ready" message.
        // The operator lifts the nozzle and the pump will be automatically authorized.

        NozzleViewModel? nozzle = post.SelectedNozzle;
        if (nozzle is null)
        {
            AddLog("SYS", "Start: no nozzle selected");
            return;
        }

        int priceRaw = nozzle.ConfiguredPriceRaw;
        if (priceRaw <= 0)
        {
            AddLog("SYS", $"Start: price = 0 for nozzle {nozzle.Number} — set a price before dispensing");
            return;
        }

        // Validate preset — required, full-fill (no preset) is NOT allowed
        int volumeRaw = 0;
        if (post.LastEditWasVolume && !string.IsNullOrWhiteSpace(post.PresetVolumeText))
        {
            volumeRaw = TatsunoValueFormatter.ParseDisplayedVolumeToRaw(post.PresetVolumeText);
        }
        else if (!post.LastEditWasVolume && !string.IsNullOrWhiteSpace(post.PresetAmountText))
        {
            int amountRaw = TatsunoValueFormatter.ParseDisplayedMoneyToRaw(post.PresetAmountText);
            volumeRaw = priceRaw > 0 ? amountRaw * 100 / priceRaw : 0;
        }

        if (volumeRaw <= 0)
        {
            AddLog("SYS", $"ОШИБКА: Пресет не задан для пистолета {nozzle.Number}! Введите объём (л) или сумму (сум) и нажмите Старт снова.");
            return;
        }

        string presetDesc = $"volume={volumeRaw / 100.0:F2}L";

        AddLog("SYS", $"Start: нозл {nozzle.Number} цена={priceRaw * 10} пресет={presetDesc}. Поднимите пистолет — A11 будет отправлена автоматически.");

        // Lock all other nozzles immediately so the operator can't accidentally lift the wrong one
        post.PendingStartNozzleNumber = nozzle.Number;

        // If nozzle is already lifted (pump condition=NozzleLifted), fire A11 immediately.
        // This handles the case where user presses Start AFTER the nozzle is already up.
        if (post.Engine.Snapshot.Condition == TatsunoPumpCondition.NozzleLifted)
        {
            AddLog("SYS", "Start: nozzle already lifted — triggering A11 now");
            TriggerAutoAuthorize(post);
        }
    }

    /// <summary>
    /// Automatically sends A11 authorization when a nozzle-lifted transition is detected,
    /// or when Start is pressed while a nozzle is already up.
    /// Uses the ACTUAL nozzle number reported by the pump in Q61 (ActiveNozzleNumber).
    /// This is the only correct way to determine the A11 slot: slot = nozzle_number - 1.
    /// </summary>
    private void TriggerAutoAuthorize(PostViewModel post)
    {
        int liftedNozzleNumber = post.Engine.Snapshot.ActiveNozzleNumber;
        if (liftedNozzleNumber <= 0)
        {
            AddLog("SYS", $"Auto-A11: ActiveNozzleNumber=0 for {post.Header}, cannot determine lifted nozzle");
            return;
        }

        // If Start was already pressed for a specific nozzle, make sure the CORRECT nozzle was lifted.
        // This prevents the wrong nozzle from being authorized when the operator makes a mistake.
        int pendingNozzle = post.PendingStartNozzleNumber;
        if (pendingNozzle > 0 && liftedNozzleNumber != pendingNozzle)
        {
            AddLog("SYS", $"ОШИБКА: Поднят неверный пистолет! Старт нажат для пистолета {pendingNozzle}, но поднят пистолет {liftedNozzleNumber}. Опустите пистолет {liftedNozzleNumber} и поднимите пистолет {pendingNozzle}.");
            return;
        }

        NozzleViewModel? nozzle = post.Nozzles.FirstOrDefault(n => n.Number == liftedNozzleNumber);
        if (nozzle is null)
        {
            AddLog("SYS", $"Auto-A11: Nozzle #{liftedNozzleNumber} not found in {post.Header}");
            return;
        }

        int priceRaw = nozzle.ConfiguredPriceRaw;
        if (priceRaw <= 0)
        {
            AddLog("SYS", $"Auto-A11: Price=0 for nozzle #{liftedNozzleNumber}, cannot authorize");
            return;
        }

        // Determine volume preset: use UI preset if entered, otherwise use max volume for full fill.
        // IMPORTANT: This pump model requires term=VolumeLimited (term=1) with a non-zero presetValue.
        // term=Normal (term=0) is accepted by the pump (DLE1) but does NOT start fuelling — confirmed
        // by log analysis: all term=0 A11 frames resulted in nozzle being lowered without dispensing,
        // while term=1 (VolumeLimited) with preset=500 triggered fuelling correctly.
        // Reference program (tatsunofuling.txt) always uses term=1 — never term=0.
        int volumeRaw = 0;
        string presetKindLabel = "Volume(full-fill/max)";
        if (!string.IsNullOrWhiteSpace(post.PresetVolumeText))
        {
            volumeRaw = TatsunoValueFormatter.ParseDisplayedVolumeToRaw(post.PresetVolumeText);
            presetKindLabel = "Volume(preset)";
        }
        else if (!string.IsNullOrWhiteSpace(post.PresetAmountText))
        {
            int amountRaw = TatsunoValueFormatter.ParseDisplayedMoneyToRaw(post.PresetAmountText);
            volumeRaw = priceRaw > 0 ? amountRaw * 100 / priceRaw : 0;
            presetKindLabel = "Volume(from-amount)";
        }

        // Preset is REQUIRED — full-fill without a preset is not allowed.
        if (volumeRaw <= 0)
        {
            AddLog("SYS", $"Auto-A11 ОТМЕНА: пресет не задан для пистолета #{liftedNozzleNumber}. Введите объём (л) или сумму (сум) перед нажатием Старт.");
            return;
        }

        const TatsunoAuthorizationTerm term = TatsunoAuthorizationTerm.VolumeLimited;

        var products = BuildLiftedNozzlePrice(post, liftedNozzleNumber);
        string payload = TatsunoCodec.BuildAuthorizeMultiPricePayload(
            term,
            TatsunoPresetKind.Volume,
            volumeRaw,
            products);

        string presetText = !string.IsNullOrWhiteSpace(post.PresetVolumeText)
            ? post.PresetVolumeText
            : post.PresetAmountText;

        LogTransactionDetails("Auto-A11 (NozzleLifted)", post, nozzle, presetKindLabel, presetText, volumeRaw, payload);
        post.Engine.Enqueue(payload, TatsunoCommandKind.AuthorizeMultiPrice,
            $"auto-authorize nozzle {liftedNozzleNumber} volume={volumeRaw}");
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
        try
        {
            // Normalize each PriceText to the nearest representable protocol value (×10 step).
            // E.g. user enters "1 122" → raw=112 → effective "1 120". Show this to the user.
            foreach (NozzleViewModel n in post.Nozzles)
            {
                int raw = n.ConfiguredPriceRaw;
                string normalized = TatsunoValueFormatter.FormatMoney(raw);
                if (normalized != n.PriceText && raw > 0)
                {
                    AddLog("SYS", $"  ⚠ Nozzle {n.Number}: price {n.PriceText} rounded to {normalized} (protocol step = 10)");
                    n.PriceText = normalized;
                }
            }

            // Persist prices as { nozzleNumber(int) : priceText(string) }
            var dict = post.Nozzles.ToDictionary(n => n.Number, n => n.PriceText);
            string json = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(GetPricesFilePath(post), json, System.Text.Encoding.UTF8);
            AddLog("SYS", $"Prices saved for {post.Header}:");
            foreach (NozzleViewModel n in post.Nozzles)
            {
                AddLog("SYS", $"  Nozzle {n.Number} ({n.ProductName}): {n.PriceText} (raw={n.ConfiguredPriceRaw})");
            }
        }
        catch (Exception ex)
        {
            AddLog("SYS", $"ERROR saving prices for {post.Header}: {ex.Message}");
        }
    }

    private static string GetPricesFilePath(PostViewModel post)
    {
        string dir = AppContext.BaseDirectory;
        return Path.Combine(dir, $"prices_post_{post.AddressLabel}.json");
    }

    private static void LoadPricesFromFile(PostViewModel post)
    {
        string path = GetPricesFilePath(post);
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            string json = File.ReadAllText(path, System.Text.Encoding.UTF8);
            var dict = JsonSerializer.Deserialize<Dictionary<int, string>>(json);
            if (dict is null) return;
            foreach (NozzleViewModel nozzle in post.Nozzles)
            {
                if (dict.TryGetValue(nozzle.Number, out string? priceText) && priceText is not null)
                {
                    nozzle.PriceText = priceText;
                }
            }
        }
        catch
        {
            // If file is corrupt, silently ignore and use defaults
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
