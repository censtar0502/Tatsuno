using System;

namespace Tatsuno.Model;

public sealed class PostStatus
{
    public required char Address { get; init; }

    /// <summary>
    /// Protocol status code from Q6xx family:
    /// 10, 11, 13, 14, 51, ...
    /// </summary>
    public required int Code { get; init; }

    // Raw numeric fields (for proper scaling)
    
    /// <summary>
    /// Raw volume value from dispenser (e.g., 372)
    /// </summary>
    public long VolumeRaw { get; init; }

    /// <summary>
    /// Decimal places for volume (e.g., 2 = 3.72 L)
    /// </summary>
    public int VolumeDecimals { get; init; } = 2;

    /// <summary>
    /// Formatted volume in liters
    /// </summary>
    public double? VolumeLiters => VolumeDecimals > 0 
        ? VolumeRaw / Math.Pow(10, VolumeDecimals) 
        : VolumeRaw;

    /// <summary>
    /// Raw price value (e.g., 212500)
    /// </summary>
    public long PriceRaw { get; init; }

    /// <summary>
    /// Decimal places for price
    /// </summary>
    public int PriceDecimals { get; init; } = 3;

    /// <summary>
    /// Formatted price per liter
    /// </summary>
    public decimal? PricePerLiter => PriceDecimals > 0
        ? PriceRaw / (decimal)Math.Pow(10, PriceDecimals)
        : PriceRaw;

    /// <summary>
    /// Raw amount (money) value
    /// </summary>
    public long AmountRaw { get; init; }

    /// <summary>
    /// Decimal places for amount
    /// </summary>
    public int AmountDecimals { get; init; } = 2;

    /// <summary>
    /// Formatted amount (total money)
    /// </summary>
    public decimal? Amount => AmountDecimals > 0
        ? AmountRaw / (decimal)Math.Pow(10, AmountDecimals)
        : AmountRaw;

    /// <summary>
    /// 0 = none, 1..N = active nozzle.
    /// </summary>
    public int Nozzle { get; init; }

    /// <summary>
    /// Pump condition flags
    /// </summary>
    public PumpCondition Condition { get; init; } = PumpCondition.Normal;

    /// <summary>
    /// Product code (if available)
    /// </summary>
    public int ProductCode { get; init; }

    public required DateTime Timestamp { get; init; }
    public required string RawPayload { get; init; }
}
