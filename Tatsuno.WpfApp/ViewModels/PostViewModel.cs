using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Media;
using Tatsuno.Model;
using Tatsuno.Protocol;
using Tatsuno.WpfApp.Infrastructure;

namespace Tatsuno.WpfApp.ViewModels;

public sealed class PostViewModel : ObservableObject
{
    private bool _isSelected;
    private string _statusText = "Ожидание";
    private string _controllabilityText = "—";
    private string _liveAmountText = "0";
    private string _liveVolumeText = "0,00";
    private string _livePriceText = "0";
    private string _activeNozzleText = "—";
    private string _lastPayload = string.Empty;
    private string _lastUpdateText = "—";
    private string _presetVolumeText = "";
    private string _presetAmountText = "";
    private bool _isUpdatingPreset;
    private bool _lastEditWasVolume = true;
    private bool _isBusy;
    private bool _commsLost;
    private DateTime _lastCommsUtc = DateTime.MinValue;
    private int? _pendingStartNozzleNumber;

    public PostViewModel(int postIndex, string addressLabel, int nozzlesCount)
    {
        PostIndex = postIndex;
        AddressLabel = addressLabel;
        Engine = new TatsunoControllerEngine(addressLabel, nozzlesCount);
        Nozzles = new ObservableCollection<NozzleViewModel>();

        string[] defaults = ["A-80", "A-92", "A-95", "ДТ", "Керосин", "Product-6"];
        int[] prices = [8500, 12500, 14000, 0, 0, 0];
        for (int i = 1; i <= nozzlesCount; i++)
        {
            var nozzle = new NozzleViewModel(i, defaults.ElementAtOrDefault(i - 1) ?? $"Пистолет-{i}", prices.ElementAtOrDefault(i - 1));
            nozzle.Selected += HandleNozzleSelected;
            Nozzles.Add(nozzle);
        }

        if (Nozzles.Count > 0)
        {
            Nozzles[0].IsSelected = true;
        }

        SelectCommand = new RelayCommand(() => Selected?.Invoke(this));
        PostStartCommand = new RelayCommand(() => StartRequested?.Invoke(this), () => !IsBusy);
        PostCancelCommand = new RelayCommand(() => CancelRequested?.Invoke(this));
        PostStatusCommand = new RelayCommand(() => StatusRequested?.Invoke(this));
        PostTotalsCommand = new RelayCommand(() => TotalsRequested?.Invoke(this));
        PostLockCommand = new RelayCommand(() => LockRequested?.Invoke(this));
        PostReleaseCommand = new RelayCommand(() => ReleaseRequested?.Invoke(this));
        SavePricesCommand = new RelayCommand(() => SavePricesRequested?.Invoke(this));
    }

    public event Action<PostViewModel>? Selected;
    public event Action<PostViewModel>? StartRequested;
    public event Action<PostViewModel>? CancelRequested;
    public event Action<PostViewModel>? StatusRequested;
    public event Action<PostViewModel>? TotalsRequested;
    public event Action<PostViewModel>? LockRequested;
    public event Action<PostViewModel>? ReleaseRequested;
    public event Action<PostViewModel>? SavePricesRequested;

    public TatsunoControllerEngine Engine { get; }
    public int PostIndex { get; }
    public string AddressLabel { get; }
    public ObservableCollection<NozzleViewModel> Nozzles { get; }

    public RelayCommand SelectCommand { get; }
    public RelayCommand PostStartCommand { get; }
    public RelayCommand PostCancelCommand { get; }
    public RelayCommand PostStatusCommand { get; }
    public RelayCommand PostTotalsCommand { get; }
    public RelayCommand PostLockCommand { get; }
    public RelayCommand PostReleaseCommand { get; }
    public RelayCommand SavePricesCommand { get; }

    public string Header => $"ТРК Сторона А (Адрес {AddressLabel})";

