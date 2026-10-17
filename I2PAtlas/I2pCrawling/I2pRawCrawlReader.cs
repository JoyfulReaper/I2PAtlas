using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using I2PAtlas.AddressBook;
using I2PAtlas.I2pProbing;
using I2PAtlas.I2pTransport;
using I2PAtlas.Seeds;

namespace I2PAtlas.I2pCrawling;

/// <summary>Local, fail-closed validation of private v1/v2 evidence. Never repairs or sorts input.</summary>
internal static class I2pRawCrawlReader
{
    public const int MaxArtifactBytes = 128 * 1024 * 1024;

    public static async Task<I2pRawCrawlArtifact> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        path = I2pRawCrawlFile.LocalPath(path);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length > MaxArtifactBytes) throw Invalid("Raw artifact exceeds 128 MiB.");
        using var bytes = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = await file.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (bytes.Length + count > MaxArtifactBytes) throw Invalid("Raw artifact exceeds 128 MiB.");
            bytes.Write(buffer, 0, count);
        }
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var input = bytes.GetBuffer().AsMemory(0, (int)bytes.Length);
            _ = new UTF8Encoding(false, true).GetCharCount(input.Span);
            using var document = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = 16 });
            return Read(document.RootElement, cancellationToken);
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException or InvalidOperationException or FormatException or ArgumentException)
        {
            // Never echo JSON, destinations, filesystem paths or serializer exception text.
            throw Invalid("Invalid raw artifact JSON, encoding or field value.");
        }
    }

    private static I2pRawCrawlArtifact Read(JsonElement root, CancellationToken token)
    {
        Fields(root, "schema", "version", "crawlStartedAtUtc", "crawlCompletedAtUtc", "snapshot", "proxy", "settings",
            "snapshotSeedCount", "selectedSeedCount", "seedLimitApplied", "discovery", "knownSeedResults", "discoveredHostResults", "metrics");
        var version = Integer(root, "version", 1, 2);
        Require(Text(root, "schema", 64) == I2pRawCrawlArtifact.CurrentSchema,
            "Unsupported raw crawl schema/version.");
        var started = Timestamp(root, "crawlStartedAtUtc");
        var completed = Timestamp(root, "crawlCompletedAtUtc");
        Require(started <= completed, "Invalid crawl timestamp ordering.");
        var source = root.GetProperty("snapshot");
        Fields(source, "schema", "version", "fileSha256", "fileByteLength", "importedAtUtc", "sourceSha256", "sourceByteLength");
        Require(Text(source, "schema", 64) == I2pSeedSnapshot.CurrentSchema, "Invalid snapshot schema.");
        var snapshot = new I2pCrawlSnapshot(I2pSeedSnapshot.CurrentSchema, Integer(source, "version", 1, 1),
            Hash(source, "fileSha256"), Long(source, "fileByteLength", SeedSnapshotReader.MaxSnapshotBytes), Timestamp(source, "importedAtUtc"),
            Hash(source, "sourceSha256"), Long(source, "sourceByteLength", 4L * AddressBookParser.MaxInputCharacters));
        Require(snapshot.ImportedAtUtc <= started, "Snapshot import follows crawl start.");
        var proxy = root.GetProperty("proxy");
        Fields(proxy, "address", "port");
        var address = Text(proxy, "address", 64);
        Require(IPAddress.TryParse(address, out var ip) && IPAddress.IsLoopback(ip) && !ip.IsIPv4MappedToIPv6 && ip.ToString() == address,
            "Invalid proxy identity.");
        var proxyIdentity = new I2pCrawlProxy(address, Integer(proxy, "port", 1, 65535));
        var config = root.GetProperty("settings");
        Fields(config, version == 1 ? ["concurrency", "requestTimeoutMilliseconds", "maximumSeeds", "maximumDiscoveredHosts"] :
            ["concurrency", "requestTimeoutMilliseconds", "maximumSeeds", "maximumDiscoveredHosts", "maximumDepth"]);
        var settings = new I2pCrawlSettings(Integer(config, "concurrency", 1, 4), Integer(config, "requestTimeoutMilliseconds", 1, 60000),
            Integer(config, "maximumSeeds", 1, 10000), Integer(config, "maximumDiscoveredHosts", 0, 1000),
            version == 1 ? null : Integer(config, "maximumDepth", 1, 3));
        var snapshotCount = Integer(root, "snapshotSeedCount", 0, 10000);
        var knownArray = Array(root, "knownSeedResults", 10000);
        var count = Integer(root, "selectedSeedCount", 0, 10000);
        Require(count == knownArray.GetArrayLength() && count == Math.Min(snapshotCount, settings.MaximumSeeds) &&
            Boolean(root, "seedLimitApplied") == (count < snapshotCount), "Inconsistent seed selection counts.");
        var known = ImmutableArray.CreateBuilder<I2pCrawlKnownResult>(count);
        var identities = new HashSet<I2pCrawlReferrer>();
        var knownNames = new HashSet<string>(StringComparer.Ordinal);
        I2pSeed? previousSeed = null;
        foreach (var item in knownArray.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            Fields(item, "hostname", "destination", "destinationHostname", "depth", "evidence");
            var hostname = Text(item, "hostname", 253);
            var destination = Text(item, "destination", AddressBookParser.MaxDestinationCharacters);
            var seed = new I2pSeed(hostname, destination);
            Require(seed.Hostname == hostname && seed.Destination == destination, "Known seed must already be normalized.");
            var b32 = Text(item, "destinationHostname", 60);
            var identity = I2pCrawlReferrer.FromSeed(seed);
            Require(b32 == identity.DestinationHostname && identities.Add(identity), "Invalid or duplicate known semantic identity.");
            var order = previousSeed == null ? -1 : string.CompareOrdinal(previousSeed.Hostname, hostname);
            Require(previousSeed == null || order < 0 || order == 0 && string.CompareOrdinal(previousSeed.Destination, destination) < 0,
                "Known seeds must be strictly ordered by hostname then preserved destination text.");
            previousSeed = seed;
            knownNames.Add(hostname);
            knownNames.Add(b32);
            known.Add(new(hostname, destination, b32, Integer(item, "depth", 0, 0), Evidence(item.GetProperty("evidence"), started, completed)));
        }
        var discoveryValue = root.GetProperty("discovery");
        Fields(discoveryValue, "eligibleReferenceCount", "admittedHostCount", "admissionLimitApplied");
        var hostArray = Array(root, "discoveredHostResults", settings.MaximumDiscoveredHosts);
        var admitted = Integer(discoveryValue, "admittedHostCount", 0, settings.MaximumDiscoveredHosts);
        var references = Long(discoveryValue, "eligibleReferenceCount", (long)(count + (version == 1 ? 0 : admitted)) * I2pPageMetadata.MaxLinks);
        var limited = Boolean(discoveryValue, "admissionLimitApplied");
        Require(admitted == hostArray.GetArrayLength() && references >= admitted &&
            (!limited || admitted == settings.MaximumDiscoveredHosts && references > admitted) &&
            (admitted != 0 || references == 0 || settings.MaximumDiscoveredHosts == 0 && limited) &&
            (references != 0 || !limited), "Inconsistent discovery bounds/counts.");
        var hosts = ImmutableArray.CreateBuilder<I2pCrawlHostResult>(admitted);
        string? previousHost = null;
        var previousDepth = 1;
        var byHostname = new Dictionary<string, I2pCrawlHostResult>(StringComparer.Ordinal);
        var knownCompleted = known.Count == 0 ? started : known.Max(r => r.Evidence.CompletedAtUtc);
        var waveCompleted = new Dictionary<int, DateTimeOffset> { [0] = knownCompleted };
        foreach (var item in hostArray.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            Fields(item, "hostname", "hostForm", "depth", "referrer", "evidence");
            var hostname = Text(item, "hostname", 253);
            var depth = Integer(item, "depth", 1, settings.MaximumDepth);
            var target = new I2pProbeTarget.DiscoveredHost(hostname);
            Require(target.Hostname == hostname && target.HostForm == EnumValue<I2pDiscoveredHostForm>(item, "hostForm") &&
                !knownNames.Contains(hostname), "Invalid discovered host identity/form.");
            Require(!byHostname.ContainsKey(hostname) && (previousHost == null ? depth == 1 :
                depth == previousDepth && string.CompareOrdinal(previousHost, hostname) < 0 || depth == previousDepth + 1),
                "Discovered hosts must be unique and ordered by contiguous depth then hostname.");
            previousHost = hostname;
            previousDepth = depth;
            var referrer = item.GetProperty("referrer");
            Fields(referrer, depth == 1 ? ["hostname", "destinationHostname"] : ["hostname", "hostForm"]);
            var parent = depth == 1
                ? new I2pCrawlReferrer(Text(referrer, "hostname", 253), Text(referrer, "destinationHostname", 60))
                : new I2pCrawlReferrer(Text(referrer, "hostname", 253), null, EnumValue<I2pDiscoveredHostForm>(referrer, "hostForm"));
            I2pCrawlEvidence parentEvidence;
            if (depth == 1)
            {
                Require(identities.Contains(parent), "Referrer must exactly identify a selected known seed.");
                parentEvidence = known.First(k => k.Hostname == parent.Hostname && k.DestinationHostname == parent.DestinationHostname).Evidence;
            }
            else
            {
                Require(byHostname.TryGetValue(parent.Hostname, out var parentHost) && parentHost.Depth == depth - 1 && parentHost.HostForm == parent.HostForm,
                    "Host-only referrer must identify a host in the immediately preceding wave.");
                parentEvidence = byHostname[parent.Hostname].Evidence;
            }
            if (version == 2)
                Require(parentEvidence.ToObservation() is { } observation && I2pHostDiscovery.Eligible(observation) &&
                    observation.HomepageMetadata!.Links.Any(l => I2pHostDiscovery.Candidate(l)?.Hostname == hostname),
                    "Referrer must contain eligible discovery evidence for its child.");
            var evidence = Evidence(item.GetProperty("evidence"), started, completed);
            Require(waveCompleted.TryGetValue(depth - 1, out var barrier) && evidence.StartedAtUtc >= barrier,
                "Discovered observation precedes its breadth-first barrier.");
            waveCompleted[depth] = waveCompleted.TryGetValue(depth, out var end) && end > evidence.CompletedAtUtc ? end : evidence.CompletedAtUtc;
            var host = new I2pCrawlHostResult(hostname, target.HostForm, depth, parent, evidence);
            hosts.Add(host);
            byHostname.Add(hostname, host);
        }
        var immutableKnown = known.ToImmutable();
        var immutableHosts = hosts.ToImmutable();
        var evidenceAll = immutableKnown.Select(r => r.Evidence).Concat(immutableHosts.Select(r => r.Evidence)).ToArray();
        var largestWave = Math.Max(count, immutableHosts.GroupBy(h => h.Depth).Select(g => g.Count()).DefaultIfEmpty().Max());
        var metrics = Metrics(root.GetProperty("metrics"), evidenceAll, settings, largestWave);
        var result = new I2pRawCrawlArtifact(I2pRawCrawlArtifact.CurrentSchema, version, started, completed, snapshot, proxyIdentity,
            settings, snapshotCount, new(references, admitted, limited), immutableKnown, immutableHosts, metrics);
        Require(result.TextCharacters() <= I2pCrawlService.MaxRetainedTextCharacters, "Crawl exceeds retained evidence budget.");
        token.ThrowIfCancellationRequested();
        return result;
    }

    private static I2pCrawlMetrics Metrics(JsonElement value, I2pCrawlEvidence[] evidence, I2pCrawlSettings settings, int largestWave)
    {
        Fields(value, "totalDurationMilliseconds", "robotsRequestOutcomes", "robotsProxyClassifications", "homepageRequestOutcomes",
            "homepageProxyClassifications", "homepageAttemptedCount", "homepageResponseCount", "unexpectedProbeFailureCount", "maximumActiveProbes");
        var active = Integer(value, "maximumActiveProbes", evidence.Length == 0 ? 0 : 1, Math.Min(settings.Concurrency, largestWave));
        var expected = I2pCrawlMetrics.Calculate(evidence, Duration(value, "totalDurationMilliseconds"), active);
        Counts("robotsRequestOutcomes", expected.RobotsRequestOutcomes);
        Counts("robotsProxyClassifications", expected.RobotsProxyClassifications);
        Counts("homepageRequestOutcomes", expected.HomepageRequestOutcomes);
        Counts("homepageProxyClassifications", expected.HomepageProxyClassifications);
        Require(Integer(value, "homepageAttemptedCount", 0, evidence.Length) == expected.HomepageAttemptedCount &&
            Integer(value, "homepageResponseCount", 0, evidence.Length) == expected.HomepageResponseCount &&
            Integer(value, "unexpectedProbeFailureCount", 0, evidence.Length) == expected.UnexpectedProbeFailureCount, "Inconsistent crawl metrics.");
        return expected;

        void Counts(string name, ImmutableSortedDictionary<string, int> counts)
        {
            Fields(value.GetProperty(name), counts.Keys.ToArray());
            foreach (var pair in counts)
                Require(Integer(value.GetProperty(name), pair.Key, 1, 11000) == pair.Value, "Inconsistent metric counts.");
        }
    }

    private static I2pCrawlEvidence Evidence(JsonElement entry, DateTimeOffset scanStarted, DateTimeOffset scanCompleted)
    {
        Fields(entry, "startedAtUtc", "completedAtUtc", "durationMilliseconds",
            "robotsRequestOutcome", "robotsOriginEvidence", "robotsProxyClassification", "robotsObservation", "robotsResponse",
            "homepageRequestAttempted", "homepageDisposition", "homepageRequestOutcome", "homepageOriginEvidence",
            "homepageProxyClassification", "homepageResponse", "homepageMetadata", "errors");
        var started = Timestamp(entry, "startedAtUtc");
        var completed = Timestamp(entry, "completedAtUtc");
        Require(scanStarted <= started && started <= completed && completed <= scanCompleted, "Invalid result timestamp ordering.");
        var robotsOutcome = EnumValue<I2pRequestOutcome>(entry, "robotsRequestOutcome", true);
        var robotsEvidence = EnumValue<I2pOriginEvidence>(entry, "robotsOriginEvidence")!.Value;
        var robotsClass = EnumValue<I2pProxyResponseClassification>(entry, "robotsProxyClassification")!.Value;
        var robots = EnumValue<I2pRobotsObservation>(entry, "robotsObservation")!.Value;
        var robotsResponse = Response(entry.GetProperty("robotsResponse"));
        var attempted = NullableBoolean(entry, "homepageRequestAttempted");
        var disposition = EnumValue<I2pHomepageDisposition>(entry, "homepageDisposition", true);
        var homepageOutcome = EnumValue<I2pRequestOutcome>(entry, "homepageRequestOutcome", true);
        var homepageEvidence = EnumValue<I2pOriginEvidence>(entry, "homepageOriginEvidence")!.Value;
        var homepageClass = EnumValue<I2pProxyResponseClassification>(entry, "homepageProxyClassification", true);
        var homepageResponse = Response(entry.GetProperty("homepageResponse"));
        var metadata = Metadata(entry.GetProperty("homepageMetadata"));
        var errors = ImmutableArray.CreateBuilder<I2pCrawlError>();
        foreach (var error in Array(entry, "errors", 3).EnumerateArray())
        {
            Fields(error, "phase", "category", "detail");
            var phase = Text(error, "phase", 16);
            Require(phase is "robots" or "homepage" or "probe", "Invalid error phase.");
            errors.Add(new(phase, Text(error, "category", 64), Text(error, "detail", 256)));
        }
        if (robotsOutcome == null)
        {
            Require(robotsEvidence == I2pOriginEvidence.NotObserved && robotsClass == I2pProxyResponseClassification.Ambiguous &&
                robots == I2pRobotsObservation.NotObserved && robotsResponse == null && attempted == null && disposition == null &&
                homepageOutcome == null && homepageClass == null && homepageResponse == null && metadata == null &&
                errors.Count == 1 && errors[0].Phase == "probe" && errors[0].Category == "UnexpectedProbeFailure", "Invalid unknown probe state.");
        }
        else
        {
            Require(attempted.HasValue && disposition.HasValue, "Missing known probe state.");
            Require((robotsOutcome == I2pRequestOutcome.ProxyHttpResponse) == (robotsResponse != null), "Robots response/outcome mismatch.");
            Require(robotsResponse == null ? robots == I2pRobotsObservation.NotObserved && robotsClass == I2pProxyResponseClassification.Ambiguous
                : RobotsConsistent(robotsResponse, robots, robotsClass), "Inconsistent robots observation/classification.");
            var gate = robotsClass == I2pProxyResponseClassification.LikelyOriginResponse && robots == I2pRobotsObservation.AllowsRoot;
            Require(attempted == gate && attempted == homepageOutcome.HasValue, "Homepage attempt violates recorded robots authorization.");
            var expectedDisposition = attempted == true
                ? homepageOutcome == I2pRequestOutcome.ProxyHttpResponse ? I2pHomepageDisposition.ResponseObserved : I2pHomepageDisposition.RequestFailed
                : robotsOutcome != I2pRequestOutcome.ProxyHttpResponse
                    ? robotsOutcome == I2pRequestOutcome.RejectedBeforeNetworking ? I2pHomepageDisposition.SkippedRequestRejected : I2pHomepageDisposition.SkippedRequestFailed
                    : robotsClass == I2pProxyResponseClassification.LikelyProxyFailure ? I2pHomepageDisposition.SkippedProxyFailure
                    : robots == I2pRobotsObservation.DisallowsRoot ? I2pHomepageDisposition.SkippedObservedDisallow
                    : robots == I2pRobotsObservation.AllowsRoot ? I2pHomepageDisposition.SkippedAmbiguousResponse : I2pHomepageDisposition.SkippedRobotsUnknown;
            Require(disposition == expectedDisposition, "Inconsistent homepage disposition.");
        }
        Require(robotsEvidence == (robotsResponse == null ? I2pOriginEvidence.NotObserved : I2pOriginEvidence.Unproven) &&
            homepageEvidence == (homepageResponse == null ? I2pOriginEvidence.NotObserved : I2pOriginEvidence.Unproven), "Inconsistent origin evidence.");
        Require((homepageOutcome == I2pRequestOutcome.ProxyHttpResponse) == (homepageResponse != null), "Homepage response/outcome mismatch.");
        Require(homepageClass == (homepageResponse == null ? (I2pProxyResponseClassification?)null : StatusClass(homepageResponse.StatusCode)),
            "Inconsistent homepage classification.");
        Require(metadata == null || (homepageResponse?.StatusCode == 200 && metadata.ContentType == homepageResponse.ContentType &&
            metadata.BodyBytesExamined == homepageResponse.BodyBytes), "Metadata requires a matching homepage response.");
        var result = new I2pCrawlEvidence(started, completed, Duration(entry, "durationMilliseconds"),
            robotsOutcome, robotsEvidence, robotsClass, robots, robotsResponse, attempted, disposition, homepageOutcome,
            homepageEvidence, homepageClass, homepageResponse, metadata, errors.ToImmutable());
        var expectedErrors = I2pCrawlEvidence.FromObservation(result.ToObservation(), started, completed, result.DurationMilliseconds).Errors;
        Require(result.Errors.SequenceEqual(expectedErrors), "Noncanonical probe error categories/details.");
        return result;
    }

    private static bool RobotsConsistent(I2pResponseObservation response, I2pRobotsObservation robots, I2pProxyResponseClassification classification)
    {
        if (response.StatusCode is >= 300 and <= 399)
            return robots == I2pRobotsObservation.Redirect && classification == I2pProxyResponseClassification.Ambiguous;
        if (response.StatusCode != 200)
            return robots == I2pRobotsObservation.UnknownStatus && classification == StatusClass(response.StatusCode);
        if (response.BodyBytes > I2pHttpProbe.MaxRobotsBytes)
            return robots == I2pRobotsObservation.TooLarge && classification == I2pProxyResponseClassification.Ambiguous;
        if (robots is not (I2pRobotsObservation.AllowsRoot or I2pRobotsObservation.DisallowsRoot or
            I2pRobotsObservation.Malformed or I2pRobotsObservation.UnsupportedRepresentation)) return false;
        if (classification == I2pProxyResponseClassification.Ambiguous) return true;
        return classification == I2pProxyResponseClassification.LikelyOriginResponse &&
            robots is I2pRobotsObservation.AllowsRoot or I2pRobotsObservation.DisallowsRoot &&
            MediaTypeHeaderValue.TryParse(response.ContentType, out var type) &&
            string.Equals(type.MediaType, "text/plain", StringComparison.OrdinalIgnoreCase) &&
            (type.CharSet == null || type.CharSet.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase));
        // Raw state has no bodies; validation cannot re-parse robots or authenticate proxy authorship.
    }

    private static I2pProxyResponseClassification StatusClass(int status) => status is 502 or 503 or 504
        ? I2pProxyResponseClassification.LikelyProxyFailure : I2pProxyResponseClassification.Ambiguous;

    private static I2pResponseObservation? Response(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        Fields(value, "statusCode", "contentType", "bodyBytes", "redirectLocation", "redirectLocationTruncated");
        var redirect = OptionalText(value, "redirectLocation", I2pHttpProbe.MaxRedirectCharacters);
        var truncated = Boolean(value, "redirectLocationTruncated");
        Require(!truncated || redirect?.Length == I2pHttpProbe.MaxRedirectCharacters, "Invalid redirect truncation flag.");
        return new(Integer(value, "statusCode", 100, 999), OptionalText(value, "contentType", I2pHttpProbe.MaxContentTypeCharacters),
            Integer(value, "bodyBytes", 0, I2pHttpTransport.MaxResponseBytes), redirect, truncated);
    }

    private static I2pPageMetadata? Metadata(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        Fields(value, "contentType", "bodyBytesExamined", "bodyTruncated", "title", "links", "extractionIncomplete");
        var links = ImmutableArray.CreateBuilder<I2pDiscoveredLink>();
        string? previous = null;
        foreach (var item in Array(value, "links", I2pPageMetadata.MaxLinks).EnumerateArray())
        {
            Fields(item, "url", "kind");
            var url = Text(item, "url", I2pHttpRequestTarget.MaxUrlCharacters);
            var kind = EnumValue<I2pDiscoveredLinkKind>(item, "kind")!.Value;
            Require(url.Length > 0 && !url.Any(char.IsControl) && !url.Contains('\\') &&
                Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" &&
                uri.HostNameType == UriHostNameType.Dns, "Invalid discovered URL.");
            var parsedUri = new Uri(url);
            Require(parsedUri.UserInfo.Length == 0 && parsedUri.Fragment.Length == 0 &&
                !parsedUri.Query.TrimStart('?').Split('&').Any(p => Uri.UnescapeDataString(p.Split('=', 2)[0]).Equals("i2paddresshelper", StringComparison.OrdinalIgnoreCase)),
                "Unsafe extracted URL.");
            var host = parsedUri.Host;
            var i2p = host.TrimEnd('.').EndsWith(".i2p", StringComparison.OrdinalIgnoreCase);
            if (i2p) _ = I2pSeed.NormalizeHostname(host);
            Require((kind == I2pDiscoveredLinkKind.I2p) == i2p, "Discovered link kind does not match its host.");
            Require(previous == null || string.CompareOrdinal(previous, url) < 0, "Discovered URLs must be unique and ordinally sorted.");
            previous = url;
            links.Add(new(url, kind));
        }
        var truncated = Boolean(value, "bodyTruncated");
        var incomplete = Boolean(value, "extractionIncomplete");
        Require(!truncated || incomplete, "Truncated metadata must mark extraction incomplete.");
        return new(OptionalText(value, "contentType", I2pHttpProbe.MaxContentTypeCharacters),
            Integer(value, "bodyBytesExamined", 0, I2pPageMetadata.MaxBodyBytes), truncated,
            OptionalText(value, "title", I2pPageMetadata.MaxTitleCharacters), links.ToImmutable(), incomplete);
    }

    private static void Fields(JsonElement value, params string[] fields)
    {
        Require(value.ValueKind == JsonValueKind.Object, "Expected a JSON object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            Require(fields.Contains(property.Name, StringComparer.Ordinal) && seen.Add(property.Name), "Unknown or duplicate JSON property.");
        Require(seen.Count == fields.Length, "Missing required JSON property.");
    }

    private static JsonElement Array(JsonElement parent, string name, int maximum)
    {
        var value = parent.GetProperty(name);
        Require(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() <= maximum, "Invalid or oversized array.");
        return value;
    }

    private static string Text(JsonElement parent, string name, int maximum)
    {
        var value = parent.GetProperty(name);
        Require(value.ValueKind == JsonValueKind.String, "Expected a JSON string.");
        var text = value.GetString()!;
        Require(text.Length <= maximum, "String exceeds field bound.");
        return text;
    }
    private static string? OptionalText(JsonElement parent, string name, int maximum) =>
        parent.GetProperty(name).ValueKind == JsonValueKind.Null ? null : Text(parent, name, maximum);
    private static string Hash(JsonElement parent, string name)
    {
        var hash = Text(parent, name, 64);
        Require(hash.Length == 64 && hash.All(char.IsAsciiHexDigit), "Invalid provenance hash.");
        return hash;
    }
    private static long Long(JsonElement parent, string name, long maximum)
    {
        var value = parent.GetProperty(name);
        Require(value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _), "Expected a JSON integer.");
        var number = value.GetInt64();
        Require(number >= 0 && number <= maximum, "Integer outside field bound.");
        return number;
    }
    private static int Integer(JsonElement parent, string name, int minimum, int maximum)
    {
        var number = Long(parent, name, maximum);
        Require(number >= minimum, "Integer outside field bound.");
        return (int)number;
    }
    private static double Duration(JsonElement parent, string name)
    {
        var value = parent.GetProperty(name);
        Require(value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out _), "Expected duration number.");
        var duration = value.GetDouble();
        Require(double.IsFinite(duration) && duration >= 0 && duration <= TimeSpan.MaxValue.TotalMilliseconds, "Invalid duration.");
        return duration;
    }
    private static bool Boolean(JsonElement parent, string name)
    {
        var value = parent.GetProperty(name);
        Require(value.ValueKind is JsonValueKind.True or JsonValueKind.False, "Expected a JSON boolean.");
        return value.GetBoolean();
    }
    private static bool? NullableBoolean(JsonElement parent, string name) =>
        parent.GetProperty(name).ValueKind == JsonValueKind.Null ? null : Boolean(parent, name);
    private static T? EnumValue<T>(JsonElement parent, string name, bool nullable = false) where T : struct, Enum
    {
        if (nullable && parent.GetProperty(name).ValueKind == JsonValueKind.Null) return null;
        var text = Text(parent, name, 64);
        Require(Enum.GetNames<T>().Contains(text, StringComparer.Ordinal), "Unrecognized enum string.");
        return Enum.Parse<T>(text, ignoreCase: false);
    }
    private static DateTimeOffset Timestamp(JsonElement parent, string name)
    {
        var text = Text(parent, name, 64);
        Require((text.EndsWith('Z') || text.EndsWith("+00:00", StringComparison.Ordinal)) && parent.GetProperty(name).TryGetDateTimeOffset(out _),
            "Expected explicit UTC timestamp.");
        var timestamp = parent.GetProperty(name).GetDateTimeOffset();
        Require(timestamp.Offset == TimeSpan.Zero, "Expected explicit UTC timestamp.");
        return timestamp;
    }
    private static void Require(bool condition, string message) { if (!condition) throw Invalid(message); }
    private static InvalidDataException Invalid(string message) => new(message);
}
