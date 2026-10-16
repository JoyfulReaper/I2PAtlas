using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using I2PAtlas.AddressBook;
using I2PAtlas.I2pProbing;
using I2PAtlas.I2pScanning;
using I2PAtlas.I2pTransport;
using I2PAtlas.Seeds;

namespace I2PAtlas.I2pPublishing;

/// <summary>Local, fail-closed validation of private version-1 evidence. Never repairs or sorts input.</summary>
public static class I2pRawScanReader
{
    public const int MaxArtifactBytes = 128 * 1024 * 1024;

    public static async Task<I2pRawScanArtifact> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        path = I2pProjectionService.LocalPath(path);
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

    private static I2pRawScanArtifact Read(JsonElement root, CancellationToken token)
    {
        Fields(root, "schema", "version", "startedAtUtc", "completedAtUtc", "seedInput", "proxy", "settings",
            "snapshotSeedCount", "totalSeeds", "seedLimitApplied", "results", "metrics");
        Require(Text(root, "schema", 64) == I2pRawScanArtifact.CurrentSchema && Integer(root, "version", 1, 1) == 1,
            "Unsupported raw schema/version.");
        var started = Timestamp(root, "startedAtUtc");
        var completed = Timestamp(root, "completedAtUtc");
        Require(started <= completed, "Invalid scan timestamp ordering.");
        var source = root.GetProperty("seedInput");
        Fields(source, "schema", "version", "fileSha256", "fileByteLength", "importedAtUtc", "addressBookSha256", "addressBookByteLength");
        Require(Text(source, "schema", 64) == I2pSeedSnapshot.CurrentSchema, "Invalid seed input schema.");
        var seedInput = new I2pScanSeedInput(I2pSeedSnapshot.CurrentSchema, Integer(source, "version", 1, 1),
            Hash(source, "fileSha256"), Long(source, "fileByteLength", SeedSnapshotReader.MaxSnapshotBytes), Timestamp(source, "importedAtUtc"),
            Hash(source, "addressBookSha256"), Long(source, "addressBookByteLength", 4L * AddressBookParser.MaxInputCharacters));
        Require(seedInput.ImportedAtUtc <= started, "Seed import timestamp follows scan start.");
        var proxy = root.GetProperty("proxy");
        Fields(proxy, "address", "port");
        var address = Text(proxy, "address", 64);
        Require(IPAddress.TryParse(address, out var ip) && IPAddress.IsLoopback(ip) && !ip.IsIPv4MappedToIPv6 && ip.ToString() == address,
            "Invalid proxy identity.");
        var proxyIdentity = new I2pScanProxy(address, Integer(proxy, "port", 1, 65535));
        var configuration = root.GetProperty("settings");
        Fields(configuration, "concurrency", "requestTimeoutMilliseconds", "maximumSeeds");
        var settings = new I2pScanSettings(Integer(configuration, "concurrency", 1, I2pScanSettings.MaximumConcurrency),
            Integer(configuration, "requestTimeoutMilliseconds", 1, 60000), Integer(configuration, "maximumSeeds", 1, AddressBookParser.MaxSeeds));
        var snapshotCount = Integer(root, "snapshotSeedCount", 0, AddressBookParser.MaxSeeds);
        var total = Integer(root, "totalSeeds", 0, AddressBookParser.MaxSeeds);
        var entries = Array(root, "results", AddressBookParser.MaxSeeds);
        Require(entries.GetArrayLength() == total && total == Math.Min(snapshotCount, settings.MaximumSeeds) &&
            Boolean(root, "seedLimitApplied") == (total < snapshotCount), "Inconsistent result counts or seed limit.");
        var results = ImmutableArray.CreateBuilder<I2pRawSeedResult>(total);
        I2pSeed? previous = null;
        foreach (var entry in entries.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            var result = Result(entry, started, completed);
            var seed = new I2pSeed(result.Hostname, result.Destination);
            var hostnameOrder = previous == null ? -1 : string.CompareOrdinal(previous.Hostname, seed.Hostname);
            Require(previous == null || hostnameOrder < 0 || (hostnameOrder == 0 && string.CompareOrdinal(previous.Destination, seed.Destination) < 0),
                "Results must be strictly ordered by hostname and destination, without duplicate pairs.");
            previous = seed;
            results.Add(result);
        }
        var immutable = results.MoveToImmutable();
        var metrics = Metrics(root.GetProperty("metrics"), immutable, settings);
        return new(I2pRawScanArtifact.CurrentSchema, 1, started, completed, seedInput, proxyIdentity, settings, snapshotCount, immutable, metrics);
    }

