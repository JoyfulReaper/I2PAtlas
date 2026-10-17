using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using I2PAtlas.I2pProbing;
using I2PAtlas.I2pTransport;

namespace I2PAtlas.I2pCrawling;

[JsonConverter(typeof(I2pCrawlSettingsJsonConverter))]
internal sealed class I2pCrawlSettings
{
    public int Concurrency { get; }
    public int RequestTimeoutMilliseconds { get; }
    public int MaximumSeeds { get; }
    public int MaximumDiscoveredHosts { get; }
    public int MaximumDepth { get; }
    internal bool HasExplicitDepth { get; }

    internal I2pCrawlSettings(int concurrency = 2, int requestTimeoutMilliseconds = 10000,
        int maximumSeeds = 10000, int maximumDiscoveredHosts = 100, int? maximumDepth = null)
    {
        if (concurrency is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(concurrency));
        if (requestTimeoutMilliseconds is < 1 or > 60000) throw new ArgumentOutOfRangeException(nameof(requestTimeoutMilliseconds));
        if (maximumSeeds is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(maximumSeeds));
        if (maximumDiscoveredHosts is < 0 or > 1000) throw new ArgumentOutOfRangeException(nameof(maximumDiscoveredHosts));
        if (maximumDepth is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(maximumDepth));
        Concurrency = concurrency;
        RequestTimeoutMilliseconds = requestTimeoutMilliseconds;
        MaximumSeeds = maximumSeeds;
        MaximumDiscoveredHosts = maximumDiscoveredHosts;
        MaximumDepth = maximumDepth ?? 1;
        HasExplicitDepth = maximumDepth.HasValue;
    }
}

// v1 retains its exact four-field shape. Only the strict reader deserializes settings.
internal sealed class I2pCrawlSettingsJsonConverter : JsonConverter<I2pCrawlSettings>
{
    public override I2pCrawlSettings Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
        throw new NotSupportedException("Use the strict raw crawl reader.");
    public override void Write(Utf8JsonWriter writer, I2pCrawlSettings value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("concurrency", value.Concurrency);
        writer.WriteNumber("requestTimeoutMilliseconds", value.RequestTimeoutMilliseconds);
        writer.WriteNumber("maximumSeeds", value.MaximumSeeds);
        writer.WriteNumber("maximumDiscoveredHosts", value.MaximumDiscoveredHosts);
        if (value.HasExplicitDepth) writer.WriteNumber("maximumDepth", value.MaximumDepth);
        writer.WriteEndObject();
    }
}

// All of these types are PRIVATE operational state. None is a public projection.
internal sealed record I2pCrawlSnapshot(string Schema, int Version, string FileSha256, long FileByteLength,
    DateTimeOffset ImportedAtUtc, string SourceSha256, long SourceByteLength);
internal sealed record I2pCrawlProxy(string Address, int Port);
internal sealed record I2pCrawlError(string Phase, string Category, string Detail);
internal sealed record I2pCrawlDiscovery(long EligibleReferenceCount, int AdmittedHostCount, bool AdmissionLimitApplied);
internal sealed record I2pCrawlKnownResult(string Hostname, string Destination, string DestinationHostname,
    int Depth, I2pCrawlEvidence Evidence);
internal sealed record I2pCrawlHostResult(string Hostname, I2pDiscoveredHostForm HostForm, int Depth,
    I2pCrawlReferrer Referrer, I2pCrawlEvidence Evidence);

