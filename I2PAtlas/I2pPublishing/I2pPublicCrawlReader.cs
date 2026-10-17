using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using I2PAtlas.AddressBook;
using I2PAtlas.I2pProbing;
using I2PAtlas.I2pTransport;

namespace I2PAtlas.I2pPublishing;

/// <summary>Strict local public-crawl v1 boundary. Never repairs ordering or infers destination identity.</summary>
public static class I2pPublicCrawlReader
{
    public const int MaxArtifactBytes = 128 * 1024 * 1024;
    public const int MaxKnownSeeds = 10000;
    public const int MaxDiscoveredHosts = 1000;

    public static async Task<I2pPublicCrawl> ReadAsync(string path, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        path = I2pCrawlProjectionService.LocalPathWithoutLinks(path);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        Require(file.Length <= MaxArtifactBytes, "Public crawl exceeds 128 MiB.");
        using var bytes = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = await file.ReadAsync(buffer, token)) != 0)
        {
            Require(bytes.Length + count <= MaxArtifactBytes, "Public crawl exceeds 128 MiB.");
            bytes.Write(buffer, 0, count);
        }
        token.ThrowIfCancellationRequested();
        try
        {
            var input = bytes.GetBuffer().AsMemory(0, (int)bytes.Length);
            _ = new UTF8Encoding(false, true).GetCharCount(input.Span);
            using var document = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = 8 });
            return Read(document.RootElement, token);
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException or InvalidOperationException or FormatException or ArgumentException)
        {
            throw new InvalidDataException("Invalid public crawl JSON, encoding or field value.");
        }
    }

    private static I2pPublicCrawl Read(JsonElement root, CancellationToken token)
    {
        Fields(root, "schema", "version", "crawlStartedAtUtc", "crawlCompletedAtUtc", "knownSeedCount", "discoveredHostCount", "entries");
        Require(Text(root, "schema", 64) == I2pPublicCrawl.CurrentSchema && Integer(root, "version", 1, 1) == 1,
            "Expected i2patlas.public-crawl version 1.");
        var started = Timestamp(root, "crawlStartedAtUtc");
        var completed = Timestamp(root, "crawlCompletedAtUtc");
        Require(started <= completed, "Crawl completion precedes crawl start.");
        var knownCount = Integer(root, "knownSeedCount", 0, MaxKnownSeeds);
        var hostCount = Integer(root, "discoveredHostCount", 0, MaxDiscoveredHosts);
        var array = Array(root, "entries", MaxKnownSeeds + MaxDiscoveredHosts);
        Require(knownCount + hostCount == array.GetArrayLength(), "Inconsistent public crawl counts.");
        var entries = ImmutableArray.CreateBuilder<I2pPublicCrawlEntry>(array.GetArrayLength());
        var identities = new HashSet<(string Hostname, string DestinationHostname)>();
        var knownNames = new HashSet<string>(StringComparer.Ordinal);
        (string Hostname, string DestinationHostname)? previousKnown = null;
        string? previousHost = null;
        var discoveredSeen = false;
        foreach (var item in array.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            Require(item.ValueKind == JsonValueKind.Object && item.TryGetProperty("kind", out _), "Missing entry kind.");
            var kind = EnumValue<I2pPublicCrawlEntryKind>(item, "kind")!.Value;
            Fields(item, kind == I2pPublicCrawlEntryKind.KnownSeed
                ? ["kind", "hostname", "destinationHostname", "depth", "observation"]
                : ["kind", "hostname", "hostForm", "depth", "referrerHostname", "referrerDestinationHostname", "observation"]);
            var hostname = Host(Text(item, "hostname", 253));
            var observation = Observation(item.GetProperty("observation"));
            if (kind == I2pPublicCrawlEntryKind.KnownSeed)
            {
                Require(!discoveredSeen, "Known seeds must precede discovered hosts.");
                var b32 = B32(Text(item, "destinationHostname", 60));
                Integer(item, "depth", 0, 0);
                var order = previousKnown == null ? -1 : string.CompareOrdinal(previousKnown.Value.Hostname, hostname);
                Require(previousKnown == null || order < 0 || order == 0 && string.CompareOrdinal(previousKnown.Value.DestinationHostname, b32) < 0,
                    "Known entries must be ordered by hostname then B32 without duplicates.");
                Require(identities.Add((hostname, b32)), "Duplicate known semantic identity.");
                previousKnown = (hostname, b32);
                knownNames.Add(hostname);
                knownNames.Add(b32);
                entries.Add(new I2pPublicCrawlEntry.KnownSeed(hostname, b32, observation));
            }
            else
            {
                discoveredSeen = true;
                Integer(item, "depth", 1, 1);
                var target = new I2pProbeTarget.DiscoveredHost(hostname);
                var form = EnumValue<I2pPublicCrawlHostForm>(item, "hostForm")!.Value;
                Require((form == I2pPublicCrawlHostForm.ClassicBase32) == (target.HostForm == I2pDiscoveredHostForm.ClassicBase32),
                    "Discovered host form disagrees with its hostname.");
                Require(!knownNames.Contains(hostname) && (previousHost == null || string.CompareOrdinal(previousHost, hostname) < 0),
                    "Discovered hosts must be ordered, unique and distinct from known hosts.");
                previousHost = hostname;
                var referrer = Host(Text(item, "referrerHostname", 253));
                var referrerB32 = B32(Text(item, "referrerDestinationHostname", 60));
                Require(identities.Contains((referrer, referrerB32)), "Referrer must exactly identify one public known seed.");
                entries.Add(new I2pPublicCrawlEntry.DiscoveredHost(hostname, form, referrer, referrerB32, observation));
            }
        }
        var result = new I2pPublicCrawl(started, completed, entries.MoveToImmutable());
        Require(result.KnownSeedCount == knownCount && result.DiscoveredHostCount == hostCount, "Entry kinds disagree with counts.");
        token.ThrowIfCancellationRequested();
        return result;
    }

    private static I2pPublicCrawlObservation Observation(JsonElement value)
    {
        Fields(value, "summaryState", "robotsRequestOutcome", "robotsProxyClassification", "robotsObservation", "robotsStatusCode",
            "homepageRequestAttempted", "homepageRequestOutcome", "homepageProxyClassification", "homepageStatusCode",
            "homepageContentType", "title", "discoveredI2pHostnames", "linkExtractionIncomplete");
        var title = OptionalText(value, "title", I2pPublicProjectionPolicy.MaxTitleCharacters);
        Require(title == null || I2pPublicProjectionPolicy.Title(title) == title, "Invalid public title.");
        var type = OptionalText(value, "homepageContentType", 80);
        Require(type == null || I2pPublicProjectionPolicy.MediaType(type) == type, "Invalid public media type.");
        var hosts = ImmutableArray.CreateBuilder<string>();
        string? previous = null;
        foreach (var item in Array(value, "discoveredI2pHostnames", 250).EnumerateArray())
        {
            Require(item.ValueKind == JsonValueKind.String, "Expected hostname text.");
            var host = Host(item.GetString()!);
            Require(previous == null || string.CompareOrdinal(previous, host) < 0, "Observed hostnames must be unique and ordered.");
            hosts.Add(host);
            previous = host;
        }
        var e = new I2pPublicCrawlObservation(EnumValue<I2pPublicSummaryState>(value, "summaryState")!.Value,
            EnumValue<I2pRequestOutcome>(value, "robotsRequestOutcome", true),
            EnumValue<I2pProxyResponseClassification>(value, "robotsProxyClassification")!.Value,
            EnumValue<I2pRobotsObservation>(value, "robotsObservation")!.Value, Status(value, "robotsStatusCode"),
            NullableBoolean(value, "homepageRequestAttempted"), EnumValue<I2pRequestOutcome>(value, "homepageRequestOutcome", true),
            EnumValue<I2pProxyResponseClassification>(value, "homepageProxyClassification", true), Status(value, "homepageStatusCode"),
            type, title, hosts.ToImmutable(), Boolean(value, "linkExtractionIncomplete"));
        Require((e.RobotsRequestOutcome == I2pRequestOutcome.ProxyHttpResponse) == e.RobotsStatusCode.HasValue &&
            (e.HomepageRequestOutcome == I2pRequestOutcome.ProxyHttpResponse) == e.HomepageStatusCode.HasValue,
            "Request outcomes disagree with HTTP statuses.");
        Require(e.HomepageRequestAttempted.HasValue == e.RobotsRequestOutcome.HasValue &&
            e.HomepageRequestOutcome.HasValue == (e.HomepageRequestAttempted == true), "Inconsistent attempt state.");
        var robotsConsistent = e.RobotsStatusCode switch
        {
            null => e.RobotsObservation == I2pRobotsObservation.NotObserved && e.RobotsProxyClassification == I2pProxyResponseClassification.Ambiguous,
            >= 300 and <= 399 => e.RobotsObservation == I2pRobotsObservation.Redirect && e.RobotsProxyClassification == I2pProxyResponseClassification.Ambiguous,
            200 => e.RobotsObservation is I2pRobotsObservation.AllowsRoot or I2pRobotsObservation.DisallowsRoot or
                I2pRobotsObservation.Malformed or I2pRobotsObservation.UnsupportedRepresentation or I2pRobotsObservation.TooLarge &&
                (e.RobotsProxyClassification == I2pProxyResponseClassification.Ambiguous ||
                 e.RobotsProxyClassification == I2pProxyResponseClassification.LikelyOriginResponse &&
                 e.RobotsObservation is I2pRobotsObservation.AllowsRoot or I2pRobotsObservation.DisallowsRoot),
            _ => e.RobotsObservation == I2pRobotsObservation.UnknownStatus && e.RobotsProxyClassification == StatusClass(e.RobotsStatusCode)
        };
        Require(robotsConsistent, "Inconsistent robots observations/classification.");
        Require(e.HomepageRequestAttempted == null || e.HomepageRequestAttempted ==
            (e.RobotsProxyClassification == I2pProxyResponseClassification.LikelyOriginResponse && e.RobotsObservation == I2pRobotsObservation.AllowsRoot),
            "Homepage attempt violates recorded robots authorization.");
        Require(e.HomepageProxyClassification == StatusClass(e.HomepageStatusCode) && (type == null || e.HomepageStatusCode.HasValue),
            "Inconsistent homepage classification/representation.");
        Require(e.HomepageStatusCode == 200 || e.LinkExtractionIncomplete, "Absent page extraction must be incomplete.");
        Require(title == null && hosts.Count == 0 || e.HomepageStatusCode == 200, "Page metadata requires HTTP 200.");
        Require(e.SummaryState == I2pPublicProjectionPolicy.Summary(e.RobotsRequestOutcome, e.RobotsProxyClassification,
            e.RobotsObservation, e.HomepageRequestAttempted, e.HomepageStatusCode), "Summary disagrees with observations.");
        return e;
    }

    private static I2pProxyResponseClassification? StatusClass(int? status) => status == null ? null : status is 502 or 503 or 504
        ? I2pProxyResponseClassification.LikelyProxyFailure : I2pProxyResponseClassification.Ambiguous;
    private static string Host(string text)
    {
        Require(text.Length <= 253 && I2pSeed.NormalizeHostname(text) == text, "Hostname must already be normalized.");
        return text;
    }
    private static string B32(string text)
    {
        var target = new I2pProbeTarget.DiscoveredHost(text);
        Require(target.Hostname == text && target.HostForm == I2pDiscoveredHostForm.ClassicBase32, "Expected canonical B32 identity.");
        return text;
    }
    private static void Fields(JsonElement value, params string[] names)
    {
        Require(value.ValueKind == JsonValueKind.Object, "Expected an object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in value.EnumerateObject())
            Require(names.Contains(field.Name, StringComparer.Ordinal) && seen.Add(field.Name), "Unknown or duplicate public crawl property.");
        Require(seen.Count == names.Length, "Missing required public crawl property.");
    }
    private static JsonElement Array(JsonElement parent, string name, int max)
    {
        var value = parent.GetProperty(name);
        Require(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() <= max, "Invalid or oversized public array.");
        return value;
    }
    private static string Text(JsonElement parent, string name, int max)
    {
        var value = parent.GetProperty(name);
        Require(value.ValueKind == JsonValueKind.String, "Expected text.");
        var text = value.GetString()!;
        Require(text.Length <= max, "Public text exceeds field limit.");
        return text;
    }
    private static string? OptionalText(JsonElement parent, string name, int max) =>
        parent.GetProperty(name).ValueKind == JsonValueKind.Null ? null : Text(parent, name, max);
    private static int Integer(JsonElement parent, string name, int min, int max)
    {
        var value = parent.GetProperty(name);
        Require(value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number >= min && number <= max,
            "Invalid public integer.");
        return value.GetInt32();
    }
    private static int? Status(JsonElement parent, string name) =>
        parent.GetProperty(name).ValueKind == JsonValueKind.Null ? null : Integer(parent, name, 100, 999);
    private static bool Boolean(JsonElement parent, string name)
    {
        var value = parent.GetProperty(name);
        Require(value.ValueKind is JsonValueKind.True or JsonValueKind.False, "Expected boolean.");
        return value.GetBoolean();
    }
    private static bool? NullableBoolean(JsonElement parent, string name) =>
        parent.GetProperty(name).ValueKind == JsonValueKind.Null ? null : Boolean(parent, name);
    private static T? EnumValue<T>(JsonElement parent, string name, bool nullable = false) where T : struct, Enum
    {
        if (nullable && parent.GetProperty(name).ValueKind == JsonValueKind.Null) return null;
        var text = Text(parent, name, 64);
        Require(Enum.GetNames<T>().Contains(text, StringComparer.Ordinal), "Invalid public enum value.");
        return Enum.Parse<T>(text, false);
    }
    private static DateTimeOffset Timestamp(JsonElement parent, string name)
    {
        var text = Text(parent, name, 64);
        Require((text.EndsWith('Z') || text.EndsWith("+00:00", StringComparison.Ordinal)) &&
            parent.GetProperty(name).TryGetDateTimeOffset(out var value) && value.Offset == TimeSpan.Zero, "Expected explicit UTC timestamp.");
        return parent.GetProperty(name).GetDateTimeOffset();
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidDataException(message); }
}
