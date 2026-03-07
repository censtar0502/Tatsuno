using System;

namespace Tatsuno.Model;

/// <summary>
/// State of a single nozzle at a pump address
/// </summary>
public sealed class NozzleState
{
    /// <summary>
    /// Nozzle number (1, 2, 3, etc.)
    /// </summary>
    public int NozzleNumber { get; init; }

    /// <summary>
    /// Product code assigned to this nozzle
    /// </summary>
    public int ProductCode { get; set; }

    /// <summary>
    /// Raw price value from dispenser (e.g., 212500)
    /// </summary>
    public long PriceRaw { get; set; }

    /// <summary>
    /// Number of decimal places for price (e.g., 3 = 212.500)
    /// </summary>
    public int PriceDecimals { get; set; }

    /// <summary>
    /// Formatted price for display: PriceRaw / 10^PriceDecimals
    /// </summary>
    public decimal PriceFormatted => PriceDecimals > 0 
        ? PriceRaw / (decimal)Math.Pow(10, PriceDecimals) 
        : PriceRaw;

    /// <summary>
    /// Is nozzle currently lifted?
    /// </summary>
    public bool IsLifted { get; set; }

    /// <summary>
    /// Is nozzle authorized for fueling?
    /// </summary>
    public bool IsAuthorized { get; set; }

    /// <summary>
    /// Type of preset (None/Amount/Volume)
    /// </summary>
    public TransactionType PresetType { get; set; }

    /// <summary>
    /// Raw preset value (amount or volume)
    /// </summary>
    public long PresetRaw { get; set; }

    /// <summary>
    /// Decimal places for preset
    /// </summary>
    public int PresetDecimals { get; set; }

    // Totals information (from Q651 frames)
    
    /// <summary>
    /// Raw total volume dispensed
    /// </summary>
    public long LastTotalsVolumeRaw { get; set; }

    /// <summary>
    /// Decimal places for totals volume
    /// </summary>
    public int TotalsVolumeDecimals { get; set; }

    /// <summary>
    /// Raw total amount (money)
    /// </summary>
    public long LastTotalsAmountRaw { get; set; }

    /// <summary>
    /// Decimal places for totals amount
    /// </summary>
    public int TotalsAmountDecimals { get; set; }

    /// <summary>
    /// Last update timestamp
    /// </summary>
    public DateTime LastUpdate { get; set; }

    /// <summary>
    /// Create a formatted display string for this nozzle
    /// </summary>
    public override string ToString()
    {
        return $"Nozzle {NozzleNumber}: {(IsLifted ? "LIFTED" : "lowered")}, Price={PriceFormatted}, Product={ProductCode}";
    }
}
