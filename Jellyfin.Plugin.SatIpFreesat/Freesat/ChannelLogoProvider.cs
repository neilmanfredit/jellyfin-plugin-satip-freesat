using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.SatIpFreesat.Freesat;

/// <summary>
/// Resolves a channel logo URL for a Freesat channel name. DVB SI carries no logo data, so
/// logos are sourced from the community-maintained tv-logo/tv-logos GitHub repo, served over
/// raw.githubusercontent.com. Freesat channel names commonly include quality/region suffixes
/// (" HD", " SD", "+1", regional opt-out names like " London"/" North West") that don't appear
/// in the logo repo's filenames, so names are normalised before lookup. A static table covers
/// channels whose logo filename doesn't follow the repo's usual slug convention; everything
/// else falls back to an algorithmic guess.
/// </summary>
public static class ChannelLogoProvider
{
    private const string BaseUrl = "https://raw.githubusercontent.com/tv-logo/tv-logos/main/countries/united-kingdom/";

    // Regional opt-out suffixes Freesat appends to nationally-networked channels. Stripped
    // before lookup since the logo repo only has one logo per network, not per region.
    private static readonly string[] RegionSuffixes =
    [
        "london", "north west", "north east", "yorkshire", "north", "south", "south west",
        "south east", "west", "east", "midlands", "east midlands", "west midlands",
        "wales", "scotland", "northern ireland", "channel islands", "border", "meridian",
        "anglia", "central", "granada", "westcountry", "tyne tees",
    ];

    // Filenames in the repo that don't match the plain slugify-and-append-"-uk" pattern.
    private static readonly Dictionary<string, string> Overrides = new()
    {
        ["bbc one"] = "bbc-one-uk.png",
        ["bbc two"] = "bbc-two-uk.png",
        ["bbc three"] = "bbc-three-uk.png",
        ["bbc four"] = "bbc-four-uk.png",
        ["bbc news"] = "bbc-news-uk.png",
        ["bbc parliament"] = "bbc-parliament-uk.png",
        ["bbc alba"] = "bbc-alba-uk.png",
        ["bbc red button"] = "bbc-red-button-uk.png",
        ["cbbc"] = "bbc-cbbc-uk.png",
        ["cbeebies"] = "bbc-cbeebies-uk.png",
        ["itv"] = "itv-uk.png",
        ["itv1"] = "itv-1-uk.png",
        ["itv2"] = "itv-2-uk.png",
        ["itv3"] = "itv-3-uk.png",
        ["itv4"] = "itv-4-uk.png",
        ["itvbe"] = "itv-be-uk.png",
        ["itv encore"] = "itv-encore-uk.png",
        ["itv quiz"] = "itv-quiz-uk.png",
        ["channel 4"] = "channel-4-uk.png",
        ["4seven"] = "4-seven-uk.png",
        ["4music"] = "4-music-uk.png",
        ["more4"] = "more4-uk.png",
        ["film4"] = "film4-uk.png",
        ["e4"] = "e4-uk.png",
        ["all 4"] = "all-4-uk.png",
        ["channel 5"] = "channel-5-uk.png",
        ["5star"] = "5-star-uk.png",
        ["5usa"] = "5-usa-uk.png",
        ["5action"] = "5-action-uk.png",
        ["5select"] = "5-select-uk.png",
        ["dave"] = "dave-uk.png",
        ["gold"] = "gold-uk.png",
        ["drama"] = "drama-uk.png",
        ["yesterday"] = "yesterday-uk.png",
        ["quest"] = "quest-uk.png",
        ["quest red"] = "quest-red-uk.png",
        ["really"] = "really-uk.png",
        ["s4c"] = "s4c-uk.png",
    };

    private static readonly Regex NonSlugChars = new("[^a-z0-9 ]", RegexOptions.Compiled);
    private static readonly Regex LetterDigitBoundary = new("(?<=[a-z])(?=[0-9])", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);

    /// <summary>Best-effort logo URL for the given channel name, or null if none can be guessed.</summary>
    public static string? GetLogoUrl(string channelName)
    {
        var normalized = Normalize(channelName);
        if (normalized.Length == 0) return null;

        if (Overrides.TryGetValue(normalized, out var file))
            return BaseUrl + file;

        var slug = LetterDigitBoundary.Replace(normalized, " ");
        slug = WhitespaceRun.Replace(slug, " ").Trim().Replace(' ', '-');
        return slug.Length == 0 ? null : $"{BaseUrl}{slug}-uk.png";
    }

    private static string Normalize(string name)
    {
        var s = name.ToLowerInvariant();

        s = s.Replace("+1", " plus1").Replace("&", " and ");

        // Strip quality/edition markers.
        s = Regex.Replace(s, @"\bhd\b", " ");
        s = Regex.Replace(s, @"\bsd\b", " ");
        s = Regex.Replace(s, @"\b\(w\)", " ");

        foreach (var region in RegionSuffixes)
            s = Regex.Replace(s, $@"\b{Regex.Escape(region)}\b", " ");

        s = NonSlugChars.Replace(s, " ");
        s = WhitespaceRun.Replace(s, " ").Trim();
        return s;
    }
}
