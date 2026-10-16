using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using DN42Atlas.AddressBook;
using DN42Atlas.I2pProbing;
using DN42Atlas.I2pTransport;
using DN42Atlas.Seeds;

namespace DN42Atlas.I2pScanning;

/// <summary>Snapshot-only, bounded worker orchestration. No discovered URL can enter this queue.</summary>
public sealed class I2pScanService
{
    public const long MaxRetainedTextCharacters = 32L * 1024 * 1024;
    private readonly TimeProvider clock;
    private readonly Action<string>? beforeReplace;
    private readonly long textBudget;

    public I2pScanService(TimeProvider? timeProvider = null)
        : this(timeProvider, null, MaxRetainedTextCharacters) { }

    internal I2pScanService(TimeProvider? timeProvider, Action<string>? beforeReplace,
        long textBudget = MaxRetainedTextCharacters)
    {
        if (textBudget is < 1 or > MaxRetainedTextCharacters) throw new ArgumentOutOfRangeException(nameof(textBudget));
        clock = timeProvider ?? TimeProvider.System;
        this.beforeReplace = beforeReplace;
        this.textBudget = textBudget;
    }

    public async Task<I2pRawScanArtifact> ScanAsync(string snapshotPath, string outputPath, string proxyUri,
        I2pScanSettings? settings = null, CancellationToken cancellationToken = default)
    {
        settings ??= new I2pScanSettings();
        cancellationToken.ThrowIfCancellationRequested();
        var endpoint = I2pProxyEndpoint.Parse(proxyUri);
        var paths = I2pRawScanFile.ValidatePaths(snapshotPath, outputPath);
        var started = clock.GetUtcNow();
        var timestamp = clock.GetTimestamp();
        var loaded = await SeedSnapshotReader.ReadWithIdentityAsync(paths.Input, cancellationToken);
        var seeds = loaded.Snapshot.Seeds.Take(settings.MaximumSeeds).ToImmutableArray();
        var results = new I2pRawSeedResult[seeds.Length];
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var transport = new I2pHttpTransport(endpoint.Uri.AbsoluteUri, TimeSpan.FromMilliseconds(settings.RequestTimeoutMilliseconds));
        var probe = new I2pHttpProbe(transport);
        var next = -1;
        var active = 0;
        var maximumActive = 0;
        long retainedCharacters = 0;
        Exception? infrastructureFailure = null;

        // Exactly min(concurrency, count) workers, not one task per seed. Each seed
        // has a single stable result slot, independent of completion order.
        var workers = Enumerable.Range(0, Math.Min(settings.Concurrency, seeds.Length)).Select(_ => WorkerAsync()).ToArray();
        try { await Task.WhenAll(workers); }
        catch
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (infrastructureFailure != null) ExceptionDispatchInfo.Capture(infrastructureFailure).Throw();
            throw;
        }
        cancellationToken.ThrowIfCancellationRequested();
        var completed = clock.GetUtcNow();
        var immutable = results.ToImmutableArray();
        var metrics = Metrics(immutable, Elapsed(timestamp), maximumActive);
        var snapshot = loaded.Snapshot;
        var artifact = new I2pRawScanArtifact(I2pRawScanArtifact.CurrentSchema, I2pRawScanArtifact.CurrentVersion,
            started, completed,
            new(snapshot.Schema, snapshot.Version, loaded.FileSha256, loaded.FileByteLength, snapshot.ImportedAtUtc,
                snapshot.Source.Sha256, snapshot.Source.ByteLength),
            new(endpoint.Address, endpoint.Port), settings, snapshot.Seeds.Length, immutable, metrics);
        await I2pRawScanFile.WriteAsync(paths.Output, artifact, cancellationToken, beforeReplace);
        return artifact;

