using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DN42Atlas.AddressBook;
using DN42Atlas.Commands;
using DN42Atlas.I2pTransport;

namespace DN42Atlas.Tests;

[TestClass]
public sealed class I2pProbeTestCommandTests
{
    private static readonly I2pSeed Seed = new("friendly.i2p", SeedSnapshotTestData.Destination());
    private static readonly string BoundHost = new I2pHttpRequestTarget(Seed, "http://friendly.i2p/").DestinationHostname;

    [TestMethod]
    [DataRow(200, "User-agent: *\nAllow: /", "AllowsRoot", "SkippedOriginUnproven")]
    [DataRow(404, "proxy not found", "UnknownStatus", "SkippedRobotsUnknown")]
    [DataRow(500, "proxy error", "UnknownStatus", "SkippedRobotsUnknown")]
    public async Task HttpResponsesReportObservationsWithoutAuthorizingHomepage(int status, string body, string robots, string homepage)
    {
        using var files = new TestFiles();
        var path = Snapshot(files);
        await using var proxy = new FakeProxy(Response(status, body,
            "Content-Type: text/plain; charset=utf-8\r\nServer: PRIVATE-server\r\nVia: PRIVATE-via\r\nSet-Cookie: secret=PRIVATE-cookie\r\n"));
        var result = await Execute(path, "\tFRIENDLY.I2P. ", proxy.Uri);
        Assert.AreEqual(0, result.Code, result.Error);
        Assert.AreEqual("", result.Error);
        foreach (var expected in new[]
        {
            "Friendly hostname: friendly.i2p", "Destination-derived hostname: " + BoundHost,
            "Configured proxy: " + proxy.Uri, "Request outcome: ProxyHttpResponse", "Origin evidence: Unproven",
            "HTTP status: " + status, "Content type (media type only): text/plain", "Body bytes: " + Encoding.UTF8.GetByteCount(body),
            "Robots observation: " + robots, "Homepage disposition: " + homepage, "Homepage request attempted: False",
            "Elapsed probe milliseconds:", "Proxy response != authenticated origin response."
        }) Assert.Contains(expected, result.Output);
        Assert.DoesNotContain(body, result.Output);
        Assert.DoesNotContain("PRIVATE-", result.Output);
        AssertPrivateAndBounded(result, path);
        AssertOneBoundRobotsRequest(proxy);
    }

    [TestMethod]
    [DataRow("http://other.i2p/secret-path?i2paddresshelper=SECRET#fragment", "http://other.i2p:80/")]
    [DataRow("http://user:SECRET@example.com/path?token=SECRET", "http://example.com:80/")]
    [DataRow("/?i2paddresshelper=SECRET", "[relative, invalid or unsupported location redacted]")]
    public async Task RedirectsAreRecordedWithSensitivePartsRedactedAndNeverFollowed(string location, string authority)
    {
        using var files = new TestFiles();
        var path = Snapshot(files);
        await using var proxy = new FakeProxy(Response(302, "", $"Location: {location}\r\n"));
        var result = await Execute(path, "friendly.i2p", proxy.Uri);
        Assert.AreEqual(0, result.Code);
        Assert.Contains("HTTP status: 302", result.Output);
        Assert.Contains("Robots observation: Redirect", result.Output);
        Assert.Contains("Redirect location (redacted): " + authority, result.Output);
        Assert.DoesNotContain("SECRET", result.Output);
        Assert.DoesNotContain("secret-path", result.Output);
        Assert.DoesNotContain("i2paddresshelper", result.Output);
        AssertPrivateAndBounded(result, path);
        AssertOneBoundRobotsRequest(proxy);
    }

