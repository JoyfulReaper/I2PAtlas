using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Text.Json;
using System.Text.Json.Nodes;
using I2PAtlas.Commands;
using I2PAtlas.I2pCrawling;
using I2PAtlas.I2pPublishing;
using I2PAtlas.I2pReporting;
using static I2PAtlas.Tests.CrawlProjectionTestData;

namespace I2PAtlas.Tests;

[TestClass]
public sealed class I2pCrawlProjectionTests
{
    [TestMethod]
    public async Task LocalProjectionUsesOnlyPublicAllowlistAndPreservesAlternateIdentitiesAndReferrers()
    {
        using var files = new TemporaryFiles();
        var raw = PrivateArtifact();
        var input = await WriteRawAsync(files, raw);
        var original = await File.ReadAllBytesAsync(input);
        var output = Path.Combine(Directory.CreateDirectory(Path.Combine(files.DirectoryPath, "published")).FullName, "crawl.json");
        using var network = new NetworkObserver();
        var projected = await new I2pCrawlProjectionService().ProjectAsync(input, output);
        var json = await File.ReadAllTextAsync(output);
        var reread = await I2pPublicCrawlReader.ReadAsync(output);
        Assert.AreEqual(JsonSerializer.Serialize(projected, I2pCrawlProjectionService.JsonOptions),
            JsonSerializer.Serialize(reread, I2pCrawlProjectionService.JsonOptions));
        Assert.AreEqual("i2patlas.public-crawl", projected.Schema);
        Assert.AreEqual(1, projected.Version);
        Assert.AreEqual(2, projected.KnownSeedCount);
        Assert.AreEqual(2, projected.DiscoveredHostCount);
        var known = projected.Entries.OfType<I2pPublicCrawlEntry.KnownSeed>().ToArray();
        Assert.IsTrue(known.All(k => k.Hostname == "a.i2p" && k.Depth == 0));
        Assert.AreNotEqual(known[0].DestinationHostname, known[1].DestinationHostname);
        CollectionAssert.AreEqual(raw.KnownSeedResults.Select(k => k.DestinationHostname).Order(StringComparer.Ordinal).ToArray(),
            known.Select(k => k.DestinationHostname).ToArray());
        var discovered = projected.Entries.OfType<I2pPublicCrawlEntry.DiscoveredHost>().ToArray();
        Assert.IsTrue(discovered.All(h => h.Depth == 1));
        Assert.AreEqual(I2pPublicCrawlHostForm.Friendly, discovered[0].HostForm);
        Assert.AreEqual(CrawlTestData.ClassicB32, discovered[1].Hostname);
        Assert.AreEqual(I2pPublicCrawlHostForm.ClassicBase32, discovered[1].HostForm);
        foreach (var host in discovered)
            Assert.HasCount(1, known.Where(k => k.Hostname == host.ReferrerHostname && k.DestinationHostname == host.ReferrerDestinationHostname).ToArray());
        var page = known.Single(k => k.Observation.Title != null).Observation;
        Assert.AreEqual("text/html", page.HomepageContentType);
        Assert.AreEqual("<b>literal</b>" + new string('x', 146), page.Title);
        CollectionAssert.AreEqual(new[] { "child.i2p", CrawlTestData.ClassicB32 }, page.DiscoveredI2pHostnames.ToArray());
        Assert.AreEqual(raw.CrawlStartedAtUtc, projected.CrawlStartedAtUtc);
        Assert.AreEqual(raw.CrawlCompletedAtUtc, projected.CrawlCompletedAtUtc);
        foreach (var destination in raw.KnownSeedResults.Select(k => k.Destination)) Assert.DoesNotContain(destination, json);
        foreach (var text in new[] { "127.0.0.2", "43891", "PRIVATE_CHARSET", "PRIVATE_PATH", "PRIVATE_QUERY", "PRIVATE_REDIRECT",
            "PRIVATE_CLEARNET", "UnexpectedProbeFailure", "partial request state", raw.Snapshot.FileSha256, raw.Snapshot.SourceSha256,
            "private-raw.json", "http://", "https://" }) Assert.DoesNotContain(text, json);
        var root = JsonNode.Parse(json)!;
        CollectionAssert.AreEquivalent(new[] { "schema", "version", "crawlStartedAtUtc", "crawlCompletedAtUtc", "knownSeedCount", "discoveredHostCount", "entries" },
            root.AsObject().Select(p => p.Key).ToArray());
        foreach (var host in root["entries"]!.AsArray().Where(e => e!["kind"]!.GetValue<string>() == "DiscoveredHost"))
            CollectionAssert.AreEquivalent(new[] { "kind", "hostname", "hostForm", "depth", "referrerHostname", "referrerDestinationHostname", "observation" },
                host!.AsObject().Select(p => p.Key).ToArray());
        Assert.IsEmpty(network.Activity);
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(input));
        Assert.IsEmpty(Directory.GetFiles(Path.GetDirectoryName(output)!, ".i2p-public-crawl-*.tmp"));
    }

    [TestMethod]
    public async Task SameInputProducesIdenticalBytesAndEmptyCrawlRoundTrips()
    {
        using var files = new TemporaryFiles();
        var input = await WriteRawAsync(files);
        var first = Path.Combine(files.DirectoryPath, "first.json");
        var second = Path.Combine(files.DirectoryPath, "second.json");
        await new I2pCrawlProjectionService().ProjectAsync(input, first);
        await new I2pCrawlProjectionService().ProjectAsync(input, second);
        CollectionAssert.AreEqual(await File.ReadAllBytesAsync(first), await File.ReadAllBytesAsync(second));
        var raw = PrivateArtifact() with { KnownSeedResults = [], DiscoveredHostResults = [], SnapshotSeedCount = 0,
            Discovery = new(0, 0, false) };
        raw = Refresh(raw) with { Metrics = I2pCrawlMetrics.Calculate([], 0, 0) };
        input = await WriteRawAsync(files, raw);
        await new I2pCrawlProjectionService().ProjectAsync(input, first);
        var empty = await I2pPublicCrawlReader.ReadAsync(first);
        Assert.IsEmpty(empty.Entries);
        Assert.AreEqual(0, empty.KnownSeedCount + empty.DiscoveredHostCount);
    }

    [TestMethod]
    [DataRow("invalid-raw")][DataRow("cancel-before")][DataRow("cancel-stage")][DataRow("cancel-replace")]
    [DataRow("io-stage")][DataRow("io-replace")][DataRow("invalid-stage")][DataRow("serialization-limit")]
    public async Task EveryFailureBeforeReplacementPreservesOldOutputAndCleansOwnedStage(string failure)
    {
        using var files = new TemporaryFiles();
        var input = await WriteRawAsync(files);
        var output = files.Write("public.json", "previous public artifact");
        using var cancel = new CancellationTokenSource();
        if (failure == "invalid-raw") File.WriteAllText(input, "PRIVATE_INVALID_INPUT");
        if (failure == "cancel-before") cancel.Cancel();
        var validated = false;
        var service = new I2pCrawlProjectionService(beforeValidate: staged =>
        {
            using (File.Open(staged, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } // Stage is closed.
            if (!OperatingSystem.IsWindows()) Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(staged));
            if (failure == "cancel-stage") cancel.Cancel();
            if (failure == "io-stage") throw new IOException("PRIVATE_IO_FAILURE");
            if (failure == "invalid-stage")
            {
                var node = JsonNode.Parse(File.ReadAllText(staged))!;
                node["entries"]![0]!["destination"] = "PRIVATE_BASE64";
                File.WriteAllText(staged, node.ToJsonString());
            }
        }, beforeReplace: _ =>
        {
            validated = true;
            if (failure == "cancel-replace") cancel.Cancel();
            if (failure == "io-replace") throw new IOException("PRIVATE_IO_FAILURE");
        }, outputByteLimit: failure == "serialization-limit" ? 100 : I2pPublicCrawlReader.MaxArtifactBytes);
        try
        {
            await service.ProjectAsync(input, output, cancel.Token);
            Assert.Fail("Injected failure must prevent replacement.");
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or OperationCanceledException) { }
        Assert.AreEqual(failure is "cancel-replace" or "io-replace", validated, "Malformed staged output must fail strict reading before replacement.");
        Assert.AreEqual("previous public artifact", await File.ReadAllTextAsync(output));
        Assert.IsEmpty(Directory.GetFiles(files.DirectoryPath, ".i2p-public-crawl-*.tmp"));
    }

    [TestMethod]
    public async Task CrawlArtifactsCannotEnterTheExistingScanProjectionOrViewer()
    {
        using var files = new TemporaryFiles();
        var raw = await WriteRawAsync(files);
        var projected = Path.Combine(files.DirectoryPath, "public-crawl.json");
        await new I2pCrawlProjectionService().ProjectAsync(raw, projected);
        var output = files.Write("old-output", "previous");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new I2pProjectionService().ProjectAsync(raw, output));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new I2pProjectionService().ProjectAsync(projected, output));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new I2pViewerGenerator().GenerateAsync(projected, output));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new I2pCrawlProjectionService().ProjectAsync(projected, output));
        Assert.AreEqual("previous", await File.ReadAllTextAsync(output));
    }

    [TestMethod]
    [DataRow("same")][DataRow("url")][DataRow("unc")][DataRow("device")][DataRow("input-url")]
    public async Task NonlocalAndOverwritingPathsAreRejected(string failure)
    {
        using var files = new TemporaryFiles();
        var input = await WriteRawAsync(files);
        var original = await File.ReadAllBytesAsync(input);
        var output = failure switch { "same" => input, "url" => "https://invalid.example/output.json", "unc" => @"\\invalid\share\crawl.json",
            "device" => @"\\?\C:\output.json", _ => Path.Combine(files.DirectoryPath, "out.json") };
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => new I2pCrawlProjectionService().ProjectAsync(failure == "input-url" ? "https://invalid.example/input" : input, output));
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(input));
    }

    internal sealed class NetworkObserver : EventListener
    {
        internal ConcurrentQueue<string> Activity { get; } = new();
        protected override void OnEventSourceCreated(EventSource source)
        { if (source.Name is "System.Net.Http" or "System.Net.Sockets" or "System.Net.NameResolution") EnableEvents(source, EventLevel.Informational); }
        protected override void OnEventWritten(EventWrittenEventArgs e)
        { if (e.EventName is "RequestStart" or "ConnectStart" or "ResolutionStart") Activity.Enqueue(e.EventSource.Name + ":" + e.EventName); }
    }
}
