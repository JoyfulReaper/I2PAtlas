using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using I2PAtlas.Commands;
using I2PAtlas.I2pCrawling;
using I2PAtlas.I2pPublishing;
using I2PAtlas.I2pReporting;
using I2PAtlas.I2pTransport;
using static I2PAtlas.Tests.CrawlTestData;

namespace I2PAtlas.Tests;

[TestClass]
public sealed class I2pMultiDepthCrawlTests
{
    [TestMethod]
    [DataRow(1)][DataRow(2)][DataRow(3)]
    public async Task FakeProxyGraphRespectsDepthBarriersSuppressionAndAggregateProgress(int maximumDepth)
    {
        using var files = new TemporaryFiles();
        var seed = Seed("private-parent.i2p");
        var unselected = Seed("unselected.i2p", 1);
        var finished = new ConcurrentDictionary<string, int>();
        string[] Suppressed() => [seed.Hostname, B32(seed), unselected.Hostname, B32(unselected)];
        var graph = new Dictionary<string, (int Depth, string[] Links)>
        {
            [B32(seed)] = (0, ["m-parent.i2p", "z-parent.i2p"]),
            ["m-parent.i2p"] = (1, ["b-child.i2p", "c-child.i2p", "z-parent.i2p"]),
            ["z-parent.i2p"] = (1, ["b-child.i2p", "c-child.i2p", "m-parent.i2p"]),
            ["b-child.i2p"] = (2, ["a-leaf.i2p", "m-parent.i2p"]),
            ["c-child.i2p"] = (2, ["a-leaf.i2p", "z-parent.i2p"]),
            ["a-leaf.i2p"] = (3, ["never-depth-four.i2p", "b-child.i2p"])
        };
        await using var proxy = new CrawlProxy(async (uri, token) =>
        {
            var node = graph[uri.Host];
            if (uri.AbsolutePath == "/robots.txt")
            {
                Assert.IsTrue(graph.Where(p => p.Value.Depth < node.Depth).All(p => finished.ContainsKey(p.Key)), "Complete breadth-first barrier required.");
                return Response(200, Allow, "text/plain");
            }
            await Task.Delay(uri.Host.StartsWith('m') ? 25 : 1, token);
            finished.AddOrUpdate(uri.Host, 1, (_, count) => count + 1);
            return Response(200, "<title>Graph</title>" + string.Concat(node.Links.Concat(Suppressed())
                .Select(h => $"<a href='http://{h}/private?q=secret'>child</a>")));
        });
        var events = new List<I2pCrawlProgress>();
        using var dns = new I2pCrawlTests.DnsObserver();
        var path = Path.Combine(files.DirectoryPath, "crawl.json");
        var result = await new I2pCrawlService(progress: events.Add).CrawlAsync(Snapshot(files, seed, unselected), path,
            proxy.Uri, new(maximumSeeds: 1, maximumDepth: maximumDepth));
        var expectedHosts = maximumDepth == 1 ? 2 : maximumDepth == 2 ? 4 : 5;
        Assert.AreEqual(expectedHosts, result.Discovery.AdmittedHostCount);
        Assert.AreEqual(2, result.Version);
        Assert.AreEqual(maximumDepth, result.Settings.MaximumDepth);
        Assert.IsTrue(finished.Values.All(c => c == 1));
        Assert.HasCount((1 + expectedHosts) * 2, proxy.Requests);
        Assert.IsLessThanOrEqualTo(2, proxy.MaximumActive);
        Assert.IsEmpty(dns.Hostnames);
        Assert.AreEqual(Json(result), Json(await I2pRawCrawlReader.ReadAsync(path)));
        var barriers = events.Where(e => e.WaveCompleted).ToArray();
        CollectionAssert.AreEqual(Enumerable.Range(0, maximumDepth + 1).ToArray(), barriers.Select(e => e.Depth).ToArray());
        Assert.AreEqual(1 + expectedHosts, barriers.Sum(e => e.CompletedTargets));
        Assert.AreEqual(result.Metrics.HomepageAttemptedCount, barriers[^1].HomepageAttemptedCount);
        Assert.AreEqual(result.Metrics.HomepageResponseCount, barriers[^1].HomepageResponseCount);
        Assert.AreEqual(0, barriers[^1].AdmittedHosts);
        foreach (var wave in events.GroupBy(e => e.Depth))
        {
            var completions = wave.Where(e => !e.WaveCompleted).ToArray();
            CollectionAssert.AreEqual(Enumerable.Range(0, completions.Length).ToArray(), completions.Select(e => e.CompletedTargets).ToArray());
            Assert.IsTrue(completions.All(e => e.TotalTargets == completions.Length - 1));
        }
        var diagnostics = JsonSerializer.Serialize(events);
        foreach (var secret in Suppressed().Append(seed.Destination).Append(proxy.Uri).Append("private?q=secret"))
            Assert.DoesNotContain(secret, diagnostics);
        foreach (var host in result.DiscoveredHostResults)
        {
            if (host.Depth == 1) Assert.AreEqual(I2pCrawlReferrer.FromSeed(seed), host.Referrer);
            else
            {
                Assert.IsNull(host.Referrer.DestinationHostname);
                Assert.AreEqual(I2pDiscoveredHostForm.Friendly, host.Referrer.HostForm);
                Assert.AreEqual(host.Depth == 2 ? "m-parent.i2p" : "b-child.i2p", host.Referrer.Hostname);
            }
        }
    }

