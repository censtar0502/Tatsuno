using System;
using System.Collections.Generic;
using System.Linq;

namespace Tatsuno.Model;

public sealed class PostSnapshot
{
    public PostSnapshot(string addressLabel, int nozzlesCount)
    {
        AddressLabel = addressLabel;
        Nozzles = Enumerable.Range(1, nozzlesCount)
            .Select(i => new NozzleSnapshot { Number = i })
            .ToList();
    }

    public string AddressLabel { get; }
    public TatsunoPumpCondition Condition { get; set; } = TatsunoPumpCondition.Unknown;
    public TatsunoPumpControllability Controllability { get; set; } = TatsunoPumpControllability.Unknown;
    public TatsunoUnitPriceFlag UnitPriceFlag { get; set; } = TatsunoUnitPriceFlag.Unknown;
    public TatsunoProductCode ActiveProduct { get; set; } = TatsunoProductCode.None;
    public TatsunoIndicationType IndicationType { get; set; } = TatsunoIndicationType.Unknown;
    public int ActiveNozzleNumber { get; set; }
    public int CurrentVolumeRaw { get; set; }
    public int CurrentAmountRaw { get; set; }
    public int CurrentUnitPriceRaw { get; set; }
    public int LastReceiveErrorCode { get; set; }
    public string LastPayload { get; set; } = string.Empty;
    public DateTime LastUpdatedLocal { get; set; }
    public IList<NozzleSnapshot> Nozzles { get; }
}
