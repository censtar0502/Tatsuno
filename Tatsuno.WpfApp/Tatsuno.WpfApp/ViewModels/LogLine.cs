using System;

namespace Tatsuno.WpfApp.ViewModels;

public sealed class LogLine
{
    public required DateTime Time { get; init; }
    public required string Direction { get; init; } // "TX"/"RX"/"SYS"
    public required string Text { get; init; }

    public override string ToString() => $"[{Time:HH:mm:ss.fff}] {Direction} {Text}";
}