    [TestMethod]
    [DataRow(false)][DataRow(true)]
    public async Task AdmissionIsDeterministicAndBudgetIsGlobalAcrossWaves(bool reverse)
    {
        using var files = new TemporaryFiles();
        var seen = new ConcurrentBag<string>();
        var service = new I2pCrawlService(probeOverride: async (target, token) =>
        {
            if (target is I2pProbeTarget.KnownSeed) return Observation("http://z.i2p/", "http://m.i2p/");
            var host = (I2pProbeTarget.DiscoveredHost)target;
            seen.Add(host.Hostname);
            await Task.Delay((host.Hostname == "m.i2p") == reverse ? 20 : 1, token);
            return host.Hostname == "a.i2p" ? Observation("http://c.i2p/") : Observation("http://b.i2p/", "http://a.i2p/");
        });
        var result = await service.CrawlAsync(Snapshot(files, Seed("seed.i2p")), Path.Combine(files.DirectoryPath, "raw.json"),
            "http://127.0.0.1:4444", new(maximumDiscoveredHosts: 3, maximumDepth: 3));
        CollectionAssert.AreEqual(new[] { "m.i2p", "z.i2p", "a.i2p" }, result.DiscoveredHostResults.Select(h => h.Hostname).ToArray());
        CollectionAssert.AreEqual(new[] { 1, 1, 2 }, result.DiscoveredHostResults.Select(h => h.Depth).ToArray());
        Assert.AreEqual("m.i2p", result.DiscoveredHostResults[2].Referrer.Hostname);
        Assert.IsTrue(result.Discovery.AdmissionLimitApplied);
        Assert.AreEqual(7L, result.Discovery.EligibleReferenceCount);
        Assert.HasCount(3, seen);
        Assert.AreEqual(4, result.Metrics.HomepageAttemptedCount);
        Assert.IsLessThanOrEqualTo(2, result.Metrics.MaximumActiveProbes);
    }

    [TestMethod]
    [DataRow(false)][DataRow(true)]
    public async Task FriendlyAndClassicParentsRemainHostOnlyAndDeepProbeFailuresAreIndependent(bool classic)
    {
        using var files = new TemporaryFiles();
        var parentHost = classic ? ClassicB32 : "parent.i2p";
        var input = Snapshot(files, Seed("seed.i2p"));
        var rawPath = Path.Combine(files.DirectoryPath, "raw.json");
        var result = await new I2pCrawlService(probeOverride: (target, _) =>
        {
            if (target is I2pProbeTarget.KnownSeed) return Task.FromResult(Observation($"http://{parentHost}/"));
            var host = (I2pProbeTarget.DiscoveredHost)target;
            if (host.Hostname == "failed.i2p") throw new IOException("PRIVATE_FAILURE_TEXT");
            return Task.FromResult(host.Hostname == parentHost ? Observation("http://failed.i2p/", "http://grand.i2p/") : Observation());
        }).CrawlAsync(input, rawPath, "http://127.0.0.1:4444", new(maximumDepth: 2));
        Assert.HasCount(3, result.DiscoveredHostResults);
        var child = result.DiscoveredHostResults.Single(h => h.Hostname == "grand.i2p");
        Assert.IsNull(child.Referrer.DestinationHostname);
        Assert.AreEqual(classic ? I2pDiscoveredHostForm.ClassicBase32 : I2pDiscoveredHostForm.Friendly, child.Referrer.HostForm);
        Assert.AreEqual(1, result.Metrics.UnexpectedProbeFailureCount);
        Assert.AreEqual(3, result.Metrics.HomepageResponseCount);
        var publicPath = Path.Combine(files.DirectoryPath, "public.json");
        await new I2pCrawlProjectionService().ProjectAsync(rawPath, publicPath);
        var publicResult = await I2pPublicCrawlReader.ReadAsync(publicPath);
        var publicChild = publicResult.Entries.OfType<I2pPublicCrawlEntry.DiscoveredHost>().Single(h => h.Hostname == "grand.i2p");
        Assert.AreEqual(parentHost, publicChild.ReferrerHostname);
        Assert.IsNull(publicChild.ReferrerDestinationHostname);
        Assert.AreEqual(classic ? I2pPublicCrawlHostForm.ClassicBase32 : I2pPublicCrawlHostForm.Friendly, publicChild.ReferrerHostForm);
        Assert.DoesNotContain("PRIVATE_FAILURE_TEXT", await File.ReadAllTextAsync(rawPath));
    }

