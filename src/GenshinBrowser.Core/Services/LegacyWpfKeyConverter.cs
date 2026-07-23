namespace GenshinBrowser.Services;

internal static class LegacyWpfKeyConverter
{
    public static int ToVirtualKey(int legacyKey)
    {
        return legacyKey switch
        {
            1 => 0x03,
            2 => 0x08,
            3 => 0x09,
            5 => 0x0C,
            6 => 0x0D,
            7 => 0x13,
            8 => 0x14,
            9 => 0x15,
            10 => 0x17,
            11 => 0x18,
            12 => 0x19,
            13 => 0x1B,
            14 => 0x1C,
            15 => 0x1D,
            16 => 0x1E,
            17 => 0x1F,
            >= 18 and <= 43 => legacyKey + 14,
            >= 44 and <= 72 => legacyKey + 21,
            73 => 0x5F,
            >= 74 and <= 113 => legacyKey + 22,
            >= 114 and <= 115 => legacyKey + 30,
            >= 116 and <= 139 => legacyKey + 44,
            >= 140 and <= 148 => legacyKey + 46,
            >= 149 and <= 153 => legacyKey + 70,
            154 => 0xE2,
            155 => 0xE5,
            >= 157 and <= 171 => legacyKey + 83,
            _ => 0,
        };
    }
}
