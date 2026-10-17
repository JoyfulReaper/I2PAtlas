using System.Collections.Immutable;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using I2PAtlas.AddressBook;
using I2PAtlas.I2pProbing;

namespace I2PAtlas.I2pPublishing;

// Neutral allowlist policy shared by scan and crawl projection. No private model enters public state.
internal static class I2pPublicProjectionPolicy
{
    internal const int MaxTitleCharacters = 160;

    internal static I2pPublicSummaryState Summary(I2pRequestOutcome? robotsOutcome,
        I2pProxyResponseClassification robotsClass, I2pRobotsObservation robots, bool? homepageAttempted, int? homepageStatus)
    {
        if (homepageStatus.HasValue) return I2pPublicSummaryState.HomepageResponseObserved;
        if (homepageAttempted == true) return I2pPublicSummaryState.HomepageAttemptFailed;
        if (robots == I2pRobotsObservation.DisallowsRoot) return I2pPublicSummaryState.RobotsDisallowed;
        if (robotsClass == I2pProxyResponseClassification.LikelyProxyFailure) return I2pPublicSummaryState.ProxyFailureObserved;
        if (robotsOutcome == I2pRequestOutcome.TimedOut) return I2pPublicSummaryState.RequestTimedOut;
        if (robotsOutcome is I2pRequestOutcome.TransportFailure or I2pRequestOutcome.ProxyUnavailable)
            return I2pPublicSummaryState.TransportFailure;
        if (robots == I2pRobotsObservation.Redirect) return I2pPublicSummaryState.RobotsRedirect;
        return I2pPublicSummaryState.Ambiguous;
    }

    internal static ImmutableArray<string> DiscoveredHostnames(I2pPageMetadata? metadata) =>
        (metadata?.Links ?? []).Where(link => link.Kind == I2pDiscoveredLinkKind.I2p)
            .Select(link => I2pSeed.NormalizeHostname(new Uri(link.Url, UriKind.Absolute).Host))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();

    internal static string? MediaType(string? text) => MediaTypeHeaderValue.TryParse(text, out var type) &&
        type.MediaType is { Length: > 0 and <= 80 } media && media.Contains('/') &&
        media.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '.' or '+') ? media.ToLowerInvariant() : null;

    // Plain text. Any later HTML renderer must escape for its own output context.
    internal static string? Title(string? text)
    {
        if (text == null) return null;
        var title = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format or
                UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator) continue;
            if (title.Length + rune.Utf16SequenceLength > MaxTitleCharacters) break;
            title.Append(rune.ToString());
        }
        var result = title.ToString().Trim();
        return result.Length == 0 ? null : result;
    }
}