    /// <summary>True when last user edit was in the volume field; false if amount field.</summary>
    public bool LastEditWasVolume => _lastEditWasVolume;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                Raise(nameof(BorderBrush));
            }
        }
    }

    public Brush BorderBrush => IsSelected
        ? new SolidColorBrush(Color.FromRgb(59, 130, 246))
        : new SolidColorBrush(Color.FromRgb(229, 231, 235));

    public bool CommsLost
    {
        get => _commsLost;
        private set
        {
            if (SetProperty(ref _commsLost, value))
            {
                Raise(nameof(StatusBrush));
                Raise(nameof(StatusText));
            }
        }
    }

    public Brush StatusBrush => CommsLost
        ? new SolidColorBrush(Color.FromRgb(239, 68, 68))  // Red for НЕТ СВЯЗИ
        : _statusText switch
        {
            "ГОТОВ" => new SolidColorBrush(Color.FromRgb(34, 197, 94)),
            "ПИСТОЛЕТ ПОДНЯТ" => new SolidColorBrush(Color.FromRgb(234, 179, 8)),
            "ОТПУСК" => new SolidColorBrush(Color.FromRgb(59, 130, 246)),
            "ЗАВЕРШЕНО" => new SolidColorBrush(Color.FromRgb(107, 114, 128)),
            _ => new SolidColorBrush(Color.FromRgb(156, 163, 175))
        };

    /// <summary>Preset volume field (liters, e.g. "5,00"). Auto-calculates amount.</summary>
    public string PresetVolumeText
    {
        get => _presetVolumeText;
        set
        {
            if (!SetProperty(ref _presetVolumeText, value)) return;
            _lastEditWasVolume = true;
            if (_isUpdatingPreset) return;
            _isUpdatingPreset = true;
            try
            {
                NozzleViewModel? nozzle = SelectedNozzle;
                int priceRaw = nozzle?.ConfiguredPriceRaw ?? 0;
                if (priceRaw > 0)
                {
                    int volumeRaw = TatsunoValueFormatter.ParseDisplayedVolumeToRaw(value);
                    // amount_displayed = volumeRaw * priceRaw / 100 * 10
                    // volumeRaw is in centilitres (100 = 1L), priceRaw is display/10
                    // amountRaw = volumeRaw * priceRaw / 100
                    int amountRaw = volumeRaw * priceRaw / 100;
                    PresetAmountText = TatsunoValueFormatter.FormatMoney(amountRaw);
                }
            }
            finally
            {
                _isUpdatingPreset = false;
            }
        }
    }

    /// <summary>Preset amount field (sum, e.g. "42 500"). Auto-calculates volume.</summary>
    public string PresetAmountText
    {
        get => _presetAmountText;
        set
        {
            if (!SetProperty(ref _presetAmountText, value)) return;
            if (!_isUpdatingPreset) _lastEditWasVolume = false;
            if (_isUpdatingPreset) return;
            _isUpdatingPreset = true;
            try
            {
                NozzleViewModel? nozzle = SelectedNozzle;
                int priceRaw = nozzle?.ConfiguredPriceRaw ?? 0;
                if (priceRaw > 0)
                {
                    int amountRaw = TatsunoValueFormatter.ParseDisplayedMoneyToRaw(value);
                    // volumeRaw = amountRaw * 100 / priceRaw
                    int volumeRaw = amountRaw * 100 / priceRaw;
                    PresetVolumeText = TatsunoValueFormatter.FormatVolume(volumeRaw);
                }
            }
            finally
            {
                _isUpdatingPreset = false;
            }
        }
    }

    /// <summary>Transaction in progress — locks nozzle switching and preset editing.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                PostStartCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string StatusText
    {
        get => CommsLost ? "НЕТ СВЯЗИ" : _statusText;
        private set
        {
            if (SetProperty(ref _statusText, value))
            {
                Raise(nameof(StatusBrush));
            }
        }
    }

    public string ControllabilityText
    {
        get => _controllabilityText;
        private set => SetProperty(ref _controllabilityText, value);
    }

    public string LiveAmountText
    {
        get => _liveAmountText;
        private set => SetProperty(ref _liveAmountText, value);
    }

    public string LiveVolumeText
    {
        get => _liveVolumeText;
        private set => SetProperty(ref _liveVolumeText, value);
    }

    public string LivePriceText
    {
        get => _livePriceText;
        private set => SetProperty(ref _livePriceText, value);
    }

    public string ActiveNozzleText
    {
        get => _activeNozzleText;
        private set => SetProperty(ref _activeNozzleText, value);
    }

    /// <summary>Active nozzle + product display for live counters, e.g. "Пистолет 2 — A-92".</summary>
    public string ActiveNozzleProductText
    {
        get
        {
            int num = Engine.Snapshot.ActiveNozzleNumber;
            if (num <= 0) return "";
            NozzleViewModel? nozzle = Nozzles.FirstOrDefault(n => n.Number == num);
            return nozzle is not null ? $"Пистолет {nozzle.Number} — {nozzle.ProductName}" : $"Пистолет {num}";
        }
    }

    public string LastPayload
    {
        get => _lastPayload;
        private set => SetProperty(ref _lastPayload, value);
    }

    public string LastUpdateText
    {
        get => _lastUpdateText;
        private set => SetProperty(ref _lastUpdateText, value);
    }

    /// <summary>Pending nozzle number after Start button press, waiting for lift confirmation.</summary>
    public int? PendingStartNozzleNumber
    {
        get => _pendingStartNozzleNumber;
        set
        {
            if (SetProperty(ref _pendingStartNozzleNumber, value))
            {
                Raise(nameof(HasPendingAuthorization));
            }
        }
    }

    /// <summary>True when there is a pending authorization waiting for nozzle lift.</summary>
    public bool HasPendingAuthorization => _pendingStartNozzleNumber.HasValue;

    public bool HasLiftedNozzle => Nozzles.Any(n => n.IsLifted);

    public NozzleViewModel? SelectedNozzle => Nozzles.FirstOrDefault(n => n.IsSelected);

    public void ApplySnapshot()
    {
        PostSnapshot snapshot = Engine.Snapshot;
        StatusText = snapshot.Condition switch
        {
            TatsunoPumpCondition.NozzleStored => "ГОТОВ",
            TatsunoPumpCondition.NozzleLifted => "ПИСТОЛЕТ ПОДНЯТ",
            TatsunoPumpCondition.Fuelling => "ОТПУСК",
            TatsunoPumpCondition.Finished => "ЗАВЕРШЕНО",
            _ => "НЕИЗВЕСТНО"
        };

        ControllabilityText = snapshot.Controllability switch
        {
            TatsunoPumpControllability.PowerOn => "Power On",
            TatsunoPumpControllability.Controllable => "Управляемая",
            TatsunoPumpControllability.Uncontrollable => "Неуправляемая",
            _ => "—"
        };

        LiveAmountText = TatsunoValueFormatter.FormatMoney(snapshot.CurrentAmountRaw);
        LiveVolumeText = TatsunoValueFormatter.FormatVolume(snapshot.CurrentVolumeRaw);
        LivePriceText = TatsunoValueFormatter.FormatMoney(snapshot.CurrentUnitPriceRaw);
        ActiveNozzleText = snapshot.ActiveNozzleNumber > 0 ? $"Пистолет {snapshot.ActiveNozzleNumber}" : "—";
        LastPayload = snapshot.LastPayload;
        LastUpdateText = snapshot.LastUpdatedLocal == default ? "—" : snapshot.LastUpdatedLocal.ToString("HH:mm:ss.fff");

        foreach (NozzleSnapshot nozzleSnapshot in snapshot.Nozzles)
        {
            NozzleViewModel? vm = Nozzles.FirstOrDefault(n => n.Number == nozzleSnapshot.Number);
            vm?.ApplySnapshot(nozzleSnapshot);
        }

        // Lock nozzle switching during active transaction
        bool isTransacting = snapshot.Condition is TatsunoPumpCondition.Fuelling or TatsunoPumpCondition.NozzleLifted;
        foreach (NozzleViewModel nozzle in Nozzles)
        {
            nozzle.IsLocked = isTransacting && !nozzle.IsLifted;
        }
        IsBusy = isTransacting;

        // Only auto-select nozzle when it's physically active (lifted or fueling).
        if (snapshot.ActiveNozzleNumber > 0 &&
            snapshot.Condition is TatsunoPumpCondition.NozzleLifted or TatsunoPumpCondition.Fuelling)
        {
            SelectNozzle(snapshot.ActiveNozzleNumber);
        }

        Raise(nameof(HasLiftedNozzle));
        Raise(nameof(ActiveNozzleProductText));
    }

    public void SelectNozzle(int number)
    {
        foreach (NozzleViewModel nozzle in Nozzles)
        {
            nozzle.IsSelected = nozzle.Number == number;
        }
        // BUG FIX: raise SelectedNozzle so PresetVolumeText/PresetAmountText
        // cross-calculations use the correct nozzle after auto-selection
        Raise(nameof(SelectedNozzle));
    }

    /// <summary>Called on every successful RX from this ТРК (EOT, frame, etc.).</summary>
    public void MarkCommsReceived()
    {
        _lastCommsUtc = DateTime.UtcNow;
        CommsLost = false;
    }

    /// <summary>
    /// Check if communication has been lost. Called periodically by watchdog timer.
    /// </summary>
    public void CheckComms(TimeSpan timeout)
    {
        // BUG FIX: when _lastCommsUtc == MinValue we have never received data yet —
        // this is normal right after connecting.  Do NOT flag as lost; just wait.
        if (_lastCommsUtc == DateTime.MinValue)
            return;

        CommsLost = (DateTime.UtcNow - _lastCommsUtc) > timeout;
    }

    /// <summary>
    /// Auto-authorize when correct nozzle is lifted after Start button press.
    /// If wrong nozzle is lifted, cancel authorization.
    /// </summary>
    public void TriggerAutoAuthorize()
    {
        if (!HasPendingAuthorization)
        {
            return;
        }

        int liftedNozzleNumber = Engine.Snapshot.ActiveNozzleNumber;
        int? pendingNozzleNumber= PendingStartNozzleNumber;

        if (pendingNozzleNumber == null || liftedNozzleNumber == 0)
        {
            return;
        }

        if (liftedNozzleNumber == pendingNozzleNumber)
        {
            // Correct nozzle lifted — allow transaction to continue
            // Do NOT send A11 again (already sent in HandlePostStart)
            PendingStartNozzleNumber = null;
        }
        else
        {
            // Wrong nozzle lifted — cancel authorization
            var payload = TatsunoCodec.BuildCancelAuthorizationPayload();
            Engine.Enqueue(payload, TatsunoCommandKind.CancelAuthorization, "Cancel wrong nozzle authorization");
            PendingStartNozzleNumber = null;
        }
    }

    private void HandleNozzleSelected(NozzleViewModel nozzle)
    {
        SelectNozzle(nozzle.Number);
    }
}
