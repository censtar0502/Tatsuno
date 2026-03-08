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

    private readonly object _sync = new();
    private readonly TatsunoStreamDecoder _decoder = new();
    private readonly DispatcherTimer _watchdog;
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
    private string _presetDisplayText = "12500";
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
        StartAmountCommand = new RelayCommand(StartAmountPreset, () => DashboardPost is not null);
        StartVolumeCommand = new RelayCommand(StartVolumePreset, () => DashboardPost is not null);
        CancelCommand = new RelayCommand(QueueCancel, () => DashboardPost is not null);
        RequestStatusCommand = new RelayCommand(QueueStatus, () => DashboardPost is not null);
        RequestTotalsCommand = new RelayCommand(QueueTotals, () => DashboardPost is not null);
        LockPumpCommand = new RelayCommand(QueueLockPump, () => DashboardPost is not null);
        ReleasePumpCommand = new RelayCommand(QueueReleasePump, () => DashboardPost is not null);

        _watchdog = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _watchdog.Tick += OnWatchdogTick;

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
    public RelayCommand StartAmountCommand { get; }
    public RelayCommand StartVolumeCommand { get; }
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

    public string PresetDisplayText
    {
        get => _presetDisplayText;
        set => SetProperty(ref _presetDisplayText, value);
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
                StartAmountCommand.RaiseCanExecuteChanged();
                StartVolumeCommand.RaiseCanExecuteChanged();
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
        post.StartAmountRequested += HandlePostStartAmount;
        post.StartVolumeRequested += HandlePostStartVolume;
        post.CancelRequested += HandlePostCancel;
        post.StatusRequested += HandlePostStatus;
        post.TotalsRequested += HandlePostTotals;
        post.LockRequested += HandlePostLock;
        post.ReleaseRequested += HandlePostRelease;
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
            AddLog("SYS", $"Connected {SelectedPort}");

            // Request initial totals from ТРК (reference program does A20 only, no A15)
            // Status "ГОТОВ" is determined from EOT responses to polling
            PostViewModel? trk = Posts.FirstOrDefault();
            if (trk is not null)
            {
                trk.Engine.Enqueue(TatsunoCodec.BuildRequestTotalsPayload(), TatsunoCommandKind.RequestTotals, "initial totals", allowedWhenUncontrollable: true);
                AddLog("SYS", "Queued initial A20 (totals)");
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

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (Posts.Count == 0 || _session is null)
                    {
                        await Task.Delay(150, token);
                        continue;
                    }

                    _roundRobinIndex = (_roundRobinIndex + 1) % Posts.Count;
                    PostViewModel post = Posts[_roundRobinIndex];
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

    private void StartAmountPreset()
    {
        PostViewModel? post = DashboardPost;
        NozzleViewModel? nozzle = post?.SelectedNozzle;
        if (post is null || nozzle is null) return;

        int amountRaw = TatsunoValueFormatter.ParseDisplayedMoneyToRaw(PresetDisplayText);
        var products = BuildSelectedNozzlePrice(post, nozzle);
        string payload = TatsunoCodec.BuildAuthorizeMultiPricePayload(
            TatsunoAuthorizationTerm.VolumeLimited,
            TatsunoPresetKind.Amount,
            amountRaw,
            products);

        post.Engine.Enqueue(payload, TatsunoCommandKind.AuthorizeMultiPrice, $"authorize amount nozzle {nozzle.Number} amount={PresetDisplayText} raw={amountRaw}");
        AddLog("SYS", $"Queue {post.Header}: amount preset {PresetDisplayText} (raw={amountRaw}) nozzle {nozzle.Number} price={nozzle.ConfiguredPriceRaw}");
    }

    private void StartVolumePreset()
    {
        PostViewModel? post = DashboardPost;
        NozzleViewModel? nozzle = post?.SelectedNozzle;
        if (post is null || nozzle is null) return;

        int volumeRaw = TatsunoValueFormatter.ParseDisplayedVolumeToRaw(PresetDisplayText);
        var products = BuildSelectedNozzlePrice(post, nozzle);
        string payload = TatsunoCodec.BuildAuthorizeMultiPricePayload(
            TatsunoAuthorizationTerm.VolumeLimited,
            TatsunoPresetKind.Volume,
            volumeRaw,
            products);

        post.Engine.Enqueue(payload, TatsunoCommandKind.AuthorizeMultiPrice, $"authorize volume nozzle {nozzle.Number} volume={PresetDisplayText}");
        AddLog("SYS", $"Queue {post.Header}: volume preset {PresetDisplayText} nozzle {nozzle.Number}");
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
    private static List<(TatsunoUnitPriceFlag Flag, int UnitPriceRaw)> BuildSelectedNozzlePrice(PostViewModel post, NozzleViewModel selectedNozzle)
    {
        var products = new List<(TatsunoUnitPriceFlag Flag, int UnitPriceRaw)>();
        foreach (NozzleViewModel n in post.Nozzles)
        {
            if (n.Number == selectedNozzle.Number)
            {
                int raw = n.ConfiguredPriceRaw;
                products.Add(raw > 0 ? (TatsunoUnitPriceFlag.Credit, raw) : (TatsunoUnitPriceFlag.Unknown, 0));
            }
            else
            {
                products.Add((TatsunoUnitPriceFlag.Unknown, 0));
            }
        }
        return products;
    }

    private void HandlePostStartAmount(PostViewModel post)
    {
        NozzleViewModel? nozzle = post.SelectedNozzle;
        if (nozzle is null) return;

        int amountRaw = TatsunoValueFormatter.ParseDisplayedMoneyToRaw(post.PresetDisplayText);
        var products = BuildSelectedNozzlePrice(post, nozzle);
        string payload = TatsunoCodec.BuildAuthorizeMultiPricePayload(
            TatsunoAuthorizationTerm.VolumeLimited,
            TatsunoPresetKind.Amount,
            amountRaw,
            products);

        post.Engine.Enqueue(payload, TatsunoCommandKind.AuthorizeMultiPrice, $"authorize amount nozzle {nozzle.Number} amount={post.PresetDisplayText} raw={amountRaw}");
        AddLog("SYS", $"Queue {post.Header}: amount preset {post.PresetDisplayText} (raw={amountRaw}) nozzle {nozzle.Number} price={nozzle.ConfiguredPriceRaw}");
    }

    private void HandlePostStartVolume(PostViewModel post)
    {
        NozzleViewModel? nozzle = post.SelectedNozzle;
        if (nozzle is null) return;

        int volumeRaw = TatsunoValueFormatter.ParseDisplayedVolumeToRaw(post.PresetDisplayText);
        var products = BuildSelectedNozzlePrice(post, nozzle);
        string payload = TatsunoCodec.BuildAuthorizeMultiPricePayload(
            TatsunoAuthorizationTerm.VolumeLimited,
            TatsunoPresetKind.Volume,
            volumeRaw,
            products);

        post.Engine.Enqueue(payload, TatsunoCommandKind.AuthorizeMultiPrice, $"authorize volume nozzle {nozzle.Number} volume={post.PresetDisplayText}");
        AddLog("SYS", $"Queue {post.Header}: volume preset {post.PresetDisplayText} nozzle {nozzle.Number}");
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
        Raise(nameof(DashboardNozzles));
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
        _fileLog = new StreamWriter(path) { AutoFlush = true };
        LogFilePath = path;
    }

    private void CloseFileLog()
    {
        try { _fileLog?.Dispose(); } catch { }
        _fileLog = null;
        LogFilePath = "—";
    }

    private void WriteRawToFile(string prefix, ReadOnlySpan<byte> bytes)
    {
        try
        {
            _fileLog?.WriteLine($"<{DateTime.Now:yyyyMMddHHmmss.fff} {prefix}>\n{RenderBytes(bytes)}");
        }
        catch
        {
        }
    }

    private static string RenderBytes(ReadOnlySpan<byte> bytes)
    {
        return string.Concat(bytes.ToArray().Select(RenderByte));
    }

    private static string RenderByte(byte value) => value switch
    {
        0x00 => "<NUL>",
        0x02 => "<STX>",
        0x03 => "<ETX>",
        0x04 => "<EOT>",
        0x05 => "<ENQ>",
        0x10 => "<DLE>",
        0x15 => "<NAK>",
        _ when value >= 0x20 && value <= 0x7E => ((char)value).ToString(),
        _ => $"<0x{value:X2}>"
    };
}