    [TestMethod]
    public async Task UntrustedHeaderValuesCannotDiscloseDestinationOrInjectTerminalOutput()
    {
        using var files = new TestFiles();
        var path = Snapshot(files);
        var destination = Seed.Destination;
        await using var proxy = new FakeProxy(Response(302, "",
            $"Location: http://other.i2p/{destination}?token={destination}\r\n" +
            "Content-Type: text/plain; token=PRIVATE-token\r\nX-Secret: PRIVATE-secret\r\n"));
        var result = await Execute(path, "friendly.i2p", proxy.Uri);
        Assert.Contains("http://other.i2p:80/[path/query/fragment redacted]", result.Output);
        Assert.DoesNotContain("PRIVATE-", result.Output);
        AssertPrivateAndBounded(result, path);
        AssertOneBoundRobotsRequest(proxy);
    }

    [TestMethod]
    public async Task ProxyRefusalPrintsBoundedFailureDiagnosticsAndReturnsOne()
    {
        using var files = new TestFiles();
        var path = Snapshot(files);
        using var reserved = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        reserved.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var result = await Execute(path, "friendly.i2p", $"http://127.0.0.1:{((IPEndPoint)reserved.LocalEndPoint!).Port}");
        Assert.AreEqual(1, result.Code);
        Assert.Contains("Request outcome: ProxyUnavailable", result.Output);
        Assert.Contains("Origin evidence: NotObserved", result.Output);
        Assert.Contains("HTTP status: not observed", result.Output);
        Assert.Contains("Homepage disposition: SkippedRequestFailed", result.Output);
        AssertPrivateAndBounded(result, path);
    }

    [TestMethod]
    public async Task ExplicitTimeoutHasDistinctDiagnostics()
    {
        using var files = new TestFiles();
        var path = Snapshot(files);
        await using var proxy = new FakeProxy(null);
        var result = await Execute(path, "friendly.i2p", proxy.Uri, "--timeout-ms", "300");
        Assert.AreEqual(1, result.Code);
        Assert.Contains("Request outcome: TimedOut", result.Output);
        Assert.Contains("Homepage disposition: SkippedRequestFailed", result.Output);
        Assert.DoesNotContain("ProxyUnavailable", result.Output);
        AssertPrivateAndBounded(result, path);
        AssertOneBoundRobotsRequest(proxy);
    }

    [TestMethod]
    [DataRow("absent")]
    [DataRow("invalid-second-seed")]
    [DataRow("missing-hostname-field")]
    [DataRow("conflict")]
    public async Task SelectionAndWholeSnapshotValidationFinishBeforeNetworking(string failure)
    {
        using var files = new TestFiles();
        var json = SeedSnapshotTestData.Snapshot();
        var seeds = json["seeds"]!.AsArray();
        switch (failure)
        {
            case "invalid-second-seed": seeds.Add(new System.Text.Json.Nodes.JsonObject { ["hostname"] = "other.i2p", ["destination"] = "PRIVATE-invalid" }); break;
            case "missing-hostname-field": seeds[0]!.AsObject().Remove("hostname"); break;
            case "conflict": seeds.Add(new System.Text.Json.Nodes.JsonObject { ["hostname"] = "FRIENDLY.I2P.", ["destination"] = SeedSnapshotTestData.Destination(salt: 1) }); break;
        }
        var path = files.Write("seeds.json", json.ToJsonString());
        await using var proxy = new FakeProxy(Response(200, ""));
        var result = await Execute(path, failure == "absent" ? "absent.i2p" : "friendly.i2p", proxy.Uri);
        Assert.AreEqual(1, result.Code);
        Assert.AreEqual("", result.Output);
        Assert.Contains(failure == "absent" ? "absent from the validated seed snapshot" : "Invalid I2P seed snapshot", result.Error);
        Assert.DoesNotContain("PRIVATE-invalid", result.Error);
        AssertPrivateAndBounded(result, path);
        Assert.IsEmpty(proxy.Requests);
    }