        async Task WorkerAsync()
        {
            try
            {
                while (true)
                {
                    stop.Token.ThrowIfCancellationRequested();
                    var index = Interlocked.Increment(ref next);
                    if (index >= seeds.Length) return;
                    var current = Interlocked.Increment(ref active);
                    int old;
                    do { old = Volatile.Read(ref maximumActive); }
                    while (current > old && Interlocked.CompareExchange(ref maximumActive, current, old) != old);
                    try
                    {
                        var seed = seeds[index];
                        var target = new I2pHttpRequestTarget(seed, $"http://{seed.Hostname}/robots.txt");
                        var seedStarted = clock.GetUtcNow();
                        var seedTimestamp = clock.GetTimestamp();
                        I2pProbeResult? observation = null;
                        var unexpected = false;
                        try { observation = await probe.ProbeAsync(seed, stop.Token); }
                        catch (OperationCanceledException) when (stop.IsCancellationRequested) { throw; }
                        catch (Exception ex) when (ex is not OutOfMemoryException)
                        {
                            // Isolate unexpected per-seed failures without persisting
                            // exception text, paths, stack traces or unknown partial state.
                            unexpected = true;
                        }
                        var result = Project(seed, target.DestinationHostname, observation, unexpected,
                            seedStarted, clock.GetUtcNow(), Elapsed(seedTimestamp));
                        if (Interlocked.Add(ref retainedCharacters, TextCharacters(result)) > textBudget)
                            throw new InvalidDataException("I2P scan exceeds its retained evidence budget; use a smaller --max-seeds.");
                        results[index] = result;
                    }
                    finally { Interlocked.Decrement(ref active); }
                }
            }
            catch (Exception ex)
            {
                if (ex is not OperationCanceledException || !stop.IsCancellationRequested)
                    Interlocked.CompareExchange(ref infrastructureFailure, ex, null);
                stop.Cancel();
                throw;
            }
        }
    }

    private double Elapsed(long timestamp) => Math.Round(Math.Max(0, clock.GetElapsedTime(timestamp).TotalMilliseconds), 3);

    private static I2pRawSeedResult Project(I2pSeed seed, string destinationHostname, I2pProbeResult? probe,
        bool unexpected, DateTimeOffset started, DateTimeOffset completed, double elapsed)
    {
        var errors = ImmutableArray.CreateBuilder<I2pScanError>();
        if (unexpected) errors.Add(new("probe", "UnexpectedProbeFailure", "Unexpected probe exception; partial request state is unknown."));
        if (probe != null)
        {
            Error("robots", probe.RequestOutcome, probe.RobotsResponse, probe.ProxyResponseClassification);
            if (probe.HomepageRequestOutcome is { } outcome)
                Error("homepage", outcome, probe.HomepageResponse, probe.HomepageProxyResponseClassification);
        }
        return new(seed.Hostname, seed.Destination, destinationHostname, started.ToUniversalTime(), completed.ToUniversalTime(), elapsed,
            probe?.RequestOutcome, probe?.OriginEvidence ?? I2pOriginEvidence.NotObserved,
            probe?.ProxyResponseClassification ?? I2pProxyResponseClassification.Ambiguous,
            probe?.Robots ?? I2pRobotsObservation.NotObserved, probe?.RobotsResponse,
            probe?.HomepageRequestAttempted, probe?.Homepage, probe?.HomepageRequestOutcome,
            probe?.HomepageOriginEvidence ?? I2pOriginEvidence.NotObserved,
            probe?.HomepageProxyResponseClassification, probe?.HomepageResponse, probe?.HomepageMetadata, errors.ToImmutable());

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

    private static I2pScanMetrics Metrics(ImmutableArray<I2pRawSeedResult> results, double duration, int maximumActive) => new(
        duration,
        Counts(results.Select(r => r.RobotsRequestOutcome?.ToString() ?? "NotObserved")),
        Counts(results.Select(r => r.RobotsProxyClassification.ToString())),
        Counts(results.Select(r => r.HomepageRequestOutcome?.ToString() ?? (r.HomepageRequestAttempted == false ? "NotAttempted" : "NotObserved"))),
        Counts(results.Select(r => r.HomepageProxyClassification?.ToString() ?? "NotObserved")),
        results.Count(r => r.HomepageRequestAttempted == true), results.Count(r => r.HomepageResponse != null),
        results.Count(r => r.RobotsRequestOutcome == null), maximumActive);

    private static ImmutableSortedDictionary<string, int> Counts(IEnumerable<string> values) => values.GroupBy(v => v, StringComparer.Ordinal)
        .ToImmutableSortedDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

    private static long TextCharacters(I2pRawSeedResult result) => result.Hostname.Length + result.Destination.Length + result.DestinationHostname.Length +
        ResponseText(result.RobotsResponse) + ResponseText(result.HomepageResponse) + (result.HomepageMetadata?.Title?.Length ?? 0) +
        (result.HomepageMetadata?.ContentType?.Length ?? 0) + (result.HomepageMetadata?.Links.Sum(link => (long)link.Url.Length) ?? 0) +
        result.Errors.Sum(error => (long)error.Phase.Length + error.Category.Length + error.Detail.Length);

    private static long ResponseText(I2pResponseObservation? response) => (response?.ContentType?.Length ?? 0) + (response?.RedirectLocation?.Length ?? 0);
}
