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
    private readonly Action<I2pCrawlProgress>? progress;

    internal I2pCrawlService(TimeProvider? timeProvider = null, Action<string>? beforeReplace = null,
        long textBudget = MaxRetainedTextCharacters,
        Func<I2pProbeTarget, CancellationToken, Task<I2pProbeObservation>>? probeOverride = null, Action<int>? workerStarted = null,
        Action<I2pCrawlProgress>? progress = null)
    {
        if (textBudget is < 1 or > MaxRetainedTextCharacters) throw new ArgumentOutOfRangeException(nameof(textBudget));
        clock = timeProvider ?? TimeProvider.System;
        this.beforeReplace = beforeReplace;
        this.textBudget = textBudget;
        this.probeOverride = probeOverride;
        this.workerStarted = workerStarted;
        this.progress = progress;
    }

    internal async Task<I2pRawCrawlArtifact> CrawlAsync(string snapshotPath, string outputPath, string proxyUri,
        I2pCrawlSettings? settings = null, CancellationToken cancellationToken = default)
    {
        settings ??= new();
        // New crawls always write v2, even at the compatible default depth of one.
        settings = new(settings.Concurrency, settings.RequestTimeoutMilliseconds, settings.MaximumSeeds,
            settings.MaximumDiscoveredHosts, settings.MaximumDepth);
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
        var progressGate = new object();
        var progressEnabled = true;
        var homepageAttempts = 0;
        var homepageResponses = 0;

        var knownEvidence = await WaveAsync(selected, 0, (i, e) =>
            selected[i].Seed.Hostname.Length + (long)selected[i].Seed.Destination.Length + selected[i].DestinationHostname.Length + e.TextCharacters());
        var known = selected.Select((s, i) => new I2pCrawlKnownResult(s.Seed.Hostname, s.Seed.Destination,
            s.DestinationHostname, 0, knownEvidence[i])).ToImmutableArray();
        var parents = selected.Select((s, i) => (Referrer: new I2pCrawlReferrer(s.Seed.Hostname, s.DestinationHostname),
            Observation: knownEvidence[i].ToObservation())).Where(p => p.Observation != null)
            .Select(p => new I2pCrawlParentObservation(p.Referrer, p.Observation!)).ToImmutableArray();
        var discovered = ImmutableArray.CreateBuilder<I2pCrawlHostResult>();
        var admitted = new HashSet<string>(StringComparer.Ordinal);
        long references = 0;
        var limited = false;
        var waveCount = known.Length;
        for (var depth = 0; ; depth++)
        {
            // Only a completed wave can populate the next frontier. The terminal
            // wave's links remain evidence; they never enter selection.
            var frontier = depth < settings.MaximumDepth
                ? I2pHostDiscovery.SelectWave(loaded.Snapshot, parents, admitted, depth + 1,
                    new(settings.MaximumDiscoveredHosts - admitted.Count), stop.Token)
                : new I2pCrawlFrontier([], 0, false);
            references += frontier.EligibleUnknownReferenceCount;
            limited |= frontier.AdmissionLimitApplied;
            Report(new(depth, waveCount, waveCount, homepageAttempts, homepageResponses, Elapsed(timestamp),
                true, frontier.EligibleUnknownReferenceCount, frontier.WorkItems.Length));
            if (frontier.WorkItems.Length == 0) break;
            var hosts = frontier.WorkItems;
            foreach (var host in hosts) admitted.Add(host.Target.Hostname);
            var evidence = await WaveAsync(hosts.Select(w => w.Target).ToImmutableArray(), depth + 1, (i, e) =>
                hosts[i].Target.Hostname.Length + (long)hosts[i].FirstReferrer.Hostname.Length +
                (hosts[i].FirstReferrer.DestinationHostname?.Length ?? 0) + e.TextCharacters());
            discovered.AddRange(hosts.Select((w, i) => new I2pCrawlHostResult(w.Target.Hostname, w.Target.HostForm, depth + 1,
                w.FirstReferrer, evidence[i])));
            parents = hosts.Select((w, i) => (Referrer: new I2pCrawlReferrer(w.Target.Hostname, null, w.Target.HostForm),
                Observation: evidence[i].ToObservation())).Where(p => p.Observation != null)
                .Select(p => new I2pCrawlParentObservation(p.Referrer, p.Observation!)).ToImmutableArray();
            waveCount = hosts.Length;
        }
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = loaded.Snapshot;
        var artifact = new I2pRawCrawlArtifact(I2pRawCrawlArtifact.CurrentSchema, 2, started.ToUniversalTime(), clock.GetUtcNow().ToUniversalTime(),
            new(snapshot.Schema, snapshot.Version, loaded.FileSha256, loaded.FileByteLength, snapshot.ImportedAtUtc,
                snapshot.Source.Sha256, snapshot.Source.ByteLength), new(endpoint.Address, endpoint.Port), settings, snapshot.Seeds.Length,
            new(references, admitted.Count, limited), known, discovered.ToImmutable(),
            I2pCrawlMetrics.Calculate(knownEvidence.Concat(discovered.Select(h => h.Evidence)), Elapsed(timestamp), maximumActive));
        await I2pRawCrawlFile.WriteAsync(paths.Output, artifact, cancellationToken, beforeReplace);
        return artifact;

        async Task<I2pCrawlEvidence[]> WaveAsync<T>(ImmutableArray<T> targets, int depth, Func<int, I2pCrawlEvidence, long> characters)
            where T : I2pProbeTarget
        {
            var results = new I2pCrawlEvidence[targets.Length];
            var next = -1;
            var completed = 0;
            Report(new(depth, 0, targets.Length, homepageAttempts, homepageResponses, Elapsed(timestamp)));
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
                            lock (progressGate)
                            {
                                if (result.HomepageRequestAttempted == true) homepageAttempts++;
                                if (result.HomepageResponse != null) homepageResponses++;
                                Report(new(depth, ++completed, targets.Length, homepageAttempts, homepageResponses, Elapsed(timestamp)));
                            }
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

        void Report(I2pCrawlProgress value)
        {
            lock (progressGate)
            {
                if (!progressEnabled || progress == null) return;
                try { progress(value); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                { progressEnabled = false; } // Diagnostics must never invalidate a crawl.
            }
        }
    }
    private double Elapsed(long timestamp) => Math.Round(Math.Max(0, clock.GetElapsedTime(timestamp).TotalMilliseconds), 3);
}
