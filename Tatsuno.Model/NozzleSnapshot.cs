namespace Tatsuno.Model;

public sealed class NozzleSnapshot
{
    public int Number { get; init; }
    public TatsunoProductCode ProductCode { get; set; } = TatsunoProductCode.None;
    public bool IsLifted { get; set; }
    public int ConfiguredPriceRaw { get; set; }
    public int TotalVolumeRaw { get; set; }
    public int TotalAmountRaw { get; set; }
}
