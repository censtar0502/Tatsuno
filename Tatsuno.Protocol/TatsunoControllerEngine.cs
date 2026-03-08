using System;
using System.Collections.Generic;
using System.Linq;
using Tatsuno.Model;

namespace Tatsuno.Protocol;

public enum TatsunoControllerActionKind
{
    None = 0,
    Poll = 1,
    SelectHandshake = 2
}

public sealed class TatsunoControllerAction
{
    public static readonly TatsunoControllerAction None = new(TatsunoControllerActionKind.None, Array.Empty<byte>(), string.Empty);

    public TatsunoControllerAction(TatsunoControllerActionKind kind, byte[] bytes, string description)
    {
        Kind = kind;
        Bytes = bytes;
        Description = description;
    }

    public TatsunoControllerActionKind Kind { get; }
    public byte[] Bytes { get; }
    public string Description { get; }
}

internal sealed class TatsunoPendingCommand
{
    public required TatsunoCommandKind Kind { get; init; }
    public required string Payload { get; init; }
    public required string Description { get; init; }
    public bool AllowedWhenUncontrollable { get; init; }
    public int Attempts { get; set; }
}

public sealed class TatsunoControllerEngine
{
    private readonly Queue<TatsunoPendingCommand> _queue = new();
    private TatsunoPendingCommand? _current;
    private DateTime _lastPollUtc = DateTime.MinValue;
    private DateTime _lastStateChangeUtc = DateTime.MinValue;

    public TatsunoControllerEngine(string addressLabel, int nozzlesCount)
    {
        Snapshot = new PostSnapshot(addressLabel, nozzlesCount);
        StationAddress = int.TryParse(addressLabel, out int addrNum)
            ? TatsunoCodec.StationAddressByte(addrNum)
            : (byte)'@';
    }

    public PostSnapshot Snapshot { get; }
    public byte StationAddress { get; }
    public char StationAddressChar => (char)StationAddress;
    public TatsunoLinkState LinkState { get; private set; } = TatsunoLinkState.Idle;
    // Reference program uses ~530ms poll interval (measured from log)
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan ReplyTimeout { get; set; } = TimeSpan.FromMilliseconds(700);
    public int PendingCount => _queue.Count + (_current is null ? 0 : 1);

    public TatsunoControllerAction GetNextAction(DateTime utcNow)
    {
        HandleTimeout(utcNow);

        if (LinkState != TatsunoLinkState.Idle)
        {
            return TatsunoControllerAction.None;
        }

        if (_current is null && _queue.Count > 0)
        {
            TatsunoPendingCommand candidate = _queue.Peek();
            if (candidate.AllowedWhenUncontrollable || Snapshot.Controllability != TatsunoPumpControllability.Uncontrollable)
            {
                _current = _queue.Dequeue();
                LinkState = TatsunoLinkState.WaitingSelectAck0;
                _lastStateChangeUtc = utcNow;
                return new TatsunoControllerAction(TatsunoControllerActionKind.SelectHandshake, TatsunoCodec.BuildActionHandshake(StationAddress), $"TX {Snapshot.AddressLabel}: {candidate.Description} handshake");
            }
        }

        if (utcNow - _lastPollUtc >= PollInterval)
        {
            LinkState = TatsunoLinkState.WaitingPollResponse;
            _lastPollUtc = utcNow;
            _lastStateChangeUtc = utcNow;
            return new TatsunoControllerAction(TatsunoControllerActionKind.Poll, TatsunoCodec.BuildPollRequest(StationAddress), $"TX {Snapshot.AddressLabel}: poll");
        }

        return TatsunoControllerAction.None;
    }

    public byte[]? HandleDle0(DateTime utcNow, out string description)
    {
        description = string.Empty;
        if (LinkState != TatsunoLinkState.WaitingSelectAck0 || _current is null)
        {
            return null;
        }

        LinkState = TatsunoLinkState.WaitingSelectAck1;
        _lastStateChangeUtc = utcNow;
        description = $"TX {Snapshot.AddressLabel}: {_current.Description}";
        string payload = _current.Payload;
        if (payload.Length > 0 && payload[0] == '@')
        {
            payload = StationAddressChar + payload.Substring(1);
        }
        return TatsunoCodec.BuildFrame(payload);
    }

    public void HandleDle1(DateTime utcNow)
    {
        if (LinkState == TatsunoLinkState.WaitingSelectAck1)
        {
            _current = null;
            LinkState = TatsunoLinkState.Idle;
            _lastStateChangeUtc = utcNow;
        }
    }

    public void HandleControlByte(byte control, DateTime utcNow)
    {
        if (LinkState == TatsunoLinkState.WaitingPollResponse && control == TatsunoControlBytes.EOT)
        {
            // EOT response to poll = ТРК connected, all nozzles stored, pump idle
            // Reference program uses this to show "ГОТОВ" (ready) status
            Snapshot.Condition = TatsunoPumpCondition.NozzleStored;
            Snapshot.ActiveNozzleNumber = 0;  // No active nozzle when idle — don't override user selection
            Snapshot.LastUpdatedLocal = DateTime.Now;

            LinkState = TatsunoLinkState.Idle;
            _lastStateChangeUtc = utcNow;
        }
    }

