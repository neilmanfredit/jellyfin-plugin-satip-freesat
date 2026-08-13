using System;
using System.Collections.Generic;
using Jellyfin.Plugin.SatIpFreesat.Freesat;

namespace Jellyfin.Plugin.SatIpFreesat.DvbSi;

/// <summary>
/// Parses DVB NIT (Network Information Table, table_id 0x40/0x41).
/// Extracts mux parameters from satellite_delivery_system_descriptor (0x43).
/// </summary>
public static class NitParser
{
    public const int PidNit = 0x10;
    public const byte TableIdNitActual = 0x40;
    public const byte TableIdNitOther = 0x41;

    public static List<MuxInfo> Parse(ReadOnlySpan<byte> section)
    {
        var muxes = new List<MuxInfo>();
        if (section.Length < 12) return muxes;
        if (section[0] is not (TableIdNitActual or TableIdNitOther)) return muxes;

        int networkDescLen = ((section[8] & 0x0F) << 8) | section[9];
        int tsLoopStart = 10 + networkDescLen;
        if (tsLoopStart + 2 > section.Length) return muxes;

        int tsLoopLen = ((section[tsLoopStart] & 0x0F) << 8) | section[tsLoopStart + 1];
        int pos = tsLoopStart + 2;
        int end = pos + tsLoopLen;

        while (pos + 6 <= end && pos + 6 <= section.Length)
        {
            int tsid = (section[pos] << 8) | section[pos + 1];
            int onid = (section[pos + 2] << 8) | section[pos + 3];
            int descLen = ((section[pos + 4] & 0x0F) << 8) | section[pos + 5];
            pos += 6;

            var descs = section[pos..Math.Min(pos + descLen, section.Length)];
            pos += descLen;

            var mux = ParseSatelliteDescriptor(descs, tsid, onid);
            if (mux is not null) muxes.Add(mux);
        }

        return muxes;
    }

    private static MuxInfo? ParseSatelliteDescriptor(ReadOnlySpan<byte> descs, int tsid, int onid)
    {
        int i = 0;
        while (i + 2 <= descs.Length)
        {
            byte tag = descs[i];
            int len = descs[i + 1];
            i += 2;
            if (i + len > descs.Length) break;

            if (tag == 0x43 && len >= 11)
            {
                var d = descs.Slice(i, len);
                double freqMHz = ParseBcdFrequency(d);
                double orbPos = ParseBcdOrbitalPos(d[4..]);
                char pol = ((d[6] >> 5) & 0x03) switch
                {
                    0 => 'H', 1 => 'V', 2 => 'L', _ => 'R',
                };
                bool isDvbS2 = (d[6] & 0x04) != 0;
                string modulationType = (d[6] & 0x03) switch
                {
                    0x02 => "8psk",
                    0x03 => "16apsk",
                    _ => "qpsk",
                };
                double srKsym = ParseBcdSymbolRate(d[7..]);
                int fecInner = d[10] & 0x0F;

                // Only include 28.2E muxes
                if (Math.Abs(orbPos - 28.2) > 0.5) { i += len; continue; }

                return new MuxInfo
                {
                    FrequencyMHz = freqMHz,
                    Polarization = pol,
                    SymbolRateKsym = srKsym,
                    IsDvbS2 = isDvbS2,
                    ModulationType = modulationType,
                    TransportStreamId = tsid,
                    OriginalNetworkId = onid,
                };
            }
            i += len;
        }
        return null;
    }

    // Frequency: 32-bit BCD, each nibble 0-9.
    // Value represents GHz with implied 6 decimal places: e.g. 0x11425000 = 11.425000 GHz = 11425 MHz.
    private static double ParseBcdFrequency(ReadOnlySpan<byte> d)
    {
        long digits = 0;
        for (int b = 0; b < 4; b++)
        {
            digits = digits * 10 + ((d[b] >> 4) & 0xF);
            digits = digits * 10 + (d[b] & 0xF);
        }
        // digits = 11425000 → 11425000 / 1000 = 11425 MHz
        return digits / 1000.0;
    }

    // Orbital position: 16-bit BCD, units of 0.1 degree
    private static double ParseBcdOrbitalPos(ReadOnlySpan<byte> d)
    {
        long digits = 0;
        digits = digits * 10 + ((d[0] >> 4) & 0xF);
        digits = digits * 10 + (d[0] & 0xF);
        digits = digits * 10 + ((d[1] >> 4) & 0xF);
        digits = digits * 10 + (d[1] & 0xF);
        return digits / 10.0;
    }

    // Symbol rate: 28-bit BCD (upper 28 bits of bytes 7-10), units of MSym/s with 5 decimal places.
    // e.g. 27500 kSym/s = 27.5 MSym/s → 0x2750000 (7 nibbles) → digits=2750000 → 2750000/100 = 27500 kSym/s
    private static double ParseBcdSymbolRate(ReadOnlySpan<byte> d)
    {
        // Bytes 0-3 of d (d[7..10] of original); lower nibble of d[3] is FEC_inner, not part of SR.
        long digits = 0;
        for (int b = 0; b < 3; b++)
        {
            digits = digits * 10 + ((d[b] >> 4) & 0xF);
            digits = digits * 10 + (d[b] & 0xF);
        }
        digits = digits * 10 + ((d[3] >> 4) & 0xF); // upper nibble of 4th byte only
        return digits / 100.0; // result in kSym/s
    }
}
