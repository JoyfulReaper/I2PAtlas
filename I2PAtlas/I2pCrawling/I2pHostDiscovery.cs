using System.Collections.Immutable;
using System.Net.Http.Headers;
using I2PAtlas.AddressBook;
using I2PAtlas.I2pProbing;
using I2PAtlas.I2pTransport;
using I2PAtlas.Seeds;

namespace I2PAtlas.I2pCrawling;

/// <summary>Pure bounded frontier selection from one completed wave; performs no requests or I/O.</summary>
internal static class I2pHostDiscovery
{
    internal static I2pCrawlFrontier Select(I2pSeedSnapshot fullSnapshot,
        ImmutableArray<I2pCrawlSeedObservation> selectedParents,
        I2pCrawlDiscoverySettings? settings = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (selectedParents.IsDefault || selectedParents.Length > AddressBookParser.MaxSeeds)
            throw new ArgumentException("Selected parent observations must be initialized and bounded.", nameof(selectedParents));
        foreach (var parent in selectedParents)
        {
            ArgumentNullException.ThrowIfNull(parent);
            ArgumentNullException.ThrowIfNull(parent.Target);
        }
        return SelectWave(fullSnapshot, selectedParents.Select(p => new I2pCrawlParentObservation(
            new(p.Target.Seed.Hostname, p.Target.DestinationHostname), p.Observation)).ToImmutableArray(),
            new HashSet<string>(StringComparer.Ordinal), 1, settings, cancellationToken);
    }

    internal static I2pCrawlFrontier SelectWave(I2pSeedSnapshot fullSnapshot,
        ImmutableArray<I2pCrawlParentObservation> selectedParents, IReadOnlySet<string> previouslyAdmitted, int depth,
        I2pCrawlDiscoverySettings? settings = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (depth is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(depth));
        ArgumentNullException.ThrowIfNull(fullSnapshot);
        ArgumentNullException.ThrowIfNull(previouslyAdmitted);
        if (previouslyAdmitted.Count > I2pCrawlDiscoverySettings.HardMaximumDiscoveredHosts)
            throw new ArgumentException("Earlier admissions exceed the global host bound.", nameof(previouslyAdmitted));
        if (fullSnapshot.Seeds.IsDefault || fullSnapshot.Seeds.Length > AddressBookParser.MaxSeeds)
            throw new ArgumentException("A complete validated seed snapshot is required.", nameof(fullSnapshot));
        if (selectedParents.IsDefault || selectedParents.Length > AddressBookParser.MaxSeeds)
            throw new ArgumentException("Selected parent observations must be initialized and bounded.", nameof(selectedParents));
        settings ??= new I2pCrawlDiscoverySettings();

        var knownNames = new HashSet<string>(StringComparer.Ordinal);
        var knownIdentities = new HashSet<I2pCrawlReferrer>();
        foreach (var seed in fullSnapshot.Seeds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var identity = I2pCrawlReferrer.FromSeed(seed);
            knownIdentities.Add(identity);
            knownNames.Add(identity.Hostname);
            knownNames.Add(identity.DestinationHostname!);
        }

        var seenParents = new HashSet<I2pCrawlReferrer>();
        var names = new SortedSet<string>(StringComparer.Ordinal);
        var retained = new Dictionary<string, I2pCrawlWorkItem>(StringComparer.Ordinal);
        long references = 0;
        var limited = false;
        foreach (var parent in selectedParents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(parent);
            ArgumentNullException.ThrowIfNull(parent.Observation);
            var referrer = parent.Referrer;
            if (!seenParents.Add(referrer) ||
                (depth == 1 ? !knownIdentities.Contains(referrer) :
                    referrer.DestinationHostname != null || referrer.HostForm == null || !previouslyAdmitted.Contains(referrer.Hostname)))
                throw new ArgumentException("Parents must identify distinct completed targets from the preceding wave.", nameof(selectedParents));
            var metadata = parent.Observation.HomepageMetadata;
            if (metadata != null && (metadata.Links.IsDefault || metadata.Links.Length > I2pPageMetadata.MaxLinks))
                throw new ArgumentException("Parent link observations must be initialized and bounded.", nameof(selectedParents));
            if (!Eligible(parent.Observation)) continue;

            foreach (var link in metadata!.Links)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = Candidate(link);
                if (target == null || knownNames.Contains(target.Hostname) || previouslyAdmitted.Contains(target.Hostname)) continue;
                references++;
                if (retained.TryGetValue(target.Hostname, out var existing))
                {
                    if (referrer.CompareTo(existing.FirstReferrer) < 0)
                        retained[target.Hostname] = existing with { FirstReferrer = referrer };
                    continue;
                }
                if (retained.Count == settings.MaximumDiscoveredHosts)
                {
                    limited = true;
                    if (settings.MaximumDiscoveredHosts == 0 || string.CompareOrdinal(target.Hostname, names.Max) >= 0)
                        continue;
                    var largest = names.Max!;
                    names.Remove(largest);
                    retained.Remove(largest);
                }
                names.Add(target.Hostname);
                retained.Add(target.Hostname, new(target, referrer, depth));
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(names.Select(name => retained[name]).ToImmutableArray(), references, limited);
    }

    internal static bool Eligible(I2pProbeObservation observation) =>
        observation.HomepageRequestAttempted && observation.HomepageRequestOutcome == I2pRequestOutcome.ProxyHttpResponse &&
        observation.HomepageResponse is { StatusCode: 200 } response &&
        observation.HomepageMetadata is { BodyTruncated: false, ExtractionIncomplete: false } metadata &&
        HtmlMediaType(response.ContentType) is { } media && media == HtmlMediaType(metadata.ContentType);

    private static string? HtmlMediaType(string? value)
    {
        if (value == null || value.Length > I2pHttpProbe.MaxContentTypeCharacters ||
            !MediaTypeHeaderValue.TryParse(value, out var parsed)) return null;
        if (string.Equals(parsed.MediaType, "text/html", StringComparison.OrdinalIgnoreCase)) return "text/html";
        return string.Equals(parsed.MediaType, "application/xhtml+xml", StringComparison.OrdinalIgnoreCase)
            ? "application/xhtml+xml" : null;
    }

    internal static I2pProbeTarget.DiscoveredHost? Candidate(I2pDiscoveredLink? link)
    {
        if (link is not { Kind: I2pDiscoveredLinkKind.I2p } || string.IsNullOrEmpty(link.Url)) return null;
        var url = link.Url;
        if (url.Length > I2pHttpRequestTarget.MaxUrlCharacters || !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == '\\') ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "http" || uri.Port != 80 ||
            uri.UserInfo.Length != 0 || uri.HostNameType != UriHostNameType.Dns) return null;

        // Reject helpers before reducing the URL to a host, including encoded/case variants.
        foreach (var parameter in uri.Query.TrimStart('?').Split('&'))
            if (Uri.UnescapeDataString(parameter.Split('=', 2)[0]).Equals("i2paddresshelper", StringComparison.OrdinalIgnoreCase))
                return null;

        // Use the raw hostname so URI repair/IDN/percent decoding cannot bypass the
        // same host validation that Crawler 0 uses. URI parsing already checked port 80.
        var authority = url.AsSpan(7);
        var end = authority.IndexOfAny('/', '?', '#');
        if (end >= 0) authority = authority[..end];
        if (authority.Contains('@')) return null;
        var colon = authority.LastIndexOf(':');
        if (colon >= 0) authority = authority[..colon];
        try { return new I2pProbeTarget.DiscoveredHost(authority.ToString()); }
        catch (FormatException) { return null; }
    }
}
