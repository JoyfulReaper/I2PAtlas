using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using I2PAtlas.I2pTransport;

namespace I2PAtlas.I2pProbing;

public enum I2pDiscoveredLinkKind { I2p, Clearnet }
public sealed record I2pDiscoveredLink(string Url, I2pDiscoveredLinkKind Kind);

/// <summary>Private, inert metadata. Links are observations, never approved request targets.</summary>
public sealed record I2pPageMetadata(
    string? ContentType,
    int BodyBytesExamined,
    bool BodyTruncated,
    string? Title,
    ImmutableArray<I2pDiscoveredLink> Links,
    bool ExtractionIncomplete)
{
    public const int MaxBodyBytes = I2pHttpTransport.MaxResponseBytes;
    public const int MaxTitleCharacters = 200;
    public const int MaxLinks = 250;
    private const int MaxCandidates = 1000;
    private static readonly Regex TitlePattern = Pattern(@"<title\b[^>]*>(?<title>.*?)</title>");
    private static readonly Regex Whitespace = Pattern(@"\s+");
    private static readonly Regex LinkPattern = Pattern(
        """href\s*=\s*["'](?<url>[^"'<>]+)["']|(?<url>https?://[^\s"'<>]+)""");

    // Network-independent extraction. Calling this does not authenticate the bytes
    // or promote discovered URLs into approved network targets.
    internal static I2pPageMetadata Read(I2pHttpRequestTarget homepage, ReadOnlySpan<byte> body,
        string? contentType, CancellationToken cancellationToken = default) =>
        Read(homepage.RequestUri, body, contentType, cancellationToken);

    internal static I2pPageMetadata Read(I2pProbeRequest homepage, ReadOnlySpan<byte> body,
        string? contentType, CancellationToken cancellationToken = default) =>
        Read(homepage.RequestUri, body, contentType, cancellationToken);

    private static I2pPageMetadata Read(Uri homepageUri, ReadOnlySpan<byte> body,
        string? contentType, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var count = Math.Min(body.Length, MaxBodyBytes);
        var truncated = body.Length > count;
        contentType = I2pHttpProbe.Bound(contentType, I2pHttpProbe.MaxContentTypeCharacters);
        var mediaType = contentType?.Split(';', 2)[0].Trim();
        if (mediaType != null && !mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) &&
            !mediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase))
            return new(contentType, count, truncated, null, [], truncated);

        var text = Encoding.UTF8.GetString(body[..count]);
        string? title = null;
        var links = new SortedDictionary<string, I2pDiscoveredLinkKind>(StringComparer.Ordinal);
        var incomplete = truncated;
        try
        {
            var match = TitlePattern.Match(text);
            if (match.Success)
                title = I2pHttpProbe.Bound(Whitespace.Replace(WebUtility.HtmlDecode(match.Groups["title"].Value), " ").Trim(),
                    MaxTitleCharacters);
            var candidates = 0;
            foreach (Match link in LinkPattern.Matches(text))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++candidates > MaxCandidates) { incomplete = true; break; }
                var raw = WebUtility.HtmlDecode(link.Groups["url"].Value).Trim();
                if (raw.Length > I2pHttpRequestTarget.MaxUrlCharacters || raw.Length == 0 || raw[0] == '#' ||
                    raw.Any(char.IsControl) || raw.Contains('\\')) continue;
                if (!Uri.TryCreate(homepageUri, raw, out var uri) ||
                    uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 ||
                    uri.HostNameType != UriHostNameType.Dns || !IsHostname(uri.Host)) continue;
                // Helpers are neither processed nor included in the discovered URL set.
                if (uri.Query.TrimStart('?').Split('&').Any(part =>
                    Uri.UnescapeDataString(part.Split('=', 2)[0]).Equals("i2paddresshelper", StringComparison.OrdinalIgnoreCase)))
                    continue;
                var url = new UriBuilder(uri) { Fragment = "" }.Uri.AbsoluteUri;
                if (url.Length > I2pHttpRequestTarget.MaxUrlCharacters) continue;
                if (!links.ContainsKey(url) && links.Count == MaxLinks) { incomplete = true; break; }
                links[url] = uri.Host.TrimEnd('.').EndsWith(".i2p", StringComparison.OrdinalIgnoreCase)
                    ? I2pDiscoveredLinkKind.I2p : I2pDiscoveredLinkKind.Clearnet;
            }
        }
        catch (RegexMatchTimeoutException) { incomplete = true; }
        cancellationToken.ThrowIfCancellationRequested();
        return new(contentType, count, truncated, title,
            links.Select(link => new I2pDiscoveredLink(link.Key, link.Value)).ToImmutableArray(), incomplete);
    }

    private static bool IsHostname(string host)
    {
        host = host.TrimEnd('.');
        if (host.Length > 253 || !host.Contains('.')) return false;
        return host.Split('.').All(label => label.Length is >= 1 and <= 63 && label[0] != '-' && label[^1] != '-' &&
            label.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-'));
    }

    private static Regex Pattern(string pattern) => new(pattern,
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.NonBacktracking,
        TimeSpan.FromMilliseconds(100));
}
