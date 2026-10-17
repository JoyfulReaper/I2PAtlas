using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Security.Cryptography;
using System.Text.Json;
using I2PAtlas.I2pCrawling;
using I2PAtlas.I2pProbing;
using I2PAtlas.I2pTransport;
using static I2PAtlas.Tests.CrawlTestData;

namespace I2PAtlas.Tests;

[TestClass]
public sealed class I2pCrawlTests
{
    [TestMethod]
    [DataRow(false, 1)][DataRow(true, 1)][DataRow(false, 2)][DataRow(true, 2)]
    public async Task OneHopUsesBothTargetKindsFullSnapshotSuppressionAndTerminalDepthOne(bool reverseCompletion, int maximumHosts)
    {
        using var files = new TemporaryFiles();
        var a = Seed("a.i2p", 0);
        var alternate = Seed("a.i2p", 1);
        var unselected = Seed("b.i2p", 2);
        var snapshot = Snapshot(files, unselected, alternate, a);
        var original = await File.ReadAllBytesAsync(snapshot);
        var known = new[] { B32(a), B32(alternate) };
        var firstParent = known.Order(StringComparer.Ordinal).First();
        var knownDone = new ConcurrentDictionary<string, bool>();
        await using var proxy = new CrawlProxy(async (uri, token) =>
        {
            if (known.Contains(uri.Host))
            {
                if (uri.AbsolutePath == "/robots.txt") return Response(200, Allow, "text/plain");
                await Task.Delay((uri.Host == firstParent) == reverseCompletion ? 40 : 5, token);
                knownDone[uri.Host] = true;
                return Response(200, $"<a href='http://child.i2p/PRIVATE_PATH?q=PRIVATE_QUERY#x'>child</a>" +
                    $"<a href='http://{ClassicB32}/ignored'>b32</a><a href='http://b.i2p/'>known</a>" +
                    $"<a href='http://{B32(unselected)}/'>known b32</a><a href='http://a.i2p/'>self</a>");
            }
            Assert.HasCount(2, knownDone, "Depth 1 cannot start before both depth-0 homepages complete.");
            Assert.IsTrue(uri.Host == "child.i2p" || uri.Host == ClassicB32);
            return uri.AbsolutePath == "/robots.txt" ? Response(200, Allow, "text/plain")
                : Response(200, "<title>Terminal</title><a href='http://depth-two.i2p/private?q=secret'>inert</a><a href='http://a.i2p/'>cycle</a>");
        });
        using var dns = new DnsObserver();
        var output = Path.Combine(files.DirectoryPath, "crawl.json");
        var artifact = await new I2pCrawlService().CrawlAsync(snapshot, output, proxy.Uri, new(maximumSeeds: 2, maximumDiscoveredHosts: maximumHosts));
        Assert.AreEqual(3, artifact.SnapshotSeedCount);
        Assert.AreEqual(2, artifact.SelectedSeedCount);
        Assert.IsTrue(artifact.SeedLimitApplied);
        Assert.AreEqual(maximumHosts, artifact.Discovery.AdmittedHostCount);
        Assert.AreEqual(4L, artifact.Discovery.EligibleReferenceCount);
        Assert.AreEqual(maximumHosts == 1, artifact.Discovery.AdmissionLimitApplied);
        Assert.AreEqual(firstParent, artifact.DiscoveredHostResults[0].Referrer.DestinationHostname);
        Assert.IsTrue(artifact.DiscoveredHostResults.All(r => r.Referrer.Hostname == "a.i2p" && r.Referrer.DestinationHostname == firstParent));
        Assert.IsTrue(artifact.KnownSeedResults.All(r => r.Depth == 0 && r.Hostname == "a.i2p"));
        Assert.IsTrue(artifact.DiscoveredHostResults.All(r => r.Depth == 1 && r.Evidence.HomepageMetadata!.Links.Any(l => l.Url.Contains("depth-two.i2p"))));
        Assert.AreEqual(I2pDiscoveredHostForm.Friendly, artifact.DiscoveredHostResults[0].HostForm);
        if (maximumHosts == 2) Assert.AreEqual(I2pDiscoveredHostForm.ClassicBase32, artifact.DiscoveredHostResults[1].HostForm);
        CollectionAssert.AreEqual(new[] { a, alternate }.OrderBy(s => s.Destination, StringComparer.Ordinal).Select(s => s.Destination).ToArray(),
            artifact.KnownSeedResults.Select(r => r.Destination).ToArray());
        Assert.AreEqual(2 + maximumHosts, artifact.Metrics.HomepageAttemptedCount);
        Assert.AreEqual(2 + maximumHosts, artifact.Metrics.HomepageResponseCount);
        Assert.AreEqual(2 + maximumHosts, artifact.Metrics.RobotsRequestOutcomes["ProxyHttpResponse"]);
        Assert.IsLessThanOrEqualTo(2, artifact.Metrics.MaximumActiveProbes);
        Assert.IsLessThanOrEqualTo(2, proxy.MaximumActive);
        Assert.HasCount((2 + maximumHosts) * 2, proxy.Requests);
        foreach (var wire in proxy.Requests)
        {
            var uri = new Uri(wire[0].Split(' ')[1]);
            Assert.IsTrue(known.Contains(uri.Host) || uri.Host == "child.i2p" || uri.Host == ClassicB32);
            Assert.IsTrue(uri.AbsolutePath is "/" or "/robots.txt");
            Assert.AreEqual("", uri.Query + uri.Fragment);
            Assert.Contains($"Host: {uri.Host}", wire);
            Assert.Contains("User-Agent: I2PAtlas/0.1", wire);
            Assert.IsTrue(wire[0].EndsWith(" HTTP/1.1", StringComparison.Ordinal));
        }
        Assert.IsEmpty(dns.Hostnames);
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(original)), artifact.Snapshot.FileSha256);
        Assert.AreEqual((long)original.Length, artifact.Snapshot.FileByteLength);
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(snapshot));
        var reread = await I2pRawCrawlReader.ReadAsync(output);
        Assert.AreEqual(Json(artifact), Json(reread));
        Assert.IsEmpty(Directory.GetFiles(files.DirectoryPath, ".i2p-crawl-*.tmp"));
    }

    [TestMethod]
    [DataRow(0)][DataRow(1)]
    public async Task ZeroDiscoveryAndEmptySnapshotMakeNoExtraRequests(int seedCount)
    {
        using var files = new TemporaryFiles();
        var seeds = Enumerable.Range(0, seedCount).Select(i => Seed($"a{i}.i2p", i)).ToArray();
        var input = Snapshot(files, seeds);
        await using var proxy = new CrawlProxy((uri, _) => Task.FromResult<byte[]?>(uri.AbsolutePath == "/robots.txt"
            ? Response(200, Allow, "text/plain") : Response(200, "<a href='http://unknown.i2p/'>inert</a>")));
        var result = await new I2pCrawlService().CrawlAsync(input, Path.Combine(files.DirectoryPath, "crawl.json"), proxy.Uri,
            new(maximumDiscoveredHosts: 0));
        Assert.HasCount(seedCount * 2, proxy.Requests);
        Assert.IsEmpty(result.DiscoveredHostResults);
        Assert.AreEqual((long)seedCount, result.Discovery.EligibleReferenceCount);
        Assert.AreEqual(seedCount != 0, result.Discovery.AdmissionLimitApplied);
        Assert.AreEqual(seedCount == 0 ? 0 : 1, result.Metrics.MaximumActiveProbes);
    }

    [TestMethod]
    [DataRow(1)][DataRow(4)]
    public async Task FixedWorkersBoundBothWavesAndAdmitAtMostOneThousand(int concurrency)
    {
        using var files = new TemporaryFiles();
        var seeds = Enumerable.Range(0, 5).Select(i => Seed($"parent{i}.i2p", i)).ToArray();
        var started = new ConcurrentDictionary<int, int>();
        var input = Snapshot(files, seeds);
        var service = new I2pCrawlService(probeOverride: async (target, token) =>
        {
            await Task.Delay(1, token);
            if (target is I2pProbeTarget.KnownSeed known)
            {
                var index = Array.FindIndex(seeds, s => s.Hostname == known.Seed.Hostname);
                return Observation(Enumerable.Range(index * 250, 250).Select(i => $"http://h{i:D4}.i2p/private?q=secret").ToArray());
            }
            return Observation("http://terminal.i2p/");
        }, workerStarted: depth => started.AddOrUpdate(depth, 1, (_, count) => count + 1));
        var result = await service.CrawlAsync(input, Path.Combine(files.DirectoryPath, "crawl.json"), "http://127.0.0.1:4444",
            new(concurrency, maximumDiscoveredHosts: 1000));
        Assert.AreEqual(concurrency, started[0]);
        Assert.AreEqual(concurrency, started[1]);
        Assert.AreEqual(concurrency, result.Metrics.MaximumActiveProbes);
        Assert.HasCount(1000, result.DiscoveredHostResults);
        Assert.IsTrue(result.Discovery.AdmissionLimitApplied);
        Assert.AreEqual(1250L, result.Discovery.EligibleReferenceCount);
        CollectionAssert.AreEqual(Enumerable.Range(0, 1000).Select(i => $"h{i:D4}.i2p").ToArray(),
            result.DiscoveredHostResults.Select(r => r.Hostname).ToArray());
    }

    [TestMethod]
    public async Task SnapshotIsReadOnceAndNotReopenedBetweenWaves()
    {
        using var files = new TemporaryFiles();
        var input = Snapshot(files, Seed("a.i2p"));
        var bytes = await File.ReadAllBytesAsync(input);
        var service = new I2pCrawlService(probeOverride: (_, _) => Task.FromResult(Observation("http://child.i2p/")),
            workerStarted: depth => { if (depth == 1) File.Delete(input); });
        var result = await service.CrawlAsync(input, Path.Combine(files.DirectoryPath, "crawl.json"), "http://127.0.0.1:4444");
        Assert.HasCount(1, result.DiscoveredHostResults);
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(bytes)), result.Snapshot.FileSha256);
    }

    [TestMethod]
    public async Task TimeoutIsAnObservationAndDoesNotAbortSiblingTargets()
    {
        using var files = new TemporaryFiles();
        var a = Seed("a.i2p");
        var b = Seed("b.i2p", 1);
        await using var proxy = new CrawlProxy((uri, _) => Task.FromResult<byte[]?>(uri.Host == B32(a) ? null : Response(504, "proxy failure")));
        var result = await new I2pCrawlService().CrawlAsync(Snapshot(files, a, b), Path.Combine(files.DirectoryPath, "crawl.json"), proxy.Uri,
            new(requestTimeoutMilliseconds: 500));
        Assert.AreEqual(I2pRequestOutcome.TimedOut, result.KnownSeedResults[0].Evidence.RobotsRequestOutcome);
        Assert.AreEqual(I2pProxyResponseClassification.LikelyProxyFailure, result.KnownSeedResults[1].Evidence.RobotsProxyClassification);
        Assert.AreEqual(1, result.Metrics.RobotsRequestOutcomes["TimedOut"]);
        Assert.AreEqual(1, result.Metrics.RobotsRequestOutcomes["ProxyHttpResponse"]);
        Assert.IsEmpty(result.DiscoveredHostResults);
    }

    [TestMethod]
    [DataRow(false)][DataRow(true)]
    public async Task UnexpectedTargetExceptionRetainsOnlySanitizedUnknownPartialState(bool discovered)
    {
        using var files = new TemporaryFiles();
        var input = Snapshot(files, Seed("a.i2p"), Seed("b.i2p", 1));
        var service = new I2pCrawlService(probeOverride: (target, _) =>
        {
            if (discovered ? target is I2pProbeTarget.DiscoveredHost : target is I2pProbeTarget.KnownSeed s && s.Seed.Hostname == "a.i2p")
                throw new InvalidOperationException("PRIVATE_EXCEPTION_PATH_STACK");
            return Task.FromResult(Observation("http://child.i2p/"));
        });
        var output = Path.Combine(files.DirectoryPath, "crawl.json");
        var result = await service.CrawlAsync(input, output, "http://127.0.0.1:4444");
        Assert.AreEqual(1, result.Metrics.UnexpectedProbeFailureCount);
        var unknown = result.Evidence.Single(e => e.RobotsRequestOutcome == null);
        Assert.IsNull(unknown.HomepageRequestAttempted);
        Assert.AreEqual("UnexpectedProbeFailure", unknown.Errors.Single().Category);
        Assert.DoesNotContain("PRIVATE_EXCEPTION", await File.ReadAllTextAsync(output));
    }

    [TestMethod]
    [DataRow(false)][DataRow(true)]
    public async Task CancellationInEitherWavePreservesPreviousArtifact(bool depthOne)
    {
        using var files = new TemporaryFiles();
        var input = Snapshot(files, Seed("a.i2p"));
        var output = files.Write("crawl.json", "previous");
        using var cancel = new CancellationTokenSource();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var proxy = new CrawlProxy((uri, _) =>
        {
            if (!depthOne || uri.Host == "child.i2p") { received.TrySetResult(); return Task.FromResult<byte[]?>(null); }
            return Task.FromResult<byte[]?>(uri.AbsolutePath == "/robots.txt" ? Response(200, Allow, "text/plain")
                : Response(200, "<a href='http://child.i2p/'>child</a>"));
        });
        var pending = new I2pCrawlService().CrawlAsync(input, output, proxy.Uri, cancellationToken: cancel.Token);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        Assert.AreEqual("previous", await File.ReadAllTextAsync(output));
        Assert.IsEmpty(Directory.GetFiles(files.DirectoryPath, ".i2p-crawl-*.tmp"));
    }

    [TestMethod]
    public async Task InfrastructureFailureCancelsSiblingsAndPreservesOutput()
    {
        using var files = new TemporaryFiles();
        var input = Snapshot(files, Seed("a.i2p"), Seed("b.i2p", 1));
        var output = files.Write("crawl.json", "previous");
        var workers = 0;
        var siblingCancelled = false;
        var service = new I2pCrawlService(workerStarted: _ => { if (Interlocked.Increment(ref workers) == 2) throw new IOException("infrastructure"); },
            probeOverride: async (_, token) =>
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) { siblingCancelled = true; throw; }
                return Observation();
            });
        await Assert.ThrowsExactlyAsync<IOException>(() => service.CrawlAsync(input, output, "http://127.0.0.1:4444"));
        Assert.IsTrue(siblingCancelled);
        Assert.AreEqual("previous", await File.ReadAllTextAsync(output));
    }

    [TestMethod]
    public async Task ActualThirtyTwoMiCharacterBudgetFailsWholeRunWithoutTruncatingResults()
    {
        using var files = new TemporaryFiles();
        var input = Snapshot(files, Enumerable.Range(0, 40).Select(i => Seed($"h{i:D2}.i2p", i)).ToArray());
        var output = files.Write("crawl.json", "previous");
        var observation = Observation(Enumerable.Range(0, 250).Select(i => ($"http://u{i:D3}.i2p/").PadRight(4096, 'x')).ToArray());
        var service = new I2pCrawlService(probeOverride: (_, _) => Task.FromResult(observation));
        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.CrawlAsync(input, output, "http://127.0.0.1:4444"));
        Assert.Contains("budget", error.Message);
        Assert.AreEqual("previous", await File.ReadAllTextAsync(output));
        Assert.IsEmpty(Directory.GetFiles(files.DirectoryPath, ".i2p-crawl-*.tmp"));
    }

    [TestMethod]
    public async Task OutOfMemoryIsNotConvertedIntoATargetObservation()
    {
        using var files = new TemporaryFiles();
        var input = Snapshot(files, Seed("a.i2p"));
        var output = files.Write("crawl.json", "previous");
        await Assert.ThrowsExactlyAsync<OutOfMemoryException>(() => new I2pCrawlService(probeOverride: (_, _) => throw new OutOfMemoryException())
            .CrawlAsync(input, output, "http://127.0.0.1:4444"));
        Assert.AreEqual("previous", await File.ReadAllTextAsync(output));
    }

    [TestMethod]
    [DataRow("html", true)][DataRow("xhtml", true)][DataRow("plain", false)]
    [DataRow("incomplete", false)][DataRow("truncated", false)][DataRow("missing", false)][DataRow("404", false)]
    public async Task OnlyEligibleCompleteHtmlParentsContributeToTheFrontier(string representation, bool eligible)
    {
        using var files = new TemporaryFiles();
        var observation = Observation("http://child.i2p/original-path?q=private");
        var type = representation switch { "xhtml" => "application/xhtml+xml; charset=utf-8", "plain" => "text/plain", _ => "text/html" };
        observation = observation with
        {
            HomepageResponse = observation.HomepageResponse! with { ContentType = type, StatusCode = representation == "404" ? 404 : 200 },
            HomepageMetadata = representation is "missing" or "404" ? null : observation.HomepageMetadata! with
            {
                ContentType = type, BodyTruncated = representation == "truncated",
                ExtractionIncomplete = representation is "incomplete" or "truncated"
            }
        };
        var targetCount = 0;
        var service = new I2pCrawlService(probeOverride: (target, _) =>
        {
            targetCount++;
            return Task.FromResult(target is I2pProbeTarget.KnownSeed ? observation : Observation());
        });
        var result = await service.CrawlAsync(Snapshot(files, Seed("a.i2p")), Path.Combine(files.DirectoryPath, "crawl.json"), "http://127.0.0.1:4444");
        Assert.AreEqual(eligible ? 2 : 1, targetCount);
        Assert.AreEqual(eligible ? 1L : 0L, result.Discovery.EligibleReferenceCount);
    }

    internal sealed class DnsObserver : EventListener
    {
        internal ConcurrentQueue<string> Hostnames { get; } = new();
        protected override void OnEventSourceCreated(EventSource source)
        { if (source.Name == "System.Net.NameResolution") EnableEvents(source, EventLevel.Verbose); }
        protected override void OnEventWritten(EventWrittenEventArgs data)
        { if (data.EventName == "ResolutionStart" && data.Payload?.FirstOrDefault() is string name) Hostnames.Enqueue(name); }
    }
}
