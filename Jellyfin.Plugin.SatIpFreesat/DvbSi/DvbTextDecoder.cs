using System;
using System.Text;

namespace Jellyfin.Plugin.SatIpFreesat.DvbSi;

/// <summary>
/// Decodes DVB character-encoded strings (EN 300 468 §A.2).
/// Handles the most common encodings on UK satellite: Latin-1 and UTF-8.
/// </summary>
public static class DvbTextDecoder
{
    public static string Decode(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return string.Empty;

        // First byte selects encoding if it is in range 0x01-0x1F
        if (data[0] >= 0x20)
        {
            // Default: ISO-8859-1 (Latin-1)
            return Encoding.Latin1.GetString(data);
        }

        return data[0] switch
        {
            0x01 => Encoding.GetEncoding("iso-8859-5").GetString(data[1..]),
            0x02 => Encoding.GetEncoding("iso-8859-6").GetString(data[1..]),
            0x03 => Encoding.GetEncoding("iso-8859-7").GetString(data[1..]),
            0x04 => Encoding.GetEncoding("iso-8859-8").GetString(data[1..]),
            0x05 => Encoding.GetEncoding("iso-8859-9").GetString(data[1..]),
            0x06 => Encoding.GetEncoding("iso-8859-10").GetString(data[1..]),
            0x07 => Encoding.GetEncoding("iso-8859-11").GetString(data[1..]),  // Thai
            0x09 => Encoding.GetEncoding("iso-8859-13").GetString(data[1..]),
            0x0A => Encoding.GetEncoding("iso-8859-14").GetString(data[1..]),
            0x0B => Encoding.GetEncoding("iso-8859-15").GetString(data[1..]),
            0x10 => DecodeSingleByte(data),     // ISO-8859-N selected by 2-byte country code
            0x11 => Encoding.BigEndianUnicode.GetString(data[1..]),  // ISO/IEC 10646
            0x13 => Encoding.GetEncoding("gb2312").GetString(data[1..]),
            0x14 => Encoding.BigEndianUnicode.GetString(data[1..]),  // BIG5
            0x15 => Encoding.UTF8.GetString(data[1..]),
            _ => Encoding.Latin1.GetString(data[1..]),
        };
    }

    private static string DecodeSingleByte(ReadOnlySpan<byte> data)
    {
        // 0x10, 0x00, N → ISO-8859-N
        if (data.Length < 3) return string.Empty;
        int n = data[2];
        try
        {
            return Encoding.GetEncoding($"iso-8859-{n}").GetString(data[3..]);
        }
        catch
        {
            return Encoding.Latin1.GetString(data[3..]);
        }
    }
}
