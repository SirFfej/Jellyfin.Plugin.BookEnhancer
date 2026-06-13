using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.BookEnhancer.Services;

/// <summary>Strips common scene/release group tags and parenthetical metadata from filenames.</summary>
public static partial class SceneTagCleaner
{
    /// <summary>
    /// Matches trailing scene release tags and parenthetical metadata appended to filenames.
    /// Strips groups like (2026), (Digital), (Kileko-Empire), (of 5), [remastered], etc.
    /// </summary>
    [GeneratedRegex(
        """
        [\s._-]*
        (?:
            \( [^)]* \)+
            |
            \[ [^\]]* \]+
            |
            \b (?:REMASTERED|FIXED|HYBRID|SCAN|NOADS|C2C|HQ|HD|WEBRIP|DIGITAL|REEDITION|SCANLATION) \b
            |
            - \s* (?:Digital|Webrip|HD|HQ|C2C|Scan|noads|hybrid|remastered|fixed|reedition|scanlation) \b
        )
        [\s._-]*
        """,
        RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace
    )]
    private static partial Regex SceneSuffixPattern();

    /// <summary>Strips scene release tags from the end of a filename string.</summary>
    /// <param name="raw">The filename (without extension) to clean.</param>
    /// <returns>The cleaned filename with trailing scene tags removed.</returns>
    public static string Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return raw ?? string.Empty;

        var result = SceneSuffixPattern().Replace(raw, string.Empty);

        result = result.Trim().TrimEnd('.', '-', '_', ' ');

        return result;
    }
}
