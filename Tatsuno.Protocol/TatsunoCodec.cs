using System;
using System.Collections.Generic;
using System.Text;
using Tatsuno.Model;

namespace Tatsuno.Protocol;

public static class TatsunoCodec
{
    public static byte StationAddressByte(int addressNumber) => (byte)(0x3F + addressNumber);

    public static byte[] BuildPollRequest(byte stationAddress)
    {
        return new[] { TatsunoControlBytes.EOT, stationAddress, (byte)'Q', TatsunoControlBytes.ENQ };
    }

    public static byte[] BuildActionHandshake(byte stationAddress)
    {
        return new[] { TatsunoControlBytes.EOT, stationAddress, (byte)'A', TatsunoControlBytes.ENQ };
    }

    public static byte[] BuildAck1()
    {
        return new[] { TatsunoControlBytes.DLE, TatsunoControlBytes.ASCII_1 };
    }

    public static byte ComputeBcc(ReadOnlySpan<byte> payloadAscii)
    {
        byte value = 0;
        for (int i = 0; i < payloadAscii.Length; i++)
        {
            value ^= payloadAscii[i];
        }

        value ^= TatsunoControlBytes.ETX;
        return value;
    }

    public static byte[] BuildFrame(string payloadAscii)
    {
        if (payloadAscii is null)
        {
            throw new ArgumentNullException(nameof(payloadAscii));
        }

        byte[] payload = Encoding.ASCII.GetBytes(payloadAscii);
        byte bcc = ComputeBcc(payload);
        byte[] buffer = new byte[payload.Length + 3];
        buffer[0] = TatsunoControlBytes.STX;
        Buffer.BlockCopy(payload, 0, buffer, 1, payload.Length);
        buffer[^2] = TatsunoControlBytes.ETX;
        buffer[^1] = bcc;
        return buffer;
    }

    public static bool ValidateFrame(ReadOnlySpan<byte> payloadAscii, byte bcc)
    {
        return ComputeBcc(payloadAscii) == bcc;
    }

    /// <summary>
    /// Build A00 CRC acknowledgment payload for Q00 Power-ON handshake.
    /// Per Section 7-2 of Tatsuno protocol specification:
    /// 1. Parse received CRC hex data (4 chars = 2 bytes)
    /// 2. Add upper byte + lower byte
    /// 3. Convert result to 2-char uppercase hex
    /// </summary>
    public static string BuildCrcAcknowledgmentPayload(string crcHexData)
    {
        if (crcHexData.Length >= 4)
        {
            try
            {
                byte upper = Convert.ToByte(crcHexData.Substring(0, 2), 16);
                byte lower = Convert.ToByte(crcHexData.Substring(2, 2), 16);
                byte sum = (byte)(upper + lower);
                return $"@A00{sum:X2}";
            }
            catch (FormatException)
            {
                // If hex parsing fails, return empty acknowledgment
            }
        }

        return "@A0000";
    }

    public static string BuildRequestStatusPayload() => "@A15";
    public static string BuildRequestTotalsPayload() => "@A20";
    public static string BuildCancelAuthorizationPayload() => "@A12";
    public static string BuildLockPumpPayload() => "@A13";
    public static string BuildReleasePumpLockPayload() => "@A14";

    public static string BuildAuthorizeSinglePricePayload(
        TatsunoAuthorizationTerm term,
        TatsunoPresetKind presetKind,
        int presetValueRaw,
        TatsunoUnitPriceFlag unitPriceFlag,
        int unitPriceRaw)
    {
        string value = Math.Clamp(presetValueRaw, 0, 999999).ToString("D6");
        string price = Math.Clamp(unitPriceRaw, 0, 9999).ToString("D4");
        return $"@A10{(int)term}{(int)presetKind}{value}{(int)unitPriceFlag}{price}";
    }

    public static string BuildAuthorizeMultiPricePayload(
        TatsunoAuthorizationTerm term,
        TatsunoPresetKind presetKind,
        int presetValueRaw,
        IReadOnlyList<(TatsunoUnitPriceFlag Flag, int UnitPriceRaw)> products)
    {
        if (products is null)
        {
            throw new ArgumentNullException(nameof(products));
        }

        var builder = new StringBuilder();
        builder.Append("@A11");
        builder.Append((int)term);
        builder.Append((int)presetKind);
        // Clamp to 6-digit max (999999) to prevent field overflow in the protocol frame
        builder.Append(Math.Clamp(presetValueRaw, 0, 999999).ToString("D6"));

        for (int i = 0; i < 6; i++)
        {
            if (i < products.Count)
            {
                builder.Append((int)products[i].Flag);
                // Clamp to 4-digit max (9999) to prevent field overflow
                builder.Append(Math.Clamp(products[i].UnitPriceRaw, 0, 9999).ToString("D4"));
            }
            else
            {
                builder.Append('0');
                builder.Append("0000");
            }
        }

        return builder.ToString();
    }
}