    [TestMethod]
    [DataRow(2)][DataRow(3)]
    public async Task CancellationInDeeperWavePreservesOldArtifact(int cancelDepth)
    {
        using var files = new TemporaryFiles();
        using var stop = new CancellationTokenSource();
        var input = Snapshot(files, Seed("seed.i2p"));
        var output = files.Write("raw.json", "previous");
        var depth = 0;
        var service = new I2pCrawlService(workerStarted: d => depth = d, probeOverride: async (_, token) =>
        {
            if (depth == cancelDepth)
            {
                stop.Cancel();
                await Task.Delay(Timeout.Infinite, token);
            }
            return Observation($"http://depth-{depth + 1}.i2p/");
        });
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => service.CrawlAsync(input, output,
            "http://127.0.0.1:4444", new(maximumDepth: 3), stop.Token));
        Assert.AreEqual("previous", await File.ReadAllTextAsync(output));
        Assert.IsEmpty(Directory.GetFiles(files.DirectoryPath, ".i2p-crawl-*.tmp"));
    }

    [TestMethod]
    public async Task ProgressFailuresDoNotAbortOrPreventInstallation()
    {
        using var files = new TemporaryFiles();
        var calls = 0;
        var service = new I2pCrawlService(probeOverride: (_, _) => Task.FromResult(Observation()),
            progress: _ => { calls++; throw new IOException("PRIVATE_WRITER_ERROR"); });
        var output = Path.Combine(files.DirectoryPath, "raw.json");
        await service.CrawlAsync(Snapshot(files, Seed("seed.i2p")), output, "http://127.0.0.1:4444");
        Assert.AreEqual(1, calls);
        Assert.AreEqual(1, (await I2pRawCrawlReader.ReadAsync(output)).SelectedSeedCount);

        await using var proxy = new CrawlProxy((_, _) => Task.FromResult<byte[]?>(Response(504, "PRIVATE_BODY")));
        using var stdout = new StringWriter();
        Assert.AreEqual(0, await I2pCrawlCommand.ExecuteAsync(["i2p-crawl", Snapshot(files, Seed("private.i2p")), output,
            proxy.Uri, "--max-depth", "3"], stdout, new FailingWriter()));
        Assert.Contains("crawl complete", stdout.ToString());
    }

    [TestMethod]
    public void CommandProgressIsAggregateOnlyAndThrottledByCountOrElapsedCompletion()
    {
        using var output = new StringWriter();
        var writer = new I2pCrawlProgressWriter(output);
        for (var completed = 0; completed <= 60; completed++)
            writer.Report(new(2, completed, 60, completed, completed, completed * 100));
        writer.Report(new(2, 60, 60, 60, 60, 30000)); // elapsed-time threshold
        writer.Report(new(2, 60, 60, 60, 60, 30001, true, 75, 10));
        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.HasCount(5, lines);
        Assert.Contains("Depth 2: 25/60 complete | 25 homepage attempts | 25 responses", lines[1]);
        Assert.Contains("Depth 2 complete: 60 probed, 75 eligible references, 10 hosts admitted.", lines[^1]);
        foreach (var text in new[] { ".i2p", "http", "127.", "destination", "error", "redirect" })
            Assert.DoesNotContain(text, output.ToString());
        Assert.IsTrue(typeof(I2pCrawlProgress).GetProperties().All(p => p.PropertyType != typeof(string)));
    }

    [TestMethod]
    public async Task VersionTwoProjectionAndViewerPreserveImmediateHostOnlyChainAndVersionOneIds()
    {
        using var files = new TemporaryFiles();
        var raw = VersionTwoArtifact();
        var input = Path.Combine(files.DirectoryPath, "raw.json");
        await I2pRawCrawlFile.WriteAsync(input, raw, default);
        var output = Path.Combine(files.DirectoryPath, "public.json");
        var projected = await new I2pCrawlProjectionService().ProjectAsync(input, output);
        Assert.AreEqual(2, projected.Version);
        var loaded = await I2pPublicCrawlReader.ReadAsync(output);
        var grand = loaded.Entries.OfType<I2pPublicCrawlEntry.DiscoveredHost>().Single(h => h.Depth == 2);
        Assert.AreEqual("child.i2p", grand.ReferrerHostname);
        Assert.IsNull(grand.ReferrerDestinationHostname);
        Assert.AreEqual(I2pPublicCrawlHostForm.Friendly, grand.ReferrerHostForm);
        var text = await File.ReadAllTextAsync(output);
        foreach (var secret in raw.KnownSeedResults.Select(k => k.Destination).Append(raw.Proxy.Address).Append(raw.Snapshot.FileSha256))
            Assert.DoesNotContain(secret, text);
        var hostJson = JsonNode.Parse(text)!["entries"]!.AsArray().Single(e => e!["depth"]!.GetValue<int>() == 2)!;
        Assert.IsFalse(hostJson.AsObject().ContainsKey("referrerDestinationHostname"));
        var htmlPath = Path.Combine(files.DirectoryPath, "crawl.html");
        await new I2pCrawlViewerGenerator().GenerateAsync(output, htmlPath);
        var html = await File.ReadAllTextAsync(htmlPath);
        var parent = loaded.Entries.Single(e => e.Hostname == "child.i2p");
        Assert.Contains($"<a href=\"#{I2pCrawlViewerGenerator.Id(parent)}\">child.i2p</a>", html);
        Assert.Contains("Referrer host form (host-only identity)</dt><dd>Friendly", html);
        Assert.Contains("<dt>Depth</dt><dd>3</dd>", html);
        Assert.Contains("href=\"http://a-grand.i2p/\" target=\"_blank\" rel=\"noopener noreferrer\" data-crawl-open", html);
        var v1 = I2pPublicCrawlProjector.Project(Artifact(), default);
        foreach (var entry in v1.Entries)
            Assert.AreEqual(I2pCrawlViewerGenerator.Id(entry), I2pCrawlViewerGenerator.Id(loaded.Entries.Single(e => e.Hostname == entry.Hostname)));
        // Existing v1 writer/reader/projector/viewer suites continue to exercise their original exact shapes.
    }

    [TestMethod]
    [DataRow("missing-parent")][DataRow("same-depth")][DataRow("skip-depth")][DataRow("duplicate-host")]
    [DataRow("parent-form")][DataRow("invented-destination")][DataRow("missing-form")]
    [DataRow("v1")][DataRow("wrong-version")][DataRow("missing-reference")][DataRow("parent-representation")]
    public async Task BothStrictReadersRejectImpossibleVersionTwoChains(string mutation)
    {
        using var files = new TemporaryFiles();
        var raw = VersionTwoArtifact();
        var rawJson = JsonNode.Parse(Json(raw))!;
        var publicJson = JsonNode.Parse(JsonSerializer.Serialize(I2pPublicCrawlProjector.Project(raw, default), I2pCrawlProjectionService.JsonOptions))!;
        var rawHost = rawJson["discoveredHostResults"]![2]!;
        var publicHost = publicJson["entries"]![4]!;
        switch (mutation)
        {
            case "missing-parent": rawHost["referrer"]!["hostname"] = "absent.i2p"; publicHost["referrerHostname"] = "absent.i2p"; break;
            case "same-depth": rawHost["referrer"]!["hostname"] = "a-grand.i2p"; publicHost["referrerHostname"] = "a-grand.i2p"; break;
            case "skip-depth": rawHost["depth"] = 3; publicHost["depth"] = 3; break;
            case "duplicate-host": rawHost["hostname"] = "child.i2p"; publicHost["hostname"] = "child.i2p"; break;
            case "parent-form": rawHost["referrer"]!["hostForm"] = "ClassicBase32"; publicHost["referrerHostForm"] = "ClassicBase32"; break;
            case "invented-destination": rawHost["referrer"]!["destinationHostname"] = ClassicB32; publicHost["referrerDestinationHostname"] = ClassicB32; break;
            case "missing-form": rawHost["referrer"]!.AsObject().Remove("hostForm"); publicHost.AsObject().Remove("referrerHostForm"); break;
            case "v1": rawJson["version"] = 1; publicJson["version"] = 1; break;
            case "wrong-version": rawJson["version"] = 3; publicJson["version"] = 3; break;
            case "missing-reference":
                rawJson["discoveredHostResults"]![0]!["evidence"]!["homepageMetadata"]!["links"] = new JsonArray();
                publicJson["entries"]![2]!["observation"]!["discoveredI2pHostnames"] = new JsonArray(); break;
            case "parent-representation":
                rawJson["discoveredHostResults"]![0]!["evidence"]!["homepageResponse"]!["contentType"] = "text/plain";
                rawJson["discoveredHostResults"]![0]!["evidence"]!["homepageMetadata"]!["contentType"] = "text/plain";
                publicJson["entries"]![2]!["observation"]!["homepageContentType"] = "text/plain"; break;
        }
        var rawPath = files.Write("bad-raw.json", rawJson.ToJsonString());
        var publicPath = files.Write("bad-public.json", publicJson.ToJsonString());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => I2pRawCrawlReader.ReadAsync(rawPath));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => I2pPublicCrawlReader.ReadAsync(publicPath));
    }

    [TestMethod]
    [DataRow("missing-depth")][DataRow("limit")][DataRow("barrier")][DataRow("wave-active")]
    public async Task RawVersionTwoEnforcesConfiguredDepthAndEveryBarrier(string mutation)
    {
        using var files = new TemporaryFiles();
        var root = JsonNode.Parse(Json(VersionTwoArtifact()))!;
        switch (mutation)
        {
            case "missing-depth": root["settings"]!.AsObject().Remove("maximumDepth"); break;
            case "limit": root["settings"]!["maximumDepth"] = 1; break;
            case "barrier": root["discoveredHostResults"]![2]!["evidence"]!["startedAtUtc"] = Started.AddSeconds(3); break;
            case "wave-active": root["settings"]!["concurrency"] = 4; root["metrics"]!["maximumActiveProbes"] = 3; break;
        }
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => I2pRawCrawlReader.ReadAsync(files.Write("bad.json", root.ToJsonString())));
    }

    private static I2pRawCrawlArtifact VersionTwoArtifact()
    {
        var old = Artifact();
        var hosts = old.DiscoveredHostResults.Select((h, i) => h with { Evidence = I2pCrawlEvidence.FromObservation(
            i == 0 ? Observation("http://a-grand.i2p/") : Observation(), Started.AddSeconds(3), Started.AddSeconds(4), 100) }).ToImmutableArray();
        hosts = hosts.Add(new("a-grand.i2p", I2pDiscoveredHostForm.Friendly, 2, new("child.i2p", null, I2pDiscoveredHostForm.Friendly),
            I2pCrawlEvidence.FromObservation(Observation("http://deep.i2p/"), Started.AddSeconds(5), Started.AddSeconds(6), 100)));
        hosts = hosts.Add(new("deep.i2p", I2pDiscoveredHostForm.Friendly, 3, new("a-grand.i2p", null, I2pDiscoveredHostForm.Friendly),
            I2pCrawlEvidence.FromObservation(Observation(), Started.AddSeconds(7), Started.AddSeconds(8), 100)));
        return old with { Version = 2, CrawlCompletedAtUtc = Started.AddSeconds(9), Settings = new(2, 10000, 2, 4, 3),
            DiscoveredHostResults = hosts, Discovery = new(6, 4, false),
            Metrics = I2pCrawlMetrics.Calculate(old.KnownSeedResults.Select(k => k.Evidence).Concat(hosts.Select(h => h.Evidence)), 9000, 2) };
    }

    private sealed class FailingWriter : StringWriter
    {
        public override void WriteLine(string? value) => throw new IOException("PRIVATE_WRITER_ERROR");
    }
}
