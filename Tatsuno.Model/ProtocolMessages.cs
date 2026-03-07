using System.Collections.Generic;

namespace Tatsuno.Model;

public abstract record TatsunoParsedMessage(string Payload);

public sealed record TatsunoPumpConditionMessage(
    TatsunoPumpControllability Controllability,
    int Contents,
    string RawPayload) : TatsunoParsedMessage(RawPayload);

public sealed record TatsunoPumpStatusMessage(
    TatsunoPumpCondition Condition,
    int VolumeRaw,
    TatsunoUnitPriceFlag UnitPriceFlag,
    int UnitPriceRaw,
    int AmountRaw,
    int NozzleNumber,
    TatsunoProductCode ProductCode,
    TatsunoIndicationType IndicationType,
    string RawPayload) : TatsunoParsedMessage(RawPayload);

public sealed record TatsunoReceiveErrorMessage(
    int ErrorCode,
    string RawPayload) : TatsunoParsedMessage(RawPayload);

public sealed record TatsunoTotalsEntry(
    int NozzleNumber,
    TatsunoProductCode ProductCode,
    int TotalVolumeRaw,
    int TotalAmountRaw);

public sealed record TatsunoTotalsMessage(
    IReadOnlyList<TatsunoTotalsEntry> Entries,
    string RawPayload) : TatsunoParsedMessage(RawPayload);
