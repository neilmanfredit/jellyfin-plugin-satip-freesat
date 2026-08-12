using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.SatIpFreesat.Freesat;

/// <summary>
/// Curated Freesat channel-name → LCN table, ported from the tvheadend provisioning scripts.
/// Used as a fallback when BAT LCN data is not available from the live stream,
/// and also as the canonical allowlist for Freesat channels.
/// Keys matched case-insensitively against the broadcast service name.
/// </summary>
public static class LcnTable
{
    // BBC One HD regional variants: each gets its own browsable number (951-966),
    // and the one matching the selected region is overridden to 101 at scan time.
    public static readonly IReadOnlyDictionary<string, int> RegionalBbcOne =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "BBC One Lon HD", 951 },
            { "BBC One NE HD",  952 },
            { "BBC One NW HD",  953 },
            { "BBC One Yks HD", 954 },
            { "BBC One Y&L HD", 955 },
            { "BBC One WM HD",  956 },
            { "BBC One EMidHD", 957 },
            { "BBC One EastHD", 958 },
            { "BBC One SE HD",  959 },
            { "BBC One Wst HD", 960 },
            { "BBC One Sth HD", 961 },
            { "BBC One SW HD",  962 },
            { "BBC One CI HD",  963 },
            { "BBC One ScotHD", 964 },
            { "BBC One Wal HD", 965 },
            { "BBC One NI HD",  966 },
        };

    // Map from region key → BBC One service name for that region (LCN 101).
    public static readonly IReadOnlyDictionary<string, string> RegionBbcOneName =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "border",      "BBC One ScotHD" },
            { "central",     "BBC One WM HD"  },
            { "channel",     "BBC One CI HD"  },
            { "granada",     "BBC One NW HD"  },
            { "london",      "BBC One Lon HD" },
            { "meridian",    "BBC One Sth HD" },
            { "anglia",      "BBC One EastHD" },
            { "utv",         "BBC One NI HD"  },
            { "stv_central", "BBC One ScotHD" },
            { "stv_north",   "BBC One ScotHD" },
            { "tynetees",    "BBC One NE HD"  },
            { "wales",       "BBC One Wal HD" },
            { "west",        "BBC One Wst HD" },
            { "westcountry", "BBC One SW HD"  },
            { "yorkshire",   "BBC One Yks HD" },
        };

    // All Freesat channels except BBC One regional variants.
    // BBC One/ITV1 are absent from core — handled specially above.
    public static readonly IReadOnlyDictionary<string, int> CoreChannels =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            // Core PSB
            { "BBC Two HD",  102 },
            { "Channel 4 HD", 104 },
            { "5 HD",        105 },
            { "S4C HD",      120 },

            // Entertainment
            { "ITV2 HD",       113 },
            { "ITV3 HD",       115 },
            { "ITV4 HD",       117 },
            { "ITV Quiz HD",   119 },
            { "E4 HD",         122 },
            { "E4+1",          123 },
            { "More4 HD",      124 },
            { "E4 Extra",      126 },
            { "4seven",        127 },
            { "Channel 4+1",   121 },
            { "5+1",           128 },
            { "5 USA",         129 },
            { "5USA+1",        130 },
            { "5STAR",         131 },
            { "5ACTION",       132 },
            { "TRUE CRIME X",  134 },
            { "TRUE CRIME",    135 },
            { "TRUE CRIME+1",  136 },
            { "LEGEND",        137 },
            { "LEGEND XTRA",   138 },
            { "LEGEND XTRA+1", 139 },
            { "Sky Mix HD",    141 },
            { "Challenge",     142 },
            { "Sky Arts HD",   143 },
            { "TLC HD",        144 },
            { "QUEST HD",      145 },
            { "Quest Red",     146 },
            { "DMAX",          147 },
            { "Food Network",  148 },
            { "Really",        149 },
            { "TLC+1",         150 },
            { "QUEST+1",       151 },
            { "Quest Red+1",   152 },
            { "DMAX+1",        153 },
            { "PBS America",   155 },
            { "U&W",           156 },
            { "U&Dave HD",     157 },
            { "U&Drama",       158 },
            { "U&Yesterday",   159 },
            { "U&Eden",        160 },
            { "BLAZE",         161 },
            { "Together",      162 },
            { "Court TV",      163 },
            { "Film4 HD",      300 },
            { "Film4+1",       301 },
            { "TalkingPictures", 306 },

            // Kids
            { "CBBC HD",    600 },
            { "CBeebies HD", 601 },

            // News
            { "BBC NEWS HD",   200 },
            { "BBC Parl HD",   201 },
            { "Sky News HD",   202 },
            { "Al Jazeera HD", 203 },
            { "FRANCE 24 HD",  204 },
            { "Bloomberg HD",  208 },
            { "NHK World HD",  209 },
            { "CNBC HD",       210 },
            { "Channels 24",   213 },
            { "Arirang TV HD", 214 },
            { "TRT World HD",  215 },
            { "GB News HD",    216 },

            // Shopping
            { "QVC HD",         800 },
            { "QVC Beauty",     801 },
            { "QVC Extra",      802 },
            { "QVC Style HD",   803 },
            { "Gemporia HD",    805 },
            { "HobbyMakerHD",   806 },
            { "JewelleryMaker", 807 },
            { "TJC HD",         809 },
            { "Ideal World HD", 810 },
            { "MstHveIdeasHD",  814 },

            // Religion
            { "DAYSTAR HD",   691 },
            { "revelation",   692 },
            { "GOD Channel",  694 },
            { "SonLife",      695 },
        };

    /// <summary>
    /// Build the full target LCN map for a given region.
    /// Caller must still handle ITV1 HD (multiple same-named instances on the feed).
    /// </summary>
    public static Dictionary<string, int> BuildTargets(string regionKey)
    {
        var targets = new Dictionary<string, int>(CoreChannels, StringComparer.OrdinalIgnoreCase);

        foreach (var (name, lcn) in RegionalBbcOne)
            targets[name] = lcn;

        // The selected region's BBC One gets the canonical LCN 101.
        if (RegionBbcOneName.TryGetValue(regionKey, out var bbcOneName))
            targets[bbcOneName] = 101;

        // ITV1 HD: all regional variants broadcast as "ITV1 HD"; first seen gets 103.
        targets["ITV1 HD"] = 103;

        return targets;
    }
}
