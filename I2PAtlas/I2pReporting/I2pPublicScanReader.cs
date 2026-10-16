using System.Collections.Immutable;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using I2PAtlas.AddressBook;
using I2PAtlas.I2pProbing;
using I2PAtlas.I2pPublishing;

namespace I2PAtlas.I2pReporting;

/// <summary>Only public v1 input; never repairs ordering or consults private evidence.</summary>
public static class I2pPublicScanReader
{
    public const int MaxArtifactBytes = 128 * 1024 * 1024;
    public const int MaxEntries = 10_000;
    public const int MaxDiscoveredHosts = 250;

    public static async Task<I2pPublicScan> ReadAsync(string path, CancellationToken token = default)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        Require(file.Length <= MaxArtifactBytes, "Public artifact exceeds 128 MiB.");
        using var bytes = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = await file.ReadAsync(buffer, token)) != 0)
        {
            Require(bytes.Length + count <= MaxArtifactBytes, "Public artifact exceeds 128 MiB.");
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
            throw new InvalidDataException("Invalid public artifact JSON, encoding or field value.");
        }
    }

    private static I2pPublicScan Read(JsonElement root, CancellationToken token)
    {
        Fields(root, "schema", "version", "scanStartedAtUtc", "scanCompletedAtUtc", "seedCount", "uniqueHostnameCount", "entries");
        Require(Text(root, "schema", 64) == I2pPublicScan.CurrentSchema && Integer(root, "version", 1, 1) == 1,
            "Expected i2patlas.public-scan version 1.");
        var started = Timestamp(root, "scanStartedAtUtc");
        var completed = Timestamp(root, "scanCompletedAtUtc");
        Require(started <= completed, "Scan completion precedes scan start.");
        var array = Array(root, "entries", MaxEntries);
        Require(Integer(root, "seedCount", 0, MaxEntries) == array.GetArrayLength(), "Inconsistent public entry count.");
        var entries = ImmutableArray.CreateBuilder<I2pPublicEntry>(array.GetArrayLength());
        I2pPublicEntry? previous = null;
        foreach (var item in array.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            var entry = Entry(item);
            var order = previous == null ? -1 : string.CompareOrdinal(previous.Hostname, entry.Hostname);
            Require(previous == null || order < 0 || order == 0 && string.CompareOrdinal(previous.DestinationHostname, entry.DestinationHostname) < 0,
                "Entries must be strictly ordered by hostname and destinationHostname without duplicate identities.");
            entries.Add(entry);
            previous = entry;
        }
        var result = new I2pPublicScan(started, completed, entries.MoveToImmutable());
        Require(Integer(root, "uniqueHostnameCount", 0, MaxEntries) == result.UniqueHostnameCount, "Inconsistent friendly-hostname count.");
        foreach (var group in result.Entries.GroupBy(e => e.Hostname, StringComparer.Ordinal))
            Require(group.All(e => e.DestinationsForHostname == group.Count()), "Inconsistent destinationsForHostname count.");
        return result;
    }

    private static I2pPublicEntry Entry(JsonElement item)
    {
        Fields(item, "hostname", "destinationHostname", "destinationsForHostname", "summaryState",
            "robotsRequestOutcome", "robotsProxyClassification", "robotsObservation", "robotsStatusCode",
            "homepageRequestAttempted", "homepageRequestOutcome", "homepageProxyClassification", "homepageStatusCode",
            "homepageContentType", "title", "discoveredI2pHostnames", "linkExtractionIncomplete");
        var hostname = Host(Text(item, "hostname", 253));
        var destination = Text(item, "destinationHostname", 60);
        Require(destination.Length == 60 && destination.EndsWith(".b32.i2p", StringComparison.Ordinal) &&
            destination[..52].All(c => c is >= 'a' and <= 'z' or >= '2' and <= '7') && destination[51] is 'a' or 'q',
            "Invalid destination-derived Base32 identity.");
        var contentType = OptionalText(item, "homepageContentType", 80);
        Require(contentType == null || contentType.Length > 0 &&
            contentType.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '.' or '+') &&
            contentType == contentType.ToLowerInvariant() && MediaTypeHeaderValue.TryParse(contentType, out var media) &&
            media.MediaType == contentType && media.Parameters.Count == 0, "Invalid public content type.");
        var title = OptionalText(item, "title", 160);
        Require(title == null || title.Length > 0 && title.Trim() == title && title.EnumerateRunes().All(r =>
            Rune.GetUnicodeCategory(r) is not (UnicodeCategory.Control or UnicodeCategory.Format or
                UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)), "Invalid public title.");
        var hosts = ImmutableArray.CreateBuilder<string>();
        string? previous = null;
        foreach (var value in Array(item, "discoveredI2pHostnames", MaxDiscoveredHosts).EnumerateArray())
        {
            Require(value.ValueKind == JsonValueKind.String, "Expected discovered hostname text.");
            var host = Host(value.GetString()!);
            Require(previous == null || string.CompareOrdinal(previous, host) < 0, "Discovered hostnames must be sorted and unique.");
            hosts.Add(host);
            previous = host;
        }
        var entry = new I2pPublicEntry(hostname, destination, Integer(item, "destinationsForHostname", 1, MaxEntries),
            EnumValue<I2pPublicSummaryState>(item, "summaryState")!.Value,
            EnumValue<I2pRequestOutcome>(item, "robotsRequestOutcome", true),
            EnumValue<I2pProxyResponseClassification>(item, "robotsProxyClassification")!.Value,
            EnumValue<I2pRobotsObservation>(item, "robotsObservation")!.Value, Status(item, "robotsStatusCode"),
            NullableBoolean(item, "homepageRequestAttempted"), EnumValue<I2pRequestOutcome>(item, "homepageRequestOutcome", true),
            EnumValue<I2pProxyResponseClassification>(item, "homepageProxyClassification", true),
            Status(item, "homepageStatusCode"), contentType, title, hosts.ToImmutable(), Boolean(item, "linkExtractionIncomplete"));
        ValidateObservations(entry);
        return entry;
    }

    private static void ValidateObservations(I2pPublicEntry e)
    {
        Require((e.RobotsRequestOutcome == I2pRequestOutcome.ProxyHttpResponse) == e.RobotsStatusCode.HasValue &&
            (e.HomepageRequestOutcome == I2pRequestOutcome.ProxyHttpResponse) == e.HomepageStatusCode.HasValue,
            "Request outcome and HTTP status disagree.");
        Require(e.HomepageRequestOutcome.HasValue == (e.HomepageRequestAttempted == true) &&
            e.HomepageProxyClassification.HasValue == e.HomepageStatusCode.HasValue &&
            (e.HomepageContentType == null || e.HomepageStatusCode.HasValue), "Inconsistent homepage observation.");
        Require(e.HomepageRequestAttempted.HasValue == e.RobotsRequestOutcome.HasValue, "Inconsistent attempt state.");
        Require(e.RobotsStatusCode.HasValue || e.RobotsObservation == I2pRobotsObservation.NotObserved &&
            e.RobotsProxyClassification == I2pProxyResponseClassification.Ambiguous, "Invalid absent robots observation.");
        Require(e.HomepageRequestAttempted != true || e.RobotsObservation == I2pRobotsObservation.AllowsRoot &&
            e.RobotsProxyClassification == I2pProxyResponseClassification.LikelyOriginResponse, "Homepage attempt lacks recorded robots authorization.");
        Require(e.HomepageRequestAttempted == null || e.HomepageRequestAttempted ==
            (e.RobotsObservation == I2pRobotsObservation.AllowsRoot && e.RobotsProxyClassification == I2pProxyResponseClassification.LikelyOriginResponse),
            "Attempt state disagrees with recorded public robots observation.");
        Require(e.RobotsProxyClassification != I2pProxyResponseClassification.LikelyOriginResponse ||
            e.RobotsStatusCode == 200 && e.RobotsObservation is I2pRobotsObservation.AllowsRoot or I2pRobotsObservation.DisallowsRoot,
            "Inconsistent robots classification.");
        Require(e.RobotsObservation != I2pRobotsObservation.Redirect || e.RobotsStatusCode is >= 300 and <= 399,
            "Robots redirect lacks a redirect status.");
        Require(e.HomepageStatusCode == 200 || e.LinkExtractionIncomplete, "Absent homepage extraction must be incomplete.");
        Require(e.Title == null && e.DiscoveredI2pHostnames.Length == 0 || e.HomepageStatusCode == 200,
            "Page metadata requires an observed HTTP 200 homepage.");
        // Check the public summary contract, without reclassifying responses or reading private state.
        var expected = e.HomepageStatusCode.HasValue ? I2pPublicSummaryState.HomepageResponseObserved :
            e.HomepageRequestAttempted == true ? I2pPublicSummaryState.HomepageAttemptFailed :
            e.RobotsObservation == I2pRobotsObservation.DisallowsRoot ? I2pPublicSummaryState.RobotsDisallowed :
            e.RobotsProxyClassification == I2pProxyResponseClassification.LikelyProxyFailure ? I2pPublicSummaryState.ProxyFailureObserved :
            e.RobotsRequestOutcome == I2pRequestOutcome.TimedOut ? I2pPublicSummaryState.RequestTimedOut :
            e.RobotsRequestOutcome is I2pRequestOutcome.TransportFailure or I2pRequestOutcome.ProxyUnavailable ? I2pPublicSummaryState.TransportFailure :
            e.RobotsObservation == I2pRobotsObservation.Redirect ? I2pPublicSummaryState.RobotsRedirect : I2pPublicSummaryState.Ambiguous;
        Require(e.SummaryState == expected, "Summary disagrees with public observations.");
    }

    private static string Host(string value)
    {
        Require(value.Length <= 253 && I2pSeed.NormalizeHostname(value) == value, "Hostname must already be normalized.");
        return value;
    }
    private static void Fields(JsonElement value, params string[] names)
    {
        Require(value.ValueKind == JsonValueKind.Object, "Expected an object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            Require(names.Contains(property.Name, StringComparer.Ordinal) && seen.Add(property.Name), "Unexpected or duplicate public property.");
        Require(seen.Count == names.Length, "Missing required public property.");
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
        Require(value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number >= min && number <= max, "Invalid public integer.");
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
        return Enum.Parse<T>(text, ignoreCase: false);
    }
    private static DateTimeOffset Timestamp(JsonElement parent, string name)
    {
        var text = Text(parent, name, 64);
        Require((text.EndsWith('Z') || text.EndsWith("+00:00", StringComparison.Ordinal)) &&
            parent.GetProperty(name).TryGetDateTimeOffset(out var value) && value.Offset == TimeSpan.Zero, "Expected explicit UTC timestamp.");
        return parent.GetProperty(name).GetDateTimeOffset();
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
