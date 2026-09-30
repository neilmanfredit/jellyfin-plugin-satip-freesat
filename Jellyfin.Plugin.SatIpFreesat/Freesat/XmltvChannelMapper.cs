using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.SatIpFreesat.Freesat;

/// <summary>
/// Maps our own Freesat-scanned channels onto a third-party XMLTV guide's channel ids by
/// exact name match after normalizing away HD/SD suffixes, case, and punctuation.
/// </summary>
/// <remarks>
/// Deliberately no fuzzy/partial/token-overlap matching. XMLTV feeds sourced from Sky's EPG
/// (like the default union UK guide this plugin uses) label regional BBC One/ITV variants
/// with the same short abbreviations Freesat itself broadcasts (e.g. both call it "BBC One
/// NW"), so a straight normalized-name match already resolves regional channels correctly.
/// A wrong regional match (e.g. showing Yorkshire's BBC One schedule under a North West
/// channel) would be worse than simply leaving that channel without XMLTV-sourced programs.
/// </remarks>
public static class XmltvChannelMapper
{
    /// <returns>Map of our <see cref="FreesatChannel.ChannelId"/> to the matched XMLTV channel id.</returns>
    public static Dictionary<string, string> BuildMapping(
        IEnumerable<FreesatChannel> channels, Dictionary<string, List<string>> xmltvDisplayNamesById)
    {
        var byNormalizedName = new Dictionary<string, string>();
        foreach (var (xmltvId, names) in xmltvDisplayNamesById)
        {
            foreach (var name in names)
            {
                var key = Normalize(name);
                if (key.Length == 0) continue;

                // First entry wins on a duplicate normalized name — the feed lists its more
                // widely-carried variant before obscure regional/operator duplicates.
                byNormalizedName.TryAdd(key, xmltvId);
            }
        }

        var mapping = new Dictionary<string, string>();
        foreach (var channel in channels)
        {
            var key = Normalize(channel.Name);
            if (key.Length > 0 && byNormalizedName.TryGetValue(key, out var xmltvId))
                mapping[channel.ChannelId] = xmltvId;
        }

        return mapping;
    }

    private static string Normalize(string name)
    {
        // Freesat's own SI service names sometimes concatenate "HD" directly onto a region
        // abbreviation with no separating space (e.g. "BBC One EastHD"), so a plain \bHD\b
        // word-boundary strip misses it — trim any trailing HD/SD suffix instead.
        var s = Regex.Replace(name.Trim(), @"\s*(HD|SD)$", "", RegexOptions.IgnoreCase);
        s = s.ToUpperInvariant();
        s = Regex.Replace(s, "[^A-Z0-9+]", "");
        return s;
    }
}
