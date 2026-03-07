using System;
using System.Collections.Generic;
using Tatsuno.Model;

namespace Tatsuno.Protocol;

public static class TatsunoQueryParser
{
    public static bool TryParse(string payload, out TatsunoParsedMessage? message)
    {
        message = null;

        if (string.IsNullOrWhiteSpace(payload) || payload.Length < 4)
        {
            return false;
        }

        if (!payload.StartsWith("@Q", StringComparison.Ordinal))
        {
            return false;
        }

        string code = payload.Substring(2, 2);
        switch (code)
        {
            case "60":
                if (payload.Length < 6)
                {
                    return false;
                }

                message = new TatsunoPumpConditionMessage(
                    ParseControllability(payload[4]),
                    ParseDigit(payload[5]),
                    payload);
                return true;

            case "61":
                if (payload.Length < 23)
                {
                    return false;
                }

                if (!int.TryParse(payload.Substring(5, 6), out int volumeRaw)) return false;
                if (!int.TryParse(payload.Substring(12, 4), out int priceRaw)) return false;
                if (!int.TryParse(payload.Substring(16, 6), out int amountRaw)) return false;

                message = new TatsunoPumpStatusMessage(
                    ParseCondition(payload[4]),
                    volumeRaw,
                    ParseUnitPriceFlag(payload[11]),
                    priceRaw,
                    amountRaw,
                    ParseDigit(payload[22]),
                    ParseProduct(payload.Length > 23 ? payload[23] : '0'),
                    ParseIndication(payload.Length > 24 ? payload[24] : '0'),
                    payload);
                return true;

            case "62":
                if (payload.Length < 5)
                {
                    return false;
                }

                message = new TatsunoReceiveErrorMessage(ParseDigit(payload[4]), payload);
                return true;

            case "65":
                if (payload.Length < 8)
                {
                    return false;
                }

                string productMap = payload.Substring(4, Math.Min(6, payload.Length - 4));
                int dataStart = 4 + productMap.Length;
                string rawBlocks = payload.Substring(dataStart);
                List<TatsunoTotalsEntry> entries = new();
                int blocks = rawBlocks.Length / 20;
                for (int i = 0; i < blocks; i++)
                {
                    string block = rawBlocks.Substring(i * 20, 20);
                    if (!long.TryParse(block.Substring(0, 10), out long volumeLong)) continue;
                    if (!long.TryParse(block.Substring(10, 10), out long amountLong)) continue;
                    entries.Add(new TatsunoTotalsEntry(
                        i + 1,
                        i < productMap.Length ? ParseProduct(productMap[i]) : TatsunoProductCode.None,
                        (int)Math.Min(int.MaxValue, volumeLong),
                        (int)Math.Min(int.MaxValue, amountLong)));
                }

                message = new TatsunoTotalsMessage(entries, payload);
                return true;

            default:
                return false;
        }
    }

    private static int ParseDigit(char c) => char.IsDigit(c) ? c - '0' : 0;

    private static TatsunoPumpCondition ParseCondition(char c) => c switch
    {
        '0' => TatsunoPumpCondition.NozzleStored,
        '1' => TatsunoPumpCondition.NozzleLifted,
        '3' => TatsunoPumpCondition.Fuelling,
        '4' => TatsunoPumpCondition.Finished,
        _ => TatsunoPumpCondition.Unknown
    };

    private static TatsunoPumpControllability ParseControllability(char c) => c switch
    {
        '1' => TatsunoPumpControllability.PowerOn,
        '2' => TatsunoPumpControllability.Controllable,
        '3' => TatsunoPumpControllability.Uncontrollable,
        _ => TatsunoPumpControllability.Unknown
    };

    private static TatsunoUnitPriceFlag ParseUnitPriceFlag(char c) => c switch
    {
        '1' => TatsunoUnitPriceFlag.Invalid,
        '2' => TatsunoUnitPriceFlag.Valid,
        '3' => TatsunoUnitPriceFlag.Credit,
        '9' => TatsunoUnitPriceFlag.SelectedAtPump,
        _ => TatsunoUnitPriceFlag.Unknown
    };

    private static TatsunoProductCode ParseProduct(char c) => c switch
    {
        '1' => TatsunoProductCode.HighOctane,
        '2' => TatsunoProductCode.Regular,
        '3' => TatsunoProductCode.Diesel,
        '4' => TatsunoProductCode.Kerosene,
        '5' => TatsunoProductCode.LeadedHighOctane,
        '6' => TatsunoProductCode.LeadedRegular,
        _ => TatsunoProductCode.None
    };

    private static TatsunoIndicationType ParseIndication(char c) => c switch
    {
        '1' => TatsunoIndicationType.Primary,
        '2' => TatsunoIndicationType.Secondary,
        _ => TatsunoIndicationType.Unknown
    };
}
