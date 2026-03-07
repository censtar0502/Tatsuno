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

    public PostViewModel(int postIndex, string addressLabel, int nozzlesCount)
    {
        PostIndex = postIndex;
        AddressLabel = addressLabel;
        Engine = new TatsunoControllerEngine(addressLabel, nozzlesCount);
        Nozzles = new ObservableCollection<NozzleViewModel>();

        string[] defaults = ["A-80", "A-92", "A-95", "ДТ", "Керосин", "Product-6"];
        int[] prices = [8500, 12500, 13000, 0, 0, 0];
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
    }

    public event Action<PostViewModel>? Selected;

    public TatsunoControllerEngine Engine { get; }
    public int PostIndex { get; }
    public string AddressLabel { get; }
    public ObservableCollection<NozzleViewModel> Nozzles { get; }
    public RelayCommand SelectCommand { get; }

    public string Header => $"Пост {PostIndex} / Адрес {AddressLabel}";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                Raise(nameof(CardBrush));
                Raise(nameof(BorderBrush));
            }
        }
    }

    public Brush CardBrush => IsSelected
        ? new SolidColorBrush(Color.FromRgb(64, 111, 165))
        : new SolidColorBrush(Color.FromRgb(78, 114, 156));

    public Brush BorderBrush => new SolidColorBrush(Color.FromRgb(241, 178, 0));

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
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

        if (snapshot.ActiveNozzleNumber > 0)
        {
            SelectNozzle(snapshot.ActiveNozzleNumber);
        }

        Raise(nameof(HasLiftedNozzle));
    }

    public void SelectNozzle(int number)
    {
        foreach (NozzleViewModel nozzle in Nozzles)
        {
            nozzle.IsSelected = nozzle.Number == number;
        }
    }

    private void HandleNozzleSelected(NozzleViewModel nozzle)
    {
        SelectNozzle(nozzle.Number);
    }
}