internal sealed record I2pCrawlEvidence(
    DateTimeOffset StartedAtUtc, DateTimeOffset CompletedAtUtc, double DurationMilliseconds,
    I2pRequestOutcome? RobotsRequestOutcome, I2pOriginEvidence RobotsOriginEvidence,
    I2pProxyResponseClassification RobotsProxyClassification, I2pRobotsObservation RobotsObservation,
    I2pResponseObservation? RobotsResponse, bool? HomepageRequestAttempted, I2pHomepageDisposition? HomepageDisposition,
    I2pRequestOutcome? HomepageRequestOutcome, I2pOriginEvidence HomepageOriginEvidence,
    I2pProxyResponseClassification? HomepageProxyClassification, I2pResponseObservation? HomepageResponse,
    I2pPageMetadata? HomepageMetadata, ImmutableArray<I2pCrawlError> Errors)
{
    internal I2pProbeObservation? ToObservation() => RobotsRequestOutcome is not { } outcome ? null : new(
        outcome, RobotsOriginEvidence, RobotsProxyClassification, RobotsResponse, RobotsObservation, HomepageDisposition!.Value,
        HomepageRequestOutcome, HomepageResponse, HomepageMetadata, HomepageProxyClassification);

    internal static I2pCrawlEvidence FromObservation(I2pProbeObservation? observation,
        DateTimeOffset started, DateTimeOffset completed, double duration)
    {
        var errors = ImmutableArray.CreateBuilder<I2pCrawlError>();
        if (observation == null)
            errors.Add(new("probe", "UnexpectedProbeFailure", "Unexpected probe exception; partial request state is unknown."));
        else
        {
            Error("robots", observation.RequestOutcome, observation.RobotsResponse, observation.ProxyResponseClassification);
            if (observation.HomepageRequestOutcome is { } outcome)
                Error("homepage", outcome, observation.HomepageResponse, observation.HomepageProxyResponseClassification);
        }
        return new(started.ToUniversalTime(), completed.ToUniversalTime(), duration,
            observation?.RequestOutcome, observation?.OriginEvidence ?? I2pOriginEvidence.NotObserved,
            observation?.ProxyResponseClassification ?? I2pProxyResponseClassification.Ambiguous,
            observation?.Robots ?? I2pRobotsObservation.NotObserved, observation?.RobotsResponse,
            observation?.HomepageRequestAttempted, observation?.Homepage, observation?.HomepageRequestOutcome,
            observation?.HomepageOriginEvidence ?? I2pOriginEvidence.NotObserved, observation?.HomepageProxyResponseClassification,
            observation?.HomepageResponse, observation?.HomepageMetadata, errors.ToImmutable());

        void Error(string phase, I2pRequestOutcome outcome, I2pResponseObservation? response, I2pProxyResponseClassification? classification)
        {
            if (outcome != I2pRequestOutcome.ProxyHttpResponse)
                errors.Add(new(phase, outcome.ToString(), outcome == I2pRequestOutcome.TimedOut
                    ? "Request deadline expired." : "Request failed without a complete HTTP response."));
            else if (response?.StatusCode >= 400)
                errors.Add(new(phase, classification == I2pProxyResponseClassification.LikelyProxyFailure ? "LikelyProxyFailure" : "HttpErrorResponse",
                    $"HTTP status {response.StatusCode}; response authorship remains unproven."));
        }
    }

    internal long TextCharacters() => ResponseText(RobotsResponse) + ResponseText(HomepageResponse) +
        (HomepageMetadata?.Title?.Length ?? 0) + (HomepageMetadata?.ContentType?.Length ?? 0) +
        (HomepageMetadata?.Links.Sum(l => (long)l.Url.Length) ?? 0) + Errors.Sum(e => (long)e.Phase.Length + e.Category.Length + e.Detail.Length);
    private static long ResponseText(I2pResponseObservation? r) => (r?.ContentType?.Length ?? 0) + (r?.RedirectLocation?.Length ?? 0);
}

internal sealed record I2pCrawlMetrics(double TotalDurationMilliseconds,
    ImmutableSortedDictionary<string, int> RobotsRequestOutcomes, ImmutableSortedDictionary<string, int> RobotsProxyClassifications,
    ImmutableSortedDictionary<string, int> HomepageRequestOutcomes, ImmutableSortedDictionary<string, int> HomepageProxyClassifications,
    int HomepageAttemptedCount, int HomepageResponseCount, int UnexpectedProbeFailureCount, int MaximumActiveProbes)
{
    internal static I2pCrawlMetrics Calculate(IEnumerable<I2pCrawlEvidence> evidence, double duration, int maximumActive)
    {
        var results = evidence.ToArray();
        return new(duration, Counts(results.Select(r => r.RobotsRequestOutcome?.ToString() ?? "NotObserved")),
            Counts(results.Select(r => r.RobotsProxyClassification.ToString())),
            Counts(results.Select(r => r.HomepageRequestOutcome?.ToString() ?? (r.HomepageRequestAttempted == false ? "NotAttempted" : "NotObserved"))),
            Counts(results.Select(r => r.HomepageProxyClassification?.ToString() ?? "NotObserved")),
            results.Count(r => r.HomepageRequestAttempted == true), results.Count(r => r.HomepageResponse != null),
            results.Count(r => r.RobotsRequestOutcome == null), maximumActive);
    }
    private static ImmutableSortedDictionary<string, int> Counts(IEnumerable<string> values) => values.GroupBy(v => v, StringComparer.Ordinal)
        .ToImmutableSortedDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
}

internal sealed record I2pRawCrawlArtifact(string Schema, int Version, DateTimeOffset CrawlStartedAtUtc,
    DateTimeOffset CrawlCompletedAtUtc, I2pCrawlSnapshot Snapshot, I2pCrawlProxy Proxy, I2pCrawlSettings Settings,
    int SnapshotSeedCount, I2pCrawlDiscovery Discovery, ImmutableArray<I2pCrawlKnownResult> KnownSeedResults,
    ImmutableArray<I2pCrawlHostResult> DiscoveredHostResults, I2pCrawlMetrics Metrics)
{
    internal const string CurrentSchema = "i2patlas.raw-crawl";
    internal const int CurrentVersion = 2;
    public int SelectedSeedCount => KnownSeedResults.Length;
    public bool SeedLimitApplied => SelectedSeedCount < SnapshotSeedCount;
    internal IEnumerable<I2pCrawlEvidence> Evidence => KnownSeedResults.Select(r => r.Evidence).Concat(DiscoveredHostResults.Select(r => r.Evidence));
    internal long TextCharacters() => KnownSeedResults.Sum(r => (long)r.Hostname.Length + r.Destination.Length + r.DestinationHostname.Length + r.Evidence.TextCharacters()) +
        DiscoveredHostResults.Sum(r => (long)r.Hostname.Length + r.Referrer.Hostname.Length + (r.Referrer.DestinationHostname?.Length ?? 0) + r.Evidence.TextCharacters());
}
