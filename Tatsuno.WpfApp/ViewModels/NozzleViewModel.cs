using System.Windows.Media;
using Tatsuno.Model;
using Tatsuno.WpfApp.Infrastructure;

namespace Tatsuno.WpfApp.ViewModels;

public sealed class NozzleViewModel : ObservableObject
{
    private string _productName;
    private string _priceText;
    private string _totalsVolumeText = "0,00";
    private string _totalsAmountText = "0";
    private bool _isLifted;
    private bool _isSelected;

    public NozzleViewModel(int number, string productName, int configuredPriceDisplay)
    {
        Number = number;
        _productName = productName;
        _priceText = configuredPriceDisplay.ToString("N0", new System.Globalization.CultureInfo("ru-RU"));
        SelectCommand = new RelayCommand(() => Selected?.Invoke(this));
    }

    public event Action<NozzleViewModel>? Selected;

    public int Number { get; }

    public RelayCommand SelectCommand { get; }

    public string ProductName
    {
        get => _productName;
        set => SetProperty(ref _productName, value);
    }

    public string PriceText
    {
        get => _priceText;
        set => SetProperty(ref _priceText, value);
    }

    public string TotalsVolumeText
    {
        get => _totalsVolumeText;
        set => SetProperty(ref _totalsVolumeText, value);
    }

    public string TotalsAmountText
    {
        get => _totalsAmountText;
        set => SetProperty(ref _totalsAmountText, value);
    }

    public bool IsLifted
    {
        get => _isLifted;
        set
        {
            if (SetProperty(ref _isLifted, value))
            {
                Raise(nameof(ProductBrush));
            }
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                Raise(nameof(ProductBrush));
            }
        }
    }

    public Brush ProductBrush => IsLifted
        ? Brushes.IndianRed
        : IsSelected
            ? Brushes.RoyalBlue
            : new SolidColorBrush(Color.FromRgb(137, 153, 175));

    public int ConfiguredPriceRaw => TatsunoValueFormatter.ParseDisplayedMoneyToRaw(PriceText);

    public void ApplySnapshot(NozzleSnapshot snapshot)
    {
        IsLifted = snapshot.IsLifted;
        TotalsVolumeText = TatsunoValueFormatter.FormatVolume(snapshot.TotalVolumeRaw);
        TotalsAmountText = TatsunoValueFormatter.FormatMoney(snapshot.TotalAmountRaw);
        if (snapshot.ProductCode != TatsunoProductCode.None)
        {
            ProductName = snapshot.ProductCode switch
            {
                TatsunoProductCode.HighOctane => "A-80",
                TatsunoProductCode.Regular => "A-92",
                TatsunoProductCode.Diesel => "ДТ",
                TatsunoProductCode.Kerosene => "Керосин",
                TatsunoProductCode.LeadedHighOctane => "A-80 Lead",
                TatsunoProductCode.LeadedRegular => "A-92 Lead",
                _ => ProductName
            };
        }
    }
}