    public TatsunoParsedMessage? HandlePayload(string payload, DateTime localTimestamp)
    {
        if (!TatsunoQueryParser.TryParse(payload, out TatsunoParsedMessage? message))
        {
            Snapshot.LastPayload = payload;
            Snapshot.LastUpdatedLocal = localTimestamp;
            LinkState = TatsunoLinkState.Idle;
            return null;
        }

        ApplyMessage(message, localTimestamp);
        LinkState = TatsunoLinkState.Idle;
        return message;
    }

    public void Enqueue(string payload, TatsunoCommandKind kind, string description, bool allowedWhenUncontrollable = false)
    {
        _queue.Enqueue(new TatsunoPendingCommand
        {
            Payload = payload,
            Kind = kind,
            Description = description,
            AllowedWhenUncontrollable = allowedWhenUncontrollable
        });
    }

    private void HandleTimeout(DateTime utcNow)
    {
        if (LinkState == TatsunoLinkState.Idle)
        {
            return;
        }

        if (utcNow - _lastStateChangeUtc < ReplyTimeout)
        {
            return;
        }

        if (_current is not null)
        {
            _current.Attempts++;
            if (_current.Attempts < 2)
            {
                _queue.Enqueue(_current);
            }
            _current = null;
        }

        LinkState = TatsunoLinkState.Idle;
        _lastStateChangeUtc = utcNow;
    }

    private void ApplyMessage(TatsunoParsedMessage message, DateTime localTimestamp)
    {
        Snapshot.LastPayload = message.Payload;
        Snapshot.LastUpdatedLocal = localTimestamp;

        switch (message)
        {
            case TatsunoPowerOnMessage powerOnMessage:
                // Q00 — Power-ON CRC handshake. Auto-queue A00 response.
                // "43AF" → "C8" is confirmed empirically on multiple boards.
                string? ackPayload = TatsunoCodec.BuildCrcAcknowledgmentPayload(powerOnMessage.CrcHexData);
                if (ackPayload is not null)
                {
                    Enqueue(ackPayload, TatsunoCommandKind.CrcAcknowledgment, $"CRC ack (Q00 data={powerOnMessage.CrcHexData})", allowedWhenUncontrollable: true);
                }
                // If unknown challenge, engine stores it in LastPayload for logging by caller
                break;

            case TatsunoPumpConditionMessage conditionMessage:
                Snapshot.Controllability = conditionMessage.Controllability;
                break;

            case TatsunoPumpStatusMessage statusMessage:
                TatsunoPumpCondition prevCondition = Snapshot.Condition;
                Snapshot.Condition = statusMessage.Condition;
                Snapshot.CurrentVolumeRaw = statusMessage.VolumeRaw;
                Snapshot.CurrentAmountRaw = statusMessage.AmountRaw;
                Snapshot.CurrentUnitPriceRaw = statusMessage.UnitPriceRaw;
                Snapshot.UnitPriceFlag = statusMessage.UnitPriceFlag;
                Snapshot.ActiveNozzleNumber = statusMessage.NozzleNumber;
                Snapshot.ActiveProduct = statusMessage.ProductCode;
                Snapshot.IndicationType = statusMessage.IndicationType;

                foreach (NozzleSnapshot nozzle in Snapshot.Nozzles)
                {
                    nozzle.IsLifted = statusMessage.NozzleNumber > 0 && nozzle.Number == statusMessage.NozzleNumber && statusMessage.Condition is TatsunoPumpCondition.NozzleLifted or TatsunoPumpCondition.Fuelling;
                    if (nozzle.Number == statusMessage.NozzleNumber)
                    {
                        nozzle.ProductCode = statusMessage.ProductCode;
                    }
                }

                // Auto-request totals when fueling completes (reference program does this)
                if (statusMessage.Condition == TatsunoPumpCondition.Finished && prevCondition != TatsunoPumpCondition.Finished)
                {
                    Enqueue(TatsunoCodec.BuildRequestTotalsPayload(), TatsunoCommandKind.RequestTotals, "auto totals (fueling finished)", allowedWhenUncontrollable: true);
                }
                break;

            case TatsunoReceiveErrorMessage errorMessage:
                Snapshot.LastReceiveErrorCode = errorMessage.ErrorCode;
                break;

            case TatsunoTotalsMessage totalsMessage:
                foreach (TatsunoTotalsEntry entry in totalsMessage.Entries)
                {
                    NozzleSnapshot? nozzle = Snapshot.Nozzles.FirstOrDefault(n => n.Number == entry.NozzleNumber);
                    if (nozzle is null)
                    {
                        continue;
                    }

                    nozzle.ProductCode = entry.ProductCode;
                    nozzle.TotalVolumeRaw = entry.TotalVolumeRaw;
                    nozzle.TotalAmountRaw = entry.TotalAmountRaw;
                }
                break;
        }
    }
}
