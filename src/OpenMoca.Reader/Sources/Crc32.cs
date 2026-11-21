namespace OpenMoca.Reader.Sources;

/// <summary>CRC-32 (ISO-HDLC, as Zip uses it), for checking an archive entry against its declared value (contract §3).</summary>
internal static class Crc32
{
    private static readonly uint[] Table = Build();

    /// <summary>The CRC-32 of <paramref name="data"/>.</summary>
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return ~crc;
    }

    private static uint[] Build()
    {
        var table = new uint[256];
        for (var n = 0u; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
