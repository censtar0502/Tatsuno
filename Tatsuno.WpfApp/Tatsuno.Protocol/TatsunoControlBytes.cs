namespace Tatsuno.Protocol;

public static class TatsunoControlBytes
{
    public const byte NUL = 0x00;
    public const byte SOH = 0x01;
    public const byte STX = 0x02;
    public const byte ETX = 0x03;
    public const byte EOT = 0x04;
    public const byte ENQ = 0x05;
    public const byte ACK = 0x06;
    public const byte DLE = 0x10;
    public const byte NAK = 0x15;

    public const byte ASCII_0 = (byte)'0';
    public const byte ASCII_1 = (byte)'1';
}
