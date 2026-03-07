using System;
using System.Collections.Generic;

namespace Tatsuno.Protocol;

public enum TatsunoRxItemType
{
    ControlByte = 0,
    Dle0 = 1,
    Dle1 = 2,
    Frame = 3
}

public sealed class TatsunoRxItem
{
    public required TatsunoRxItemType Type { get; init; }
    public byte Control { get; init; }
    public byte[]? PayloadAscii { get; init; }
    public byte Bcc { get; init; }
    public bool BccOk { get; init; }
    public string? PayloadString { get; init; }
    public DateTime TimestampLocal { get; init; }
}

public sealed class TatsunoStreamDecoder
{
    private enum DecoderState
    {
        Idle = 0,
        AfterDle = 1,
        InFrame = 2,
        ExpectBcc = 3
    }

    private readonly List<byte> _payload = new(capacity: 128);
    private DecoderState _state;

    public IReadOnlyList<TatsunoRxItem> Feed(ReadOnlySpan<byte> data)
    {
        List<TatsunoRxItem> items = new();

        for (int i = 0; i < data.Length; i++)
        {
            byte b = data[i];
            switch (_state)
            {
                case DecoderState.Idle:
                    if (b == TatsunoControlBytes.STX)
                    {
                        _payload.Clear();
                        _state = DecoderState.InFrame;
                    }
                    else if (b == TatsunoControlBytes.DLE)
                    {
                        _state = DecoderState.AfterDle;
                    }
                    else
                    {
                        items.Add(new TatsunoRxItem
                        {
                            Type = TatsunoRxItemType.ControlByte,
                            Control = b,
                            TimestampLocal = DateTime.Now
                        });
                    }
                    break;

                case DecoderState.AfterDle:
                    items.Add(new TatsunoRxItem
                    {
                        Type = b == TatsunoControlBytes.ASCII_0 ? TatsunoRxItemType.Dle0 : TatsunoRxItemType.Dle1,
                        Control = b,
                        TimestampLocal = DateTime.Now
                    });
                    _state = DecoderState.Idle;
                    break;

                case DecoderState.InFrame:
                    if (b == TatsunoControlBytes.ETX)
                    {
                        _state = DecoderState.ExpectBcc;
                    }
                    else if (b == TatsunoControlBytes.STX)
                    {
                        _payload.Clear();
                    }
                    else
                    {
                        _payload.Add(b);
                    }
                    break;

                case DecoderState.ExpectBcc:
                    byte[] payloadBytes = _payload.ToArray();
                    items.Add(new TatsunoRxItem
                    {
                        Type = TatsunoRxItemType.Frame,
                        PayloadAscii = payloadBytes,
                        PayloadString = System.Text.Encoding.ASCII.GetString(payloadBytes),
                        Bcc = b,
                        BccOk = TatsunoCodec.ValidateFrame(payloadBytes, b),
                        TimestampLocal = DateTime.Now
                    });
                    _payload.Clear();
                    _state = DecoderState.Idle;
                    break;
            }
        }

        return items;
    }
}
