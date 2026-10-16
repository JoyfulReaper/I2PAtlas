using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using I2PAtlas.I2pProbing;
using I2PAtlas.I2pTransport;
using I2PAtlas.Seeds;

namespace I2PAtlas.I2pCrawling;

internal sealed class I2pCrawlService
{
    internal const long MaxRetainedTextCharacters = 32L * 1024 * 1024;
    private readonly TimeProvider clock;
    private readonly Action<string>? beforeReplace;
    private readonly long textBudget;
    private readonly Func<I2pProbeTarget, CancellationToken, Task<I2pProbeObservation>>? probeOverride;
    private readonly Action<int>? workerStarted;

    internal I2pCrawlService(TimeProvider? timeProvider = null, Action<string>? beforeReplace = null,
        long textBudget = MaxRetainedTextCharacters,
        Func<I2pProbeTarget, CancellationToken, Task<I2pProbeObservation>>? probeOverride = null, Action<int>? workerStarted = null)
    {
        if (textBudget is < 1 or > MaxRetainedTextCharacters) throw new ArgumentOutOfRangeException(nameof(textBudget));
        clock = timeProvider ?? TimeProvider.System;
        this.beforeReplace = beforeReplace;
        this.textBudget = textBudget;
        this.probeOverride = probeOverride;
        this.workerStarted = workerStarted;
    }

    internal async Task<I2pRawCrawlArtifact> CrawlAsync(string snapshotPath, string outputPath, string proxyUri,
        I2pCrawlSettings? settings = null, CancellationToken cancellationToken = default)
    {
        settings ??= new();
        cancellationToken.ThrowIfCancellationRequested();
        var endpoint = I2pProxyEndpoint.Parse(proxyUri);
        var paths = I2pRawCrawlFile.ValidatePaths(snapshotPath, outputPath);
        var started = clock.GetUtcNow();
        var timestamp = clock.GetTimestamp();
        var loaded = await SeedSnapshotReader.ReadWithIdentityAsync(paths.Input, cancellationToken);
        var selected = loaded.Snapshot.Seeds.Take(settings.MaximumSeeds).Select(s => new I2pProbeTarget.KnownSeed(s)).ToImmutableArray();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var transport = new I2pHttpTransport(endpoint.Uri.AbsoluteUri, TimeSpan.FromMilliseconds(settings.RequestTimeoutMilliseconds));
        var probe = new I2pHttpProbe(transport);
        var active = 0;
        var maximumActive = 0;
        long retained = 0;

        var knownEvidence = await WaveAsync(selected, 0, (i, e) =>
            selected[i].Seed.Hostname.Length + (long)selected[i].Seed.Destination.Length + selected[i].DestinationHostname.Length + e.TextCharacters());
        var known = selected.Select((s, i) => new I2pCrawlKnownResult(s.Seed.Hostname, s.Seed.Destination,
            s.DestinationHostname, 0, knownEvidence[i])).ToImmutableArray();
        var parents = selected.Select((s, i) => (Target: s, Observation: knownEvidence[i].ToObservation()))
            .Where(p => p.Observation != null).Select(p => new I2pCrawlSeedObservation(p.Target, p.Observation!)).ToImmutableArray();
        // Exactly one frontier selection, after the complete depth-0 barrier.
        var frontier = I2pHostDiscovery.Select(loaded.Snapshot, parents, new(settings.MaximumDiscoveredHosts), stop.Token);
        var hosts = frontier.WorkItems;
        var hostEvidence = await WaveAsync(hosts.Select(w => w.Target).ToImmutableArray(), 1, (i, e) =>
            hosts[i].Target.Hostname.Length + (long)hosts[i].FirstReferrer.Hostname.Length + hosts[i].FirstReferrer.DestinationHostname.Length + e.TextCharacters());
        var discovered = hosts.Select((w, i) => new I2pCrawlHostResult(w.Target.Hostname, w.Target.HostForm, 1,
            w.FirstReferrer, hostEvidence[i])).ToImmutableArray();
        // Depth 1 is terminal. Its links remain private evidence and never enter selection.
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = loaded.Snapshot;
        var artifact = new I2pRawCrawlArtifact(I2pRawCrawlArtifact.CurrentSchema, 1, started.ToUniversalTime(), clock.GetUtcNow().ToUniversalTime(),
            new(snapshot.Schema, snapshot.Version, loaded.FileSha256, loaded.FileByteLength, snapshot.ImportedAtUtc,
                snapshot.Source.Sha256, snapshot.Source.ByteLength), new(endpoint.Address, endpoint.Port), settings, snapshot.Seeds.Length,
            new(frontier.EligibleUnknownReferenceCount, hosts.Length, frontier.AdmissionLimitApplied), known, discovered,
            I2pCrawlMetrics.Calculate(knownEvidence.Concat(hostEvidence), Elapsed(timestamp), maximumActive));
        await I2pRawCrawlFile.WriteAsync(paths.Output, artifact, cancellationToken, beforeReplace);
        return artifact;

        async Task<I2pCrawlEvidence[]> WaveAsync<T>(ImmutableArray<T> targets, int depth, Func<int, I2pCrawlEvidence, long> characters)
            where T : I2pProbeTarget
        {
            var results = new I2pCrawlEvidence[targets.Length];
            var next = -1;
            Exception? failure = null;
            var workers = Enumerable.Range(0, Math.Min(settings.Concurrency, targets.Length)).Select(_ => WorkerAsync()).ToArray();
            try { await Task.WhenAll(workers); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
                throw;
            }
            return results;

            async Task WorkerAsync()
            {
                try
                {
                    workerStarted?.Invoke(depth);
                    while (true)
                    {
                        stop.Token.ThrowIfCancellationRequested();
                        var index = Interlocked.Increment(ref next);
                        if (index >= targets.Length) return;
                        var current = Interlocked.Increment(ref active);
                        int old;
                        do { old = Volatile.Read(ref maximumActive); }
                        while (current > old && Interlocked.CompareExchange(ref maximumActive, current, old) != old);
                        try
                        {
                            var began = clock.GetUtcNow();
                            var beganTimestamp = clock.GetTimestamp();
                            I2pProbeObservation? observation = null;
                            try
                            {
                                observation = await (probeOverride == null ? probe.ProbeTargetAsync(targets[index], stop.Token)
                                    : probeOverride(targets[index], stop.Token));
                            }
                            catch (OperationCanceledException) when (stop.IsCancellationRequested) { throw; }
                            catch (Exception ex) when (ex is not OutOfMemoryException) { /* Sanitized unknown partial state below. */ }
                            var result = I2pCrawlEvidence.FromObservation(observation, began, clock.GetUtcNow(), Elapsed(beganTimestamp));
                            if (Interlocked.Add(ref retained, characters(index, result)) > textBudget)
                                throw new InvalidDataException("Crawl retained evidence exceeds its 32 Mi character budget.");
                            results[index] = result;
                        }
                        finally { Interlocked.Decrement(ref active); }
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    if (ex is not OperationCanceledException || !stop.IsCancellationRequested)
                        Interlocked.CompareExchange(ref failure, ex, null);
                    stop.Cancel();
                    throw;
                }
            }
        }
    }
    private double Elapsed(long timestamp) => Math.Round(Math.Max(0, clock.GetElapsedTime(timestamp).TotalMilliseconds), 3);
}
