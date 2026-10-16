using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DN42Atlas.AddressBook;
using DN42Atlas.Commands;
using DN42Atlas.I2pProbing;
using DN42Atlas.I2pScanning;
using DN42Atlas.I2pTransport;
using DN42Atlas.Seeds;

namespace DN42Atlas.Tests;

[TestClass]
public sealed class I2pScanTests
{
    [TestMethod]
    public async Task MultiSeedScanPreservesEvidenceIdentityOrderingMetricsAndPrivateBoundary()
    {
        using var files = new TestFiles();
        var input = Snapshot(files, 3);
        var bytes = await File.ReadAllBytesAsync(input);
        var output = Path.Combine(files.DirectoryPath, "scan.json");
        var hosts = Seeds(3).ToDictionary(s => Target(s).DestinationHostname, s => s.Hostname);
        const string html = "<title>Example &amp; site</title><a href='http://discovered.i2p/path'>I2P</a><a href='https://example.com/'>external</a>";
        await using var proxy = new ScanProxy((uri, _) => Task.FromResult<byte[]?>(
            hosts[uri.Host] switch
            {
                "host0.i2p" => uri.AbsolutePath == "/robots.txt" ? Response(200, "User-agent: *\nAllow: /", "text/plain") : Response(200, html, "text/html"),
                "host1.i2p" => Response(403, "proxy denied", "text/html"),
                _ => Response(504, "unreachable", "text/html")
            }));
        using var dns = new NameResolutionObserver();
        var scan = await new I2pScanService().ScanAsync(input, output, proxy.Uri);
        Assert.AreEqual("i2patlas.raw-scan", scan.Schema);
        Assert.AreEqual(1, scan.Version);
        Assert.AreEqual(3, scan.TotalSeeds);
        Assert.AreEqual(3, scan.SnapshotSeedCount);
        Assert.IsFalse(scan.SeedLimitApplied);
        CollectionAssert.AreEqual(new[] { "host0.i2p", "host1.i2p", "host2.i2p" }, scan.Results.Select(r => r.Hostname).ToArray());
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(bytes)), scan.SeedInput.FileSha256);
        Assert.AreEqual(bytes.LongLength, scan.SeedInput.FileByteLength);
        Assert.AreEqual(new string('A', 64), scan.SeedInput.AddressBookSha256);
        Assert.AreNotEqual(scan.SeedInput.AddressBookSha256, scan.SeedInput.FileSha256);
        Assert.AreEqual("127.0.0.1", scan.Proxy.Address);
        Assert.AreEqual(new Uri(proxy.Uri).Port, scan.Proxy.Port);
        Assert.AreEqual(2, scan.Settings.Concurrency);
        Assert.AreEqual(10000, scan.Settings.RequestTimeoutMilliseconds);
        Assert.AreEqual(TimeSpan.Zero, scan.StartedAtUtc.Offset);
        Assert.AreEqual(TimeSpan.Zero, scan.CompletedAtUtc.Offset);
        Assert.IsGreaterThanOrEqualTo(0d, scan.Metrics.TotalDurationMilliseconds);
        var first = scan.Results[0];
        Assert.AreEqual(SeedSnapshotTestData.Destination(), first.Destination);
        Assert.AreEqual(Target(Seeds(3)[0]).DestinationHostname, first.DestinationHostname);
        Assert.AreEqual(I2pOriginEvidence.Unproven, first.RobotsOriginEvidence);
        Assert.AreEqual(I2pProxyResponseClassification.LikelyOriginResponse, first.RobotsProxyClassification);
        Assert.IsTrue(first.HomepageRequestAttempted);
        Assert.AreEqual(200, first.HomepageResponse!.StatusCode);
        Assert.AreEqual("Example & site", first.HomepageMetadata!.Title);
        Assert.HasCount(2, first.HomepageMetadata.Links);
        Assert.AreEqual(I2pOriginEvidence.Unproven, first.HomepageOriginEvidence);
        Assert.AreEqual(I2pProxyResponseClassification.Ambiguous, scan.Results[1].RobotsProxyClassification);
        Assert.AreEqual(I2pProxyResponseClassification.LikelyProxyFailure, scan.Results[2].RobotsProxyClassification);
        Assert.AreEqual(3, scan.Metrics.RobotsRequestOutcomes["ProxyHttpResponse"]);
        foreach (var classification in Enum.GetNames<I2pProxyResponseClassification>()) Assert.AreEqual(1, scan.Metrics.RobotsProxyClassifications[classification]);
        Assert.AreEqual(1, scan.Metrics.HomepageAttemptedCount);
        Assert.AreEqual(1, scan.Metrics.HomepageResponseCount);
        Assert.AreEqual(1, scan.Metrics.HomepageRequestOutcomes["ProxyHttpResponse"]);
        Assert.AreEqual(2, scan.Metrics.HomepageRequestOutcomes["NotAttempted"]);
        Assert.AreEqual(0, scan.Metrics.UnexpectedProbeFailureCount);
        Assert.HasCount(4, proxy.Requests);
        foreach (var uri in proxy.Requests)
        {
            Assert.Contains(uri.Host, hosts.Keys);
            Assert.IsTrue(uri.AbsolutePath is "/" or "/robots.txt");
            Assert.AreEqual("", uri.Query);
        }
        Assert.IsEmpty(dns.Hostnames);
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(input));
        var json = await File.ReadAllTextAsync(output);
        Assert.Contains(SeedSnapshotTestData.Destination(), json);
        Assert.DoesNotContain("ResolutionSource", json);
        Assert.DoesNotContain("DomainResolution", json);
        Assert.DoesNotContain(SeedSnapshotTestData.SourcePath, json);
        var roundTrip = JsonSerializer.Deserialize<I2pRawScanArtifact>(json, I2pRawScanFile.JsonOptions)!;
        Assert.AreEqual(3, roundTrip.TotalSeeds);
        Assert.AreEqual(first.HomepageMetadata.Title, roundTrip.Results[0].HomepageMetadata!.Title);
        Assert.IsFalse(Directory.Exists(Path.Combine(files.DirectoryPath, "published")));
        Assert.IsFalse(Directory.Exists(Path.Combine(files.DirectoryPath, "results")));
        AssertNoStaging(files);
    }

    [TestMethod]
    public async Task EmptySnapshotProducesValidEmptyArtifactWithoutConnecting()
    {
        using var files = new TestFiles();
        var input = Snapshot(files, 0);
        await using var proxy = new ScanProxy((_, _) => throw new AssertFailedException("Empty scan must not connect."));
        var scan = await new I2pScanService().ScanAsync(input, Path.Combine(files.DirectoryPath, "scan.json"), proxy.Uri);
        Assert.IsEmpty(scan.Results);
        Assert.IsEmpty(scan.Metrics.RobotsRequestOutcomes);
        Assert.AreEqual(0, scan.Metrics.MaximumActiveSeeds);
        Assert.AreEqual(0, scan.Metrics.HomepageAttemptedCount);
        Assert.IsEmpty(proxy.Requests);
    }

    [TestMethod]
    public async Task FixedWorkerCountBoundsConcurrencyAndSeedLimitUsesSortedPrefix()
    {
        using var files = new TestFiles();
        var input = Snapshot(files, 8);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var two = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        await using var proxy = new ScanProxy(async (_, token) =>
        {
            if (Interlocked.Increment(ref count) == 2) two.TrySetResult();
            await gate.Task.WaitAsync(token);
            return Response(404, "none", "text/plain");
        });
        var pending = new I2pScanService().ScanAsync(input, Path.Combine(files.DirectoryPath, "scan.json"), proxy.Uri,
            new(concurrency: 2, maximumSeeds: 5));
        await two.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await Task.Delay(100);
            Assert.HasCount(2, proxy.Requests);
        }
        finally { gate.TrySetResult(); }
        var scan = await pending;
        Assert.AreEqual(2, proxy.MaximumProcessing);
        Assert.AreEqual(2, scan.Metrics.MaximumActiveSeeds);
        Assert.AreEqual(8, scan.SnapshotSeedCount);
        Assert.AreEqual(5, scan.TotalSeeds);
        Assert.IsTrue(scan.SeedLimitApplied);
        CollectionAssert.AreEqual(Seeds(8).Take(5).Select(s => s.Hostname).ToArray(), scan.Results.Select(r => r.Hostname).ToArray());
        Assert.HasCount(5, proxy.Requests);
    }

    [TestMethod]
    public async Task TimedOutSeedDoesNotAbortOtherSeeds()
    {
        using var files = new TestFiles();
        var input = Snapshot(files, 3);
        var slow = Target(Seeds(3)[0]).DestinationHostname;
        await using var proxy = new ScanProxy((uri, _) => Task.FromResult<byte[]?>(uri.Host == slow ? null : Response(404, "no policy", "text/plain")));
        var scan = await new I2pScanService().ScanAsync(input, Path.Combine(files.DirectoryPath, "scan.json"), proxy.Uri,
            new(concurrency: 1, requestTimeoutMilliseconds: 300));
        Assert.AreEqual(I2pRequestOutcome.TimedOut, scan.Results[0].RobotsRequestOutcome);
        Assert.AreEqual("TimedOut", scan.Results[0].Errors.Single().Category);
        Assert.IsTrue(scan.Results.Skip(1).All(r => r.RobotsRequestOutcome == I2pRequestOutcome.ProxyHttpResponse));
        Assert.AreEqual(1, scan.Metrics.RobotsRequestOutcomes["TimedOut"]);
        Assert.AreEqual(2, scan.Metrics.RobotsRequestOutcomes["ProxyHttpResponse"]);
        Assert.HasCount(3, proxy.Requests);
    }

    [TestMethod]
    public async Task RefusedProxyStillProducesOneFailureRecordPerSeed()
    {
        using var files = new TestFiles();
        using var reserved = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        reserved.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var scan = await new I2pScanService().ScanAsync(Snapshot(files, 3), Path.Combine(files.DirectoryPath, "scan.json"),
            $"http://127.0.0.1:{((IPEndPoint)reserved.LocalEndPoint!).Port}");
        Assert.AreEqual(3, scan.Metrics.RobotsRequestOutcomes["ProxyUnavailable"]);
        Assert.IsTrue(scan.Results.All(r => r.Errors.Single().Category == "ProxyUnavailable"));
        Assert.IsTrue(scan.Results.All(r => r.RobotsOriginEvidence == I2pOriginEvidence.NotObserved));
    }

    [TestMethod]
    public async Task CancellationStopsWorkersAndPreservesPreviousOutput()
    {
        using var files = new TestFiles();
        var input = Snapshot(files, 6);
        var output = files.Write("scan.json", "previous valid artifact");
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var proxy = new ScanProxy((_, _) => { received.TrySetResult(); return Task.FromResult<byte[]?>(null); });
        using var cancellation = new CancellationTokenSource();
        var pending = new I2pScanService().ScanAsync(input, output, proxy.Uri, cancellationToken: cancellation.Token);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var error = await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        Assert.AreEqual(cancellation.Token, error.CancellationToken);
        Assert.AreEqual("previous valid artifact", await File.ReadAllTextAsync(output));
        Assert.IsLessThanOrEqualTo(2, proxy.Requests.Count);
        AssertNoStaging(files);
    }

    [TestMethod]
    public async Task CompletePrivateStageExistsBeforeAtomicReplacement()
    {
        using var files = new TestFiles();
        var input = Snapshot(files, 0);
        var output = files.Write("scan.json", "old artifact");
        var observed = false;
        var service = new I2pScanService(null, staged =>
        {
            observed = true;
            Assert.AreEqual("old artifact", File.ReadAllText(output));
            using var document = JsonDocument.Parse(File.ReadAllBytes(staged));
            Assert.AreEqual("i2patlas.raw-scan", document.RootElement.GetProperty("schema").GetString());
            if (!OperatingSystem.IsWindows())
                Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(staged));
        });
        await service.ScanAsync(input, output, "http://127.0.0.1:4444");
        Assert.IsTrue(observed);
        Assert.Contains("i2patlas.raw-scan", await File.ReadAllTextAsync(output));
        AssertNoStaging(files);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CancellationOrIoFailureAfterStagingNeverInstallsPartialOutput(bool cancel)
    {
        using var files = new TestFiles();
        var input = Snapshot(files, 0);
        var output = files.Write("scan.json", "previous artifact");
        using var cancellation = new CancellationTokenSource();
        var service = new I2pScanService(null, _ =>
        {
            if (cancel) cancellation.Cancel();
            else throw new IOException("simulated replacement failure");
        });
        var pending = service.ScanAsync(input, output, "http://127.0.0.1:4444", cancellationToken: cancellation.Token);
        if (cancel) await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        else await Assert.ThrowsExactlyAsync<IOException>(() => pending);
        Assert.AreEqual("previous artifact", await File.ReadAllTextAsync(output));
        AssertNoStaging(files);
    }

    [TestMethod]
    public async Task GlobalEvidenceBudgetFailurePreservesOutputAndCancelsRemainingWork()
    {
        using var files = new TestFiles();
        var output = files.Write("scan.json", "previous artifact");
        await using var proxy = new ScanProxy((_, _) => Task.FromResult<byte[]?>(Response(404, "none", "text/plain")));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new I2pScanService(null, null, textBudget: 1)
            .ScanAsync(Snapshot(files, 6), output, proxy.Uri));
        Assert.AreEqual("previous artifact", await File.ReadAllTextAsync(output));
        Assert.IsLessThanOrEqualTo(2, proxy.Requests.Count);
        AssertNoStaging(files);
    }

    [TestMethod]
    public async Task SerializationFailureRemovesPartialStageAndPreservesPreviousOutput()
    {
        using var files = new TestFiles();
        var output = Path.Combine(files.DirectoryPath, "scan.json");
        var scan = await new I2pScanService().ScanAsync(Snapshot(files, 0), output, "http://127.0.0.1:4444");
        var previous = await File.ReadAllBytesAsync(output);
        var invalid = new I2pRawScanArtifact(scan.Schema, scan.Version, scan.StartedAtUtc, scan.CompletedAtUtc,
            scan.SeedInput, scan.Proxy, scan.Settings, scan.SnapshotSeedCount, scan.Results,
            scan.Metrics with { TotalDurationMilliseconds = double.NaN });
        await Assert.ThrowsAsync<ArgumentException>(() => I2pRawScanFile.WriteAsync(output, invalid, CancellationToken.None));
        CollectionAssert.AreEqual(previous, await File.ReadAllBytesAsync(output));
        AssertNoStaging(files);
    }

    [TestMethod]
    [DataRow("same-input")]
    [DataRow("missing-directory")]
    [DataRow("public")]
    [DataRow("published")]
    [DataRow("wwwroot")]
    [DataRow("site")]
    public async Task UnsafeOutputFailsBeforeNetworkingAndPreservesInput(string failure)
    {
        using var files = new TestFiles();
        var input = Snapshot(files, 1);
        var original = await File.ReadAllBytesAsync(input);
        var directory = Path.Combine(files.DirectoryPath, failure);
        if (failure is not ("same-input" or "missing-directory")) Directory.CreateDirectory(directory);
        var output = failure == "same-input" ? input : Path.Combine(directory, "scan.json");
        await using var proxy = new ScanProxy((_, _) => throw new AssertFailedException("Path rejection must precede network."));
        if (failure == "missing-directory")
            await Assert.ThrowsExactlyAsync<DirectoryNotFoundException>(() => new I2pScanService().ScanAsync(input, output, proxy.Uri));
        else await Assert.ThrowsExactlyAsync<ArgumentException>(() => new I2pScanService().ScanAsync(input, output, proxy.Uri));
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(input));
        Assert.IsEmpty(proxy.Requests);
        AssertNoStaging(files);
    }

    [TestMethod]
    [DataRow("schema")]
    [DataRow("version")]
    public async Task RawEnvelopeRejectsUnsupportedSchemaOrVersion(string property)
    {
        using var files = new TestFiles();
        var output = Path.Combine(files.DirectoryPath, "scan.json");
        await new I2pScanService().ScanAsync(Snapshot(files, 0), output, "http://127.0.0.1:4444");
        var node = JsonNode.Parse(await File.ReadAllTextAsync(output))!;
        if (property == "schema") node[property] = "dn42atlas.raw-scan";
        else node[property] = 2;
        Assert.ThrowsExactly<InvalidDataException>(() => JsonSerializer.Deserialize<I2pRawScanArtifact>(node.ToJsonString(), I2pRawScanFile.JsonOptions));
    }

    [TestMethod]
    public async Task ExactSnapshotHashChangesWithWhitespaceAndSourceProvenanceIsNeverOpened()
    {
        using var files = new TestFiles();
        var path = Snapshot(files, 1);
        var original = await SeedSnapshotReader.ReadWithIdentityAsync(path);
        await File.AppendAllTextAsync(path, "\n\t ");
        var changed = await SeedSnapshotReader.ReadWithIdentityAsync(path);
        Assert.AreNotEqual(original.FileSha256, changed.FileSha256);
        Assert.AreEqual(original.Snapshot.Seeds[0], changed.Snapshot.Seeds[0]);
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))), changed.FileSha256);
        Assert.AreEqual(SeedSnapshotTestData.SourcePath, changed.Snapshot.Source.Path);
    }

    [TestMethod]
    public async Task CliUsesExplicitProxyAndKeepsDestinationsOnlyInPrivateArtifact()
    {
        using var files = new TestFiles();
        Snapshot(files, 2, "private seeds.json");
        var before = Directory.GetFileSystemEntries(files.DirectoryPath).Order(StringComparer.Ordinal).ToArray();
        await using var proxy = new ScanProxy((_, _) => Task.FromResult<byte[]?>(Response(404, "none", "text/plain")));
        var result = await RunCli(files.DirectoryPath, "i2p-scan", "private seeds.json", "private scan.json", proxy.Uri,
            "--concurrency", "1", "--max-seeds", "2", "--timeout-ms", "1000");
        Assert.AreEqual(0, result.Code, result.Error);
        Assert.AreEqual("", result.Error);
        Assert.Contains("Private I2P scan complete: 2 seeds", result.Output);
        foreach (var seed in Seeds(2))
        {
            Assert.DoesNotContain(seed.Destination, result.Output);
            Assert.DoesNotContain(seed.Hostname, result.Output);
            Assert.Contains(seed.Destination, await File.ReadAllTextAsync(Path.Combine(files.DirectoryPath, "private scan.json")));
        }
        Assert.DoesNotContain(SeedSnapshotTestData.SourcePath, result.Output);
        CollectionAssert.AreEqual(before.Append(Path.Combine(files.DirectoryPath, "private scan.json")).Order(StringComparer.Ordinal).ToArray(),
            Directory.GetFileSystemEntries(files.DirectoryPath).Order(StringComparer.Ordinal).ToArray());
        var allowed = Seeds(2).Select(s => Target(s).DestinationHostname).ToArray();
        foreach (var uri in proxy.Requests) Assert.Contains(uri.Host, allowed);
        Assert.HasCount(2, proxy.Requests);
    }

    [TestMethod]
    [DataRow("missing-proxy", 2)]
    [DataRow("ambient-proxy", 2)]
    [DataRow("bad-concurrency", 2)]
    [DataRow("bad-timeout", 2)]
    [DataRow("bad-limit", 2)]
    [DataRow("duplicate-option", 2)]
    [DataRow("invalid-snapshot", 1)]
    public async Task CommandFailuresPreservePreviousArtifactAndDoNotDisclosePrivateInput(string failure, int expected)
    {
        using var files = new TestFiles();
        var input = failure == "invalid-snapshot" ? files.Write("seeds.json", "PRIVATE invalid JSON") : Snapshot(files, 1);
        var outputPath = files.Write("scan.json", "previous artifact");
        var args = new List<string> { "i2p-scan", input, outputPath };
        if (failure != "missing-proxy") args.Add(failure == "ambient-proxy" ? "http://localhost:4444" : "http://127.0.0.1:4444");
        if (failure == "bad-concurrency") args.AddRange(["--concurrency", "5"]);
        if (failure == "bad-timeout") args.AddRange(["--timeout-ms", "60001"]);
        if (failure == "bad-limit") args.AddRange(["--max-seeds", "0"]);
        if (failure == "duplicate-option") args.AddRange(["--concurrency", "1", "--concurrency", "2"]);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.AreEqual(expected, await I2pScanCommand.ExecuteAsync(args.ToArray(), output, error));
        Assert.AreEqual("previous artifact", await File.ReadAllTextAsync(outputPath));
        Assert.DoesNotContain("PRIVATE invalid JSON", error.ToString());
        Assert.DoesNotContain(SeedSnapshotTestData.Destination(), error.ToString());
        Assert.DoesNotContain(input, error.ToString());
        AssertNoStaging(files);
    }

    [TestMethod]
    public async Task CommandCancellationReturns130AndDoesNotCreateOutput()
    {
        using var files = new TestFiles();
        var input = Snapshot(files, 0);
        var path = Path.Combine(files.DirectoryPath, "scan.json");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.AreEqual(130, await I2pScanCommand.ExecuteAsync(["i2p-scan", input, path, "http://127.0.0.1:4444"], output, error, cancellation.Token));
        Assert.IsFalse(File.Exists(path));
        Assert.AreEqual("", output.ToString());
        AssertNoStaging(files);
    }

    private static I2pSeed[] Seeds(int count) => Enumerable.Range(0, count).Select(i =>
        new I2pSeed($"host{i}.i2p", SeedSnapshotTestData.Destination(salt: i))).OrderBy(s => s.Hostname, StringComparer.Ordinal).ToArray();

    private static I2pHttpRequestTarget Target(I2pSeed seed) => new(seed, $"http://{seed.Hostname}/robots.txt");

    private static string Snapshot(TestFiles files, int count, string filename = "seeds.json")
    {
        var node = SeedSnapshotTestData.Snapshot();
        node["seeds"] = new JsonArray(Seeds(count).Reverse().Select(s => (JsonNode)new JsonObject
        { ["hostname"] = s.Hostname.ToUpperInvariant() + ".", ["destination"] = s.Destination }).ToArray());
        return files.Write(filename, node.ToJsonString());
    }

    private static void AssertNoStaging(TestFiles files) => Assert.IsEmpty(Directory.GetFiles(files.DirectoryPath, ".i2p-scan-*.tmp"));
    private static byte[] Response(int status, string body, string type) => Encoding.UTF8.GetBytes(
        $"HTTP/1.1 {status} Test\r\nContent-Type: {type}\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}");

    private static async Task<(int Code, string Output, string Error)> RunCli(string directory, params string[] args)
    {
        var start = new ProcessStartInfo("dotnet")
        { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add(typeof(I2pScanCommand).Assembly.Location);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment["DN42ATLAS_RUNTIME_EXCLUSIONS_PATH"] = Path.Combine(directory, "missing-runtime.json");
        start.Environment["DN42ATLAS_REGISTRY_PATH"] = Path.Combine(directory, "missing-registry");
        start.Environment["HTTP_PROXY"] = start.Environment["HTTPS_PROXY"] = start.Environment["ALL_PROXY"] = "http://127.0.0.1:1";
        start.Environment["NO_PROXY"] = "*";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        return (process.ExitCode, await output, await error);
    }

    private sealed class NameResolutionObserver : EventListener
    {
        public ConcurrentQueue<string> Hostnames { get; } = new();
        protected override void OnEventSourceCreated(EventSource source)
        { if (source.Name == "System.Net.NameResolution") EnableEvents(source, EventLevel.Verbose); }
        protected override void OnEventWritten(EventWrittenEventArgs data)
        { if (data.EventName == "ResolutionStart" && data.Payload?.FirstOrDefault() is string host) Hostnames.Enqueue(host); }
    }

    private sealed class ScanProxy : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stopped = new();
        private readonly List<Task> connections = [];
        private readonly Task accepting;
        private int processing;
        private int maximumProcessing;
        public int MaximumProcessing => maximumProcessing;
        public ConcurrentQueue<Uri> Requests { get; } = new();
        public string Uri { get; }

        public ScanProxy(Func<Uri, CancellationToken, Task<byte[]?>> response)
        {
            listener.Start();
            Uri = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
            accepting = AcceptAsync(response);
        }

        private async Task AcceptAsync(Func<Uri, CancellationToken, Task<byte[]?>> response)
        {
            try
            {
                while (!stopped.IsCancellationRequested)
                {
                    var connection = await listener.AcceptTcpClientAsync(stopped.Token);
                    connections.Add(ServeAsync(connection, response));
                }
            }
            catch (OperationCanceledException) when (stopped.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (stopped.IsCancellationRequested) { }
            catch (SocketException) when (stopped.IsCancellationRequested) { }
        }

        private async Task ServeAsync(TcpClient connection, Func<Uri, CancellationToken, Task<byte[]?>> respond)
        {
            using (connection)
            try
            {
                using var stream = connection.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                var request = await reader.ReadLineAsync(stopped.Token);
                if (request == null) return;
                var size = request.Length;
                while (await reader.ReadLineAsync(stopped.Token) is { Length: > 0 } header)
                { if ((size += header.Length) > 8192) throw new AssertFailedException("Oversized test request."); }
                var uri = new Uri(request.Split(' ')[1]);
                Requests.Enqueue(uri);
                var current = Interlocked.Increment(ref processing);
                int old;
                do { old = maximumProcessing; }
                while (current > old && Interlocked.CompareExchange(ref maximumProcessing, current, old) != old);
                byte[]? bytes;
                try
                {
                    bytes = await respond(uri, stopped.Token);
                    if (bytes == null) await Task.Delay(Timeout.InfiniteTimeSpan, stopped.Token);
                }
                finally { Interlocked.Decrement(ref processing); }
                if (bytes != null) await stream.WriteAsync(bytes, stopped.Token);
            }
            catch (OperationCanceledException) when (stopped.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (stopped.IsCancellationRequested) { }
            catch (IOException) when (stopped.IsCancellationRequested) { }
        }

        public async ValueTask DisposeAsync()
        {
            stopped.Cancel();
            listener.Stop();
            await accepting.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAll(connections).WaitAsync(TimeSpan.FromSeconds(5));
            stopped.Dispose();
        }
    }
}
