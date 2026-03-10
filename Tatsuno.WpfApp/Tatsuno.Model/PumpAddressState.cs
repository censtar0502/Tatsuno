using System;

namespace Tatsuno.Model;

/// <summary>
/// Complete state of a pump at a specific address
/// </summary>
public sealed class PumpAddressState
{
    /// <summary>
    /// Pump address character (e.g., '6')
    /// </summary>
    public char Address { get; init; }

    // UI State
    
    /// <summary>
    /// Is this pump selected by user cursor?
    /// </summary>
    public bool IsSelectedByCursor { get; set; }

    /// <summary>
    /// Is this pump auto-activated due to nozzle lift?
    /// </summary>
    public bool IsAutoActiveByLift { get; set; }

    /// <summary>
    /// Currently active nozzle number (0 if none)
    /// </summary>
    public int ActiveNozzle { get; set; }

    // Display values (raw + decimals for proper formatting)
    
    /// <summary>
    /// Raw display amount value from dispenser
    /// </summary>
    public long CurrentDisplayAmountRaw { get; set; }

    /// <summary>
    /// Decimal places for display amount
    /// </summary>
    public int AmountDecimals { get; set; }

    /// <summary>
    /// Formatted amount for display
    /// </summary>
    public decimal CurrentDisplayAmountFormatted => AmountDecimals > 0
        ? CurrentDisplayAmountRaw / (decimal)Math.Pow(10, AmountDecimals)
        : CurrentDisplayAmountRaw;

    /// <summary>
    /// Raw display volume value
    /// </summary>
    public long CurrentDisplayVolumeRaw { get; set; }

    /// <summary>
    /// Decimal places for display volume
    /// </summary>
    public int VolumeDecimals { get; set; }

    /// <summary>
    /// Formatted volume for display (liters)
    /// </summary>
    public decimal CurrentDisplayVolumeFormatted => VolumeDecimals > 0
        ? CurrentDisplayVolumeRaw / (decimal)Math.Pow(10, VolumeDecimals)
        : CurrentDisplayVolumeRaw;

    /// <summary>
    /// Raw display price value
    /// </summary>
    public long CurrentDisplayPriceRaw { get; set; }

    /// <summary>
    /// Decimal places for display price
    /// </summary>
    public int PriceDecimals { get; set; }

    /// <summary>
    /// Formatted price for display (price per liter)
    /// </summary>
    public decimal CurrentDisplayPriceFormatted => PriceDecimals > 0
        ? CurrentDisplayPriceRaw / (decimal)Math.Pow(10, PriceDecimals)
        : CurrentDisplayPriceRaw;

    // Status information
    
    /// <summary>
    /// Last text code received (e.g., 10, 11, 13, 14, 51)
    /// </summary>
    public int LastTextCode { get; set; }

    /// <summary>
    /// Pump condition flags
    /// </summary>
    public PumpCondition LastCondition { get; set; }

    /// <summary>
    /// Last error code (if any)
    /// </summary>
    public int LastError { get; set; }

    /// <summary>
    /// Communication state machine state
    /// </summary>
    public LinkLayerState CommState { get; set; } = LinkLayerState.Idle;

    /// <summary>
    /// Transaction state machine state
    /// </summary>
    public PumpOperationalState TransactionState { get; set; } = PumpOperationalState.Unknown;

    /// <summary>
    /// Nozzles at this pump address
    /// </summary>
    public NozzleState[] Nozzles { get; init; } = Array.Empty<NozzleState>();

    /// <summary>
    /// Last update timestamp
    /// </summary>
    public DateTime LastUpdate { get; set; }

    /// <summary>
    /// Get the currently active nozzle state
    /// </summary>
    public NozzleState? GetActiveNozzleState()
    {
        if (ActiveNozzle == 0 || Nozzles.Length == 0)
            return null;
        
        return ActiveNozzle <= Nozzles.Length ? Nozzles[ActiveNozzle - 1] : null;
    }

    /// <summary>
    /// Create a formatted display string
    /// </summary>
    public override string ToString()
    {
        return $"Pump {Address}: State={TransactionState}, ActiveNozzle={ActiveNozzle}, Amount={CurrentDisplayAmountFormatted}";
    }
}
