namespace RockwellTagReader;

internal static class CipTypeCodes
{
    public const ushort Bool   = 0x00C1;
    public const ushort Sint   = 0x00C2;
    public const ushort Int    = 0x00C3;
    public const ushort Dint   = 0x00C4;
    public const ushort Lint   = 0x00C5;
    public const ushort Usint  = 0x00C6;
    public const ushort Uint   = 0x00C7;
    public const ushort Udint  = 0x00C8;
    public const ushort Ulint  = 0x00C9;
    public const ushort Real   = 0x00CA;
    public const ushort Lreal  = 0x00CB;
    public const ushort String = 0x00D0;
    public const ushort Byte   = 0x00D1;
    public const ushort Word   = 0x00D2;
    public const ushort Dword  = 0x00D3;
    public const ushort Lword  = 0x00D4;

    public static string TypeName(ushort code) => code switch
    {
        Bool   => "BOOL",
        Sint   => "SINT",
        Int    => "INT",
        Dint   => "DINT",
        Lint   => "LINT",
        Usint  => "USINT",
        Uint   => "UINT",
        Udint  => "UDINT",
        Ulint  => "ULINT",
        Real   => "REAL",
        Lreal  => "LREAL",
        Byte   => "BYTE",
        Word   => "WORD",
        Dword  => "DWORD",
        Lword  => "LWORD",
        String => "STR",
        _ => $"0x{code:X4}"
    };
}