    [TestMethod]
    [DataRow("missing-proxy")]
    [DataRow("empty-proxy")]
    [DataRow("ambient-proxy")]
    [DataRow("invalid-hostname")]
    [DataRow("empty-hostname")]
    [DataRow("missing-timeout-value")]
    [DataRow("invalid-timeout")]
    [DataRow("oversized-timeout")]
    [DataRow("unknown-option")]
    public async Task ExplicitArgumentsAreRequiredAndInvalidArgumentsDoNotOpenSnapshotOrNetwork(string failure)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        string[] args = failure switch
        {
            "missing-proxy" => ["i2p-probe-test", "missing.json", "friendly.i2p"],
            "empty-proxy" => ["i2p-probe-test", "missing.json", "friendly.i2p", ""],
            "ambient-proxy" => ["i2p-probe-test", "missing.json", "friendly.i2p", "http://localhost:4444"],
            "invalid-hostname" => ["i2p-probe-test", "missing.json", "http://friendly.i2p/", "http://127.0.0.1:4444"],
            "empty-hostname" => ["i2p-probe-test", "missing.json", "", "http://127.0.0.1:4444"],
            "missing-timeout-value" => ["i2p-probe-test", "missing.json", "friendly.i2p", "http://127.0.0.1:4444", "--timeout-ms"],
            "invalid-timeout" => ["i2p-probe-test", "missing.json", "friendly.i2p", "http://127.0.0.1:4444", "--timeout-ms", "0"],
            "oversized-timeout" => ["i2p-probe-test", "missing.json", "friendly.i2p", "http://127.0.0.1:4444", "--timeout-ms", "60001"],
            _ => ["i2p-probe-test", "missing.json", "friendly.i2p", "http://127.0.0.1:4444", "--trust-origin", "true"]
        };
        Assert.AreEqual(2, await I2pProbeTestCommand.ExecuteAsync(args, output, error));
        Assert.DoesNotContain("Unable to read", error.ToString());
        Assert.DoesNotContain("missing.json", error.ToString());
    }

    [TestMethod]
    public async Task MissingSnapshotErrorsOmitPrivatePathAndCallerCancellationPropagates()
    {
        using var files = new TestFiles();
        var path = Path.Combine(files.DirectoryPath, "PRIVATE-missing.json");
        var result = await Execute(path, "friendly.i2p", "http://127.0.0.1:4444");
        Assert.AreEqual(1, result.Code);
        Assert.AreEqual("", result.Output);
        Assert.Contains("Unable to read the local I2P seed snapshot", result.Error);
        Assert.DoesNotContain("PRIVATE-missing", result.Error);
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => I2pProbeTestCommand.ExecuteAsync(
            ["i2p-probe-test", path, "friendly.i2p", "http://127.0.0.1:4444"], output, error, cancellation.Token));
        Assert.AreEqual("", output.ToString());
        Assert.AreEqual("", error.ToString());
    }

    [TestMethod]
    public async Task CliDispatchUsesQuotedSnapshotPathAndIgnoresDn42AndAmbientProxyConfiguration()
    {
        using var files = new TestFiles();
        var path = Snapshot(files, "private seeds.json");
        var before = Directory.GetFileSystemEntries(files.DirectoryPath).Order(StringComparer.Ordinal).ToArray();
        await using var proxy = new FakeProxy(Response(200, "User-agent: *\nAllow: /", "Content-Type: text/plain\r\n"));
        var result = await RunCli(files.DirectoryPath, "i2p-probe-test", "private seeds.json", "FRIENDLY.I2P.", proxy.Uri);
        Assert.AreEqual(0, result.Code, result.Error);
        Assert.AreEqual("", result.Error);
        Assert.Contains("Request outcome: ProxyHttpResponse", result.Output);
        Assert.Contains("Homepage disposition: SkippedOriginUnproven", result.Output);
        AssertPrivateAndBounded(result, path);
        AssertOneBoundRobotsRequest(proxy);
        CollectionAssert.AreEqual(before, Directory.GetFileSystemEntries(files.DirectoryPath).Order(StringComparer.Ordinal).ToArray());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CliMissingHostnameOrProxyReturnsUsageWithoutDn42Setup(bool proxyMissing)
    {
        using var files = new TestFiles();
        var result = proxyMissing
            ? await RunCli(files.DirectoryPath, "i2p-probe-test", "missing.json", "friendly.i2p")
            : await RunCli(files.DirectoryPath, "i2p-probe-test", "missing.json", "", "http://127.0.0.1:4444");
        Assert.AreEqual(2, result.Code);
        Assert.Contains("Usage: dn42atlas i2p-probe-test", result.Output);
        Assert.AreEqual("", result.Error);
    }

    private static string Snapshot(TestFiles files, string filename = "seeds.json") =>
        files.Write(filename, SeedSnapshotTestData.Snapshot().ToJsonString());

    private static async Task<(int Code, string Output, string Error)> Execute(params string[] arguments)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await I2pProbeTestCommand.ExecuteAsync(["i2p-probe-test", .. arguments], output, error);
        return (code, output.ToString(), error.ToString());
    }

    private static void AssertPrivateAndBounded((int Code, string Output, string Error) result, string path)
    {
        Assert.IsLessThanOrEqualTo(I2pProbeTestCommand.MaxReportCharacters, result.Output.Length);
        foreach (var secret in new[] { Seed.Destination, SeedSnapshotTestData.SourcePath, path, new string('A', 64) })
        {
            Assert.DoesNotContain(secret, result.Output);
            Assert.DoesNotContain(secret, result.Error);
        }
    }

    private static void AssertOneBoundRobotsRequest(FakeProxy proxy)
    {
        Assert.HasCount(1, proxy.Requests);
        var request = proxy.Requests.Single();
        Assert.AreEqual($"GET http://{BoundHost}/robots.txt HTTP/1.1", request[0]);
        Assert.Contains("Host: " + BoundHost, request);
        Assert.Contains("User-Agent: I2PAtlas/0.1", request);
        Assert.IsFalse(request[0].Contains("i2paddresshelper", StringComparison.OrdinalIgnoreCase));
    }

    private static byte[] Response(int status, string body, string headers = "") => Encoding.UTF8.GetBytes(
        $"HTTP/1.1 {status} Diagnostic\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n{headers}\r\n{body}");

    private static async Task<(int Code, string Output, string Error)> RunCli(string directory, params string[] args)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        start.ArgumentList.Add(typeof(I2pProbeTestCommand).Assembly.Location);
        foreach (var argument in args) start.ArgumentList.Add(argument);
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

    private sealed class FakeProxy : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stopped = new();
        private readonly Task serving;
        public ConcurrentQueue<string[]> Requests { get; } = new();
        public string Uri { get; }

        public FakeProxy(byte[]? response)
        {
            listener.Start();
            Uri = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
            serving = ServeAsync(response);
        }

        private async Task ServeAsync(byte[]? response)
        {
            try
            {
                while (!stopped.IsCancellationRequested)
                {
                    using var connection = await listener.AcceptTcpClientAsync(stopped.Token);
                    using var stream = connection.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                    var lines = new List<string>();
                    var size = 0;
                    while (await reader.ReadLineAsync(stopped.Token) is { Length: > 0 } line)
                    {
                        size += line.Length;
                        if (size > 8192) throw new AssertFailedException("Unexpectedly large test request.");
                        lines.Add(line);
                    }
                    Requests.Enqueue(lines.ToArray());
                    if (response == null) await Task.Delay(Timeout.InfiniteTimeSpan, stopped.Token);
                    else await stream.WriteAsync(response, stopped.Token);
                }
            }
            catch (OperationCanceledException) when (stopped.IsCancellationRequested) { }
            catch (SocketException) when (stopped.IsCancellationRequested) { }
            catch (IOException) when (stopped.IsCancellationRequested) { }
        }

        public async ValueTask DisposeAsync()
        {
            stopped.Cancel();
            listener.Stop();
            await serving.WaitAsync(TimeSpan.FromSeconds(5));
            stopped.Dispose();
        }
    }
}