    private static I2pRawSeedResult Result(JsonElement entry, DateTimeOffset scanStarted, DateTimeOffset scanCompleted)
    {
        Fields(entry, "hostname", "destination", "destinationHostname", "startedAtUtc", "completedAtUtc", "durationMilliseconds",
            "robotsRequestOutcome", "robotsOriginEvidence", "robotsProxyClassification", "robotsObservation", "robotsResponse",
            "homepageRequestAttempted", "homepageDisposition", "homepageRequestOutcome", "homepageOriginEvidence",
            "homepageProxyClassification", "homepageResponse", "homepageMetadata", "errors");
        var hostname = Text(entry, "hostname", 253);
        var destination = Text(entry, "destination", AddressBookParser.MaxDestinationCharacters);
        var seed = new I2pSeed(hostname, destination);
        Require(hostname == seed.Hostname && destination == seed.Destination, "Seed fields must already be normalized.");
        var b32 = Text(entry, "destinationHostname", 60);
        Require(b32 == new I2pHttpRequestTarget(seed, $"http://{hostname}/robots.txt").DestinationHostname, "Mismatched destination-derived identity.");
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
        var errors = ImmutableArray.CreateBuilder<I2pScanError>();
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
        return new(hostname, destination, b32, started, completed, Duration(entry, "durationMilliseconds"),
            robotsOutcome, robotsEvidence, robotsClass, robots, robotsResponse, attempted, disposition, homepageOutcome,
            homepageEvidence, homepageClass, homepageResponse, metadata, errors.ToImmutable());
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
            var host = new Uri(url).Host;
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

    private static I2pScanMetrics Metrics(JsonElement value, ImmutableArray<I2pRawSeedResult> results, I2pScanSettings settings)
    {
        Fields(value, "totalDurationMilliseconds", "robotsRequestOutcomes", "robotsProxyClassifications", "homepageRequestOutcomes",
            "homepageProxyClassifications", "homepageAttemptedCount", "homepageResponseCount", "unexpectedProbeFailureCount", "maximumActiveSeeds");
        var robots = Counts(value, "robotsRequestOutcomes", results.Select(r => r.RobotsRequestOutcome?.ToString() ?? "NotObserved"));
        var robotsClasses = Counts(value, "robotsProxyClassifications", results.Select(r => r.RobotsProxyClassification.ToString()));
        var homepage = Counts(value, "homepageRequestOutcomes", results.Select(r => r.HomepageRequestOutcome?.ToString() ??
            (r.HomepageRequestAttempted == false ? "NotAttempted" : "NotObserved")));
        var homepageClasses = Counts(value, "homepageProxyClassifications", results.Select(r => r.HomepageProxyClassification?.ToString() ?? "NotObserved"));
        var attempted = Integer(value, "homepageAttemptedCount", 0, results.Length);
        var responses = Integer(value, "homepageResponseCount", 0, results.Length);
        var unexpected = Integer(value, "unexpectedProbeFailureCount", 0, results.Length);
        Require(attempted == results.Count(r => r.HomepageRequestAttempted == true) && responses == results.Count(r => r.HomepageResponse != null) &&
            unexpected == results.Count(r => r.RobotsRequestOutcome == null), "Inconsistent scan metrics.");
        return new(Duration(value, "totalDurationMilliseconds"), robots, robotsClasses, homepage, homepageClasses, attempted, responses, unexpected,
            Integer(value, "maximumActiveSeeds", results.Length == 0 ? 0 : 1, Math.Min(results.Length, settings.Concurrency)));
    }

    private static ImmutableSortedDictionary<string, int> Counts(JsonElement parent, string name, IEnumerable<string> values)
    {
        var expected = values.GroupBy(v => v, StringComparer.Ordinal).ToImmutableSortedDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        Fields(parent.GetProperty(name), expected.Keys.ToArray());
        foreach (var pair in expected)
            Require(Integer(parent.GetProperty(name), pair.Key, 1, AddressBookParser.MaxSeeds) == pair.Value, "Inconsistent metric counts.");
        return expected;
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
