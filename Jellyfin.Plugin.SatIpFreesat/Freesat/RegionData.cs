using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.SatIpFreesat.Freesat;

/// <summary>
/// UK Freesat regional lookup — ported from the tvheadend provisioning scripts.
/// Maps postcode → region key → bouquet name substrings used to match live BAT data.
/// </summary>
public static class RegionData
{
    public sealed record RegionInfo(string Label, string[] Match);

    public static readonly IReadOnlyDictionary<string, RegionInfo> Regions =
        new Dictionary<string, RegionInfo>(StringComparer.Ordinal)
        {
            ["border"]      = new("Border (Cumbria & South Scotland)",          ["Border"]),
            ["central"]     = new("Central England (Midlands)",                  ["Central", "Midlands"]),
            ["channel"]     = new("Channel Islands",                             ["Channel"]),
            ["granada"]     = new("North West England (Granada)",                ["North West", "Granada", "NW England"]),
            ["london"]      = new("London",                                      ["London"]),
            ["meridian"]    = new("South & South East England (Meridian)",       ["Meridian", "South East", "Solent"]),
            ["anglia"]      = new("East of England (Anglia)",                    ["Anglia", "East of England", "East England"]),
            ["utv"]         = new("Northern Ireland (UTV)",                      ["Northern Ireland", "UTV"]),
            ["stv_central"] = new("Central Scotland (STV Central)",              ["Central Scotland", "STV Central"]),
            ["stv_north"]   = new("North Scotland (STV North / Grampian)",       ["North Scotland", "Grampian", "STV North"]),
            ["tynetees"]    = new("North East England (Tyne Tees)",              ["Tyne Tees", "North East"]),
            ["wales"]       = new("Wales",                                       ["Wales", "Cymru"]),
            ["west"]        = new("West of England (Bristol/Bath)",              ["West of England"]),
            ["westcountry"] = new("South West England (West Country)",           ["West Country", "Westcountry", "South West"]),
            ["yorkshire"]   = new("Yorkshire",                                   ["Yorkshire", "Yorks"]),
        };

    private static readonly IReadOnlyDictionary<string, string> PostcodeAreas =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Border
            { "CA", "border" }, { "DG", "border" }, { "TD", "border" },
            // Central England
            { "B", "central" }, { "CV", "central" }, { "DY", "central" }, { "WS", "central" },
            { "WV", "central" }, { "TF", "central" }, { "SY", "central" }, { "LE", "central" },
            { "NN", "central" }, { "DE", "central" }, { "NG", "central" }, { "LN", "central" },
            { "WR", "central" }, { "OX", "central" },
            // Channel Islands
            { "GY", "channel" }, { "JE", "channel" },
            // North West England (Granada)
            { "M", "granada" }, { "SK", "granada" }, { "OL", "granada" }, { "BL", "granada" },
            { "WN", "granada" }, { "WA", "granada" }, { "L", "granada" }, { "CH", "granada" },
            { "CW", "granada" }, { "PR", "granada" }, { "BB", "granada" }, { "FY", "granada" },
            { "LA", "granada" }, { "ST", "granada" }, { "IM", "granada" },
            // London
            { "E", "london" }, { "EC", "london" }, { "N", "london" }, { "NW", "london" },
            { "SE", "london" }, { "SW", "london" }, { "W", "london" }, { "WC", "london" },
            { "BR", "london" }, { "CR", "london" }, { "DA", "london" }, { "EN", "london" },
            { "HA", "london" }, { "IG", "london" }, { "KT", "london" }, { "RM", "london" },
            { "SM", "london" }, { "TW", "london" }, { "UB", "london" }, { "WD", "london" },
            { "AL", "london" }, { "HP", "london" },
            // Meridian (South & South East)
            { "SO", "meridian" }, { "PO", "meridian" }, { "GU", "meridian" }, { "RH", "meridian" },
            { "BN", "meridian" }, { "TN", "meridian" }, { "ME", "meridian" }, { "CT", "meridian" },
            { "SP", "meridian" }, { "BH", "meridian" }, { "RG", "meridian" }, { "SL", "meridian" },
            // Anglia (East of England)
            { "IP", "anglia" }, { "NR", "anglia" }, { "CO", "anglia" }, { "CM", "anglia" },
            { "SS", "anglia" }, { "CB", "anglia" }, { "PE", "anglia" }, { "SG", "anglia" },
            { "LU", "anglia" }, { "MK", "anglia" },
            // Northern Ireland
            { "BT", "utv" },
            // Central Scotland
            { "G", "stv_central" }, { "ML", "stv_central" }, { "PA", "stv_central" },
            { "KA", "stv_central" }, { "FK", "stv_central" }, { "KY", "stv_central" },
            { "EH", "stv_central" },
            // North Scotland
            { "AB", "stv_north" }, { "IV", "stv_north" }, { "PH", "stv_north" },
            { "DD", "stv_north" }, { "KW", "stv_north" }, { "ZE", "stv_north" },
            { "HS", "stv_north" },
            // Tyne Tees
            { "NE", "tynetees" }, { "SR", "tynetees" }, { "DH", "tynetees" }, { "DL", "tynetees" },
            { "TS", "tynetees" },
            // Wales
            { "CF", "wales" }, { "SA", "wales" }, { "NP", "wales" }, { "LD", "wales" },
            { "LL", "wales" },
            // West of England
            { "BS", "west" }, { "BA", "west" }, { "GL", "west" }, { "HR", "west" }, { "SN", "west" },
            // South West England
            { "EX", "westcountry" }, { "PL", "westcountry" }, { "TQ", "westcountry" },
            { "TR", "westcountry" }, { "TA", "westcountry" }, { "DT", "westcountry" },
            // Yorkshire
            { "LS", "yorkshire" }, { "BD", "yorkshire" }, { "HD", "yorkshire" },
            { "HX", "yorkshire" }, { "WF", "yorkshire" }, { "YO", "yorkshire" },
            { "HU", "yorkshire" }, { "DN", "yorkshire" }, { "S", "yorkshire" },
            { "HG", "yorkshire" },
        };

    // Specific outward codes that override their area's region.
    private static readonly IReadOnlyDictionary<string, string> OutwardOverrides =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolve a UK postcode (or outward code) to a region key.
    /// Returns null if the area is not mapped.
    /// </summary>
    public static string? ResolveRegion(string postcode)
    {
        var outward = NormaliseOutwardCode(postcode);
        if (OutwardOverrides.TryGetValue(outward, out var ov))
            return ov;
        var area = AreaFromOutwardCode(outward);
        return PostcodeAreas.TryGetValue(area, out var r) ? r : null;
    }

    private static string NormaliseOutwardCode(string postcode)
    {
        var pc = postcode.Trim().ToUpperInvariant().Replace(" ", "");
        if (pc.Length > 3)
        {
            var tail = pc[^3..];
            if (char.IsDigit(tail[0]) && char.IsLetter(tail[1]) && char.IsLetter(tail[2]))
                return pc[..^3];
        }
        return pc;
    }

    private static string AreaFromOutwardCode(string outward)
    {
        var area = string.Empty;
        foreach (var ch in outward)
        {
            if (char.IsLetter(ch)) area += ch;
            else break;
        }
        return area;
    }
}
