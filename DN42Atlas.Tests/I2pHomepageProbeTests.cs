using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DN42Atlas.AddressBook;
using DN42Atlas.Commands;
using DN42Atlas.I2pProbing;
using DN42Atlas.I2pTransport;

namespace DN42Atlas.Tests;

[TestClass]
public sealed class I2pHomepageProbeTests
{
    private const string Allow = "User-agent: *\nAllow: /";
    private const string Plain = "Content-Type: text/plain; charset=utf-8\r\n";
    private static readonly I2pSeed Seed = new("friendly.i2p", SeedSnapshotTestData.Destination());
    private static readonly I2pHttpRequestTarget Root = new(Seed, "http://friendly.i2p/");

    [TestMethod]
    [DataRow(200, Allow, Plain, I2pProxyResponseClassification.LikelyOriginResponse, true)]
    [DataRow(200, "User-agent: *\nDisallow: /", Plain, I2pProxyResponseClassification.LikelyOriginResponse, false)]
    [DataRow(200, "User-agent: *\nDisallow: nope", Plain, I2pProxyResponseClassification.Ambiguous, false)]
    [DataRow(200, Allow, "Content-Type: text/html\r\n", I2pProxyResponseClassification.Ambiguous, false)]
    [DataRow(403, Allow, Plain, I2pProxyResponseClassification.Ambiguous, false)]
    [DataRow(404, "", Plain, I2pProxyResponseClassification.Ambiguous, false)]
    [DataRow(500, Allow, Plain, I2pProxyResponseClassification.Ambiguous, false)]
    [DataRow(502, Allow, Plain, I2pProxyResponseClassification.LikelyProxyFailure, false)]
    [DataRow(503, Allow, Plain, I2pProxyResponseClassification.LikelyProxyFailure, false)]
    [DataRow(504, Allow, Plain, I2pProxyResponseClassification.LikelyProxyFailure, false)]
    [DataRow(302, Allow, "Location: http://other.i2p/?i2paddresshelper=x\r\n", I2pProxyResponseClassification.Ambiguous, false)]
    [DataRow(200, Allow, "", I2pProxyResponseClassification.Ambiguous, false)]
    [DataRow(200, "", Plain, I2pProxyResponseClassification.Ambiguous, false)]
    [DataRow(200, "# no policy\n", Plain, I2pProxyResponseClassification.Ambiguous, false)]
    [DataRow(200, "User-agent: *", Plain, I2pProxyResponseClassification.Ambiguous, false)]
    [DataRow(200, "User-agent: *\nError: unavailable", Plain, I2pProxyResponseClassification.Ambiguous, false)]
    [DataRow(200, "User-agent: *\nDisallow:", Plain, I2pProxyResponseClassification.LikelyOriginResponse, true)]
    public async Task HomepageGateRequiresLikelyOriginAndAllow(int status, string body, string headers,
        I2pProxyResponseClassification classification, bool attempted)
    {
        await using var proxy = new FakeProxy(n => n == 1 ? Response(status, body, headers) : Response(200, "<title>Root</title>", "Content-Type: text/html\r\n"));
        using var transport = new I2pHttpTransport(proxy.Uri);
        var result = await new I2pHttpProbe(transport).ProbeAsync(Seed);
        Assert.AreEqual(I2pRequestOutcome.ProxyHttpResponse, result.RequestOutcome);
        Assert.AreEqual(I2pOriginEvidence.Unproven, result.OriginEvidence);
        Assert.AreEqual(classification, result.ProxyResponseClassification);
        Assert.AreEqual(status, result.RobotsResponse!.StatusCode);
        Assert.AreEqual(attempted, result.HomepageRequestAttempted);
        if (attempted)
        {
            Assert.AreEqual(I2pRobotsObservation.AllowsRoot, result.Robots);
            Assert.AreEqual(I2pHomepageDisposition.ResponseObserved, result.Homepage);
            Assert.AreEqual(I2pOriginEvidence.Unproven, result.HomepageOriginEvidence);
            Assert.AreEqual(I2pProxyResponseClassification.Ambiguous, result.HomepageProxyResponseClassification);
            Assert.AreEqual("Root", result.HomepageMetadata!.Title);
        }
        else
        {
            Assert.IsNull(result.HomepageResponse);
            Assert.IsNull(result.HomepageRequestOutcome);
            if (classification == I2pProxyResponseClassification.LikelyProxyFailure)
                Assert.AreEqual(I2pHomepageDisposition.SkippedProxyFailure, result.Homepage);
            if (body == "User-agent: *\nDisallow: /")
                Assert.AreEqual(I2pHomepageDisposition.SkippedObservedDisallow, result.Homepage);
        }
        AssertRequests(proxy, attempted ? 2 : 1);
    }

    [TestMethod]
    [DataRow("Content-Type: text/plain; charset=iso-8859-1\r\n")]
    [DataRow("Content-Type: text/plain; charset=utf-8; charset=iso-8859-1\r\n")]
    [DataRow("Content-Type: text/plain\r\nContent-Type: text/html\r\n")]
    [DataRow("Content-Type: text/plain\r\nContent-Encoding: gzip\r\n")]
    [DataRow("Content-Type: text/plain\r\nContent-Range: bytes 0-1/100\r\n")]
    public async Task RepresentationAmbiguityCannotAuthorizeRoot(string headers)
    {
        await using var proxy = new FakeProxy(_ => Response(200, Allow, headers));
        using var transport = new I2pHttpTransport(proxy.Uri);
        var result = await new I2pHttpProbe(transport).ProbeAsync(Seed);
        Assert.AreEqual(I2pProxyResponseClassification.Ambiguous, result.ProxyResponseClassification);
        Assert.IsFalse(result.HomepageRequestAttempted);
        AssertRequests(proxy, 1);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InvalidUtf8OrOversizedRobotsCannotAuthorizeRoot(bool oversized)
    {
        var body = oversized ? Encoding.UTF8.GetBytes(Allow.PadRight(I2pHttpProbe.MaxRobotsBytes + 1)) : new byte[] { 0xc3, 0x28 };
        await using var proxy = new FakeProxy(_ => Response(200, body, Plain));
        using var transport = new I2pHttpTransport(proxy.Uri);
        var result = await new I2pHttpProbe(transport).ProbeAsync(Seed);
        Assert.AreEqual(oversized ? I2pRobotsObservation.TooLarge : I2pRobotsObservation.Malformed, result.Robots);
        Assert.AreEqual(I2pProxyResponseClassification.Ambiguous, result.ProxyResponseClassification);
        Assert.IsFalse(result.HomepageRequestAttempted);
        AssertRequests(proxy, 1);
    }

    [TestMethod]
    public async Task HomepageCapturesMetadataAndNeverFetchesLinksOrProcessesAddressHelpers()
    {
        const string html = """
            <title> A &amp; B
              site </title>
            <a href="/relative#fragment">relative</a>
            <a href="http://other.i2p/path">I2P</a>
            <a href="https://example.com/path?a=1&amp;b=2">clearnet</a>
            <a href="http://other.i2p/?i2paddresshelper=untrusted">helper</a>
            <a href="http://other.i2p/?%69%32%70addresshelper=untrusted">encoded helper</a>
            """;
        await using var proxy = new FakeProxy(n => n == 1 ? Response(200, Allow, Plain) : Response(200, html, "Content-Type: text/html; charset=utf-8\r\n"));
        using var transport = new I2pHttpTransport(proxy.Uri);
        var result = await new I2pHttpProbe(transport).ProbeAsync(Seed);
        Assert.AreEqual(I2pRequestOutcome.ProxyHttpResponse, result.HomepageRequestOutcome);
        Assert.AreEqual(200, result.HomepageResponse!.StatusCode);
        Assert.AreEqual("text/html; charset=utf-8", result.HomepageResponse.ContentType);
        Assert.AreEqual(Encoding.UTF8.GetByteCount(html), result.HomepageResponse.BodyBytes);
        Assert.AreNotSame(result.RobotsResponse, result.HomepageResponse);
        var metadata = result.HomepageMetadata!;
        Assert.AreEqual("A & B site", metadata.Title);
        Assert.Contains(new I2pDiscoveredLink($"http://{Root.DestinationHostname}/relative", I2pDiscoveredLinkKind.I2p), metadata.Links);
        Assert.Contains(new I2pDiscoveredLink("http://other.i2p/path", I2pDiscoveredLinkKind.I2p), metadata.Links);
        Assert.Contains(new I2pDiscoveredLink("https://example.com/path?a=1&b=2", I2pDiscoveredLinkKind.Clearnet), metadata.Links);
        Assert.HasCount(3, metadata.Links);
        AssertRequests(proxy, 2);
    }

    [TestMethod]
    [DataRow(403, I2pProxyResponseClassification.Ambiguous)]
    [DataRow(502, I2pProxyResponseClassification.LikelyProxyFailure)]
    [DataRow(503, I2pProxyResponseClassification.LikelyProxyFailure)]
    [DataRow(504, I2pProxyResponseClassification.LikelyProxyFailure)]
    public async Task HomepageErrorsHaveSeparateObservationsAndNeverOverwriteRobots(int status, I2pProxyResponseClassification classification)
    {
        await using var proxy = new FakeProxy(n => n == 1 ? Response(200, Allow, Plain) : Response(status, "<title>proxy error</title>", "Content-Type: text/html\r\n"));
        using var transport = new I2pHttpTransport(proxy.Uri);
        var result = await new I2pHttpProbe(transport).ProbeAsync(Seed);
        AssertRobotsPreserved(result);
        Assert.IsTrue(result.HomepageRequestAttempted);
        Assert.AreEqual(I2pHomepageDisposition.ResponseObserved, result.Homepage);
        Assert.AreEqual(status, result.HomepageResponse!.StatusCode);
        Assert.AreEqual(classification, result.HomepageProxyResponseClassification);
        Assert.AreEqual(I2pOriginEvidence.Unproven, result.HomepageOriginEvidence);
        Assert.IsNull(result.HomepageMetadata);
        AssertRequests(proxy, 2);
    }

    [TestMethod]
    [DataRow("/")]
    [DataRow("http://other.i2p/")]
    [DataRow("http://example.com/?i2paddresshelper=untrusted")]
    public async Task HomepageRedirectIsRecordedAndNeverFollowed(string location)
    {
        await using var proxy = new FakeProxy(n => n == 1 ? Response(200, Allow, Plain) : Response(302, "", $"Location: {location}\r\n"));
        using var transport = new I2pHttpTransport(proxy.Uri);
        var result = await new I2pHttpProbe(transport).ProbeAsync(Seed);
        AssertRobotsPreserved(result);
        Assert.AreEqual(302, result.HomepageResponse!.StatusCode);
        Assert.AreEqual(location, result.HomepageResponse.RedirectLocation);
        Assert.IsNull(result.HomepageMetadata);
        AssertRequests(proxy, 2);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task HomepageDeadlinePreservesRobotsAndRemainsDistinctFromCancellation(bool partialBody)
    {
        await using var proxy = new FakeProxy(n => n == 1 ? Response(200, Allow, Plain) : partialBody
            ? Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 100\r\n\r\nx") : null,
            holdHomepageOpen: true);
        using var transport = new I2pHttpTransport(proxy.Uri, TimeSpan.FromMilliseconds(500));
        var result = await new I2pHttpProbe(transport).ProbeAsync(Seed);
        AssertRobotsPreserved(result);
        Assert.IsTrue(result.HomepageRequestAttempted);
        Assert.AreEqual(I2pRequestOutcome.TimedOut, result.HomepageRequestOutcome);
        Assert.AreEqual(I2pHomepageDisposition.RequestFailed, result.Homepage);
        Assert.AreEqual(I2pOriginEvidence.NotObserved, result.HomepageOriginEvidence);
        Assert.IsNull(result.HomepageResponse);
        AssertRequests(proxy, 2);
    }

    [TestMethod]
    public async Task HomepageCallerCancellationPropagates()
    {
        await using var proxy = new FakeProxy(n => n == 1 ? Response(200, Allow, Plain) : null);
        using var transport = new I2pHttpTransport(proxy.Uri);
        using var cancellation = new CancellationTokenSource();
        var pending = new I2pHttpProbe(transport).ProbeAsync(Seed, cancellation.Token);
        await proxy.HomepageReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var error = await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        Assert.AreEqual(cancellation.Token, error.CancellationToken);
        AssertRequests(proxy, 2);
    }

    [TestMethod]
    public async Task DiagnosticReportsSeparateHomepageFailureAndReturnsNonzero()
    {
        using var files = new TemporaryFiles();
        var path = files.Write("seeds.json", SeedSnapshotTestData.Snapshot().ToJsonString());
        await using var proxy = new FakeProxy(n => n == 1 ? Response(200, Allow, Plain) : null);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await I2pProbeTestCommand.ExecuteAsync(
            ["i2p-probe-test", path, "friendly.i2p", proxy.Uri, "--timeout-ms", "500"], output, error);
        Assert.AreEqual(1, code);
        var report = output.ToString();
        Assert.Contains("Request outcome: ProxyHttpResponse", report);
        Assert.Contains("Robots proxy classification (heuristic): LikelyOriginResponse", report);
        Assert.Contains("Origin evidence: Unproven", report);
        Assert.Contains("Homepage request attempted: True", report);
        Assert.Contains("Homepage request outcome: TimedOut", report);
        Assert.Contains("Homepage origin evidence: NotObserved", report);
        Assert.DoesNotContain(Seed.Destination, report);
        Assert.IsLessThanOrEqualTo(I2pProbeTestCommand.MaxReportCharacters, report.Length);
        Assert.AreEqual("", error.ToString());
        AssertRequests(proxy, 2);
    }

    [TestMethod]
    public async Task HomepageProxyConnectionRefusalPreservesRobotsAndHasNoFallback()
    {
        await using var proxy = new FakeProxy(_ => Response(200, Allow, Plain), stopBeforeFirstResponse: true);
        using var transport = new I2pHttpTransport(proxy.Uri);
        var result = await new I2pHttpProbe(transport).ProbeAsync(Seed);
        AssertRobotsPreserved(result);
        Assert.IsTrue(result.HomepageRequestAttempted);
        Assert.AreEqual(I2pRequestOutcome.ProxyUnavailable, result.HomepageRequestOutcome);
        Assert.AreEqual(I2pHomepageDisposition.RequestFailed, result.Homepage);
        Assert.IsNull(result.HomepageResponse);
        AssertRequests(proxy, 1);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task HomepageBodyCapAndMetadataBoundsAreEnforced(bool oversized)
    {
        var html = "<title>" + new string('t', 1000) + "</title>";
        html += string.Join(' ', Enumerable.Range(0, 251).Select(i => $"http://host{i}.i2p/"));
        html = html.PadRight(I2pHttpTransport.MaxResponseBytes + (oversized ? 1 : 0));
        await using var proxy = new FakeProxy(n => n == 1 ? Response(200, Allow, Plain) : Response(200, html, "Content-Type: text/html\r\n"));
        using var transport = new I2pHttpTransport(proxy.Uri);
        var result = await new I2pHttpProbe(transport).ProbeAsync(Seed);
        AssertRobotsPreserved(result);
        Assert.IsTrue(result.HomepageRequestAttempted);
        if (oversized)
        {
            Assert.AreEqual(I2pRequestOutcome.TransportFailure, result.HomepageRequestOutcome);
            Assert.IsNull(result.HomepageResponse);
            Assert.IsNull(result.HomepageMetadata);
        }
        else
        {
            Assert.AreEqual(I2pHttpTransport.MaxResponseBytes, result.HomepageResponse!.BodyBytes);
            Assert.AreEqual(200, result.HomepageMetadata!.Title!.Length);
            Assert.HasCount(250, result.HomepageMetadata.Links);
            Assert.IsTrue(result.HomepageMetadata.ExtractionIncomplete);
        }
        AssertRequests(proxy, 2);
    }

    [TestMethod]
    public async Task BothRequestsConnectOnlyToConfiguredLiteralProxyWithNoDns()
    {
        await using var proxy = new FakeProxy(n => n == 1 ? Response(200, Allow, Plain) : Response(200, "<title>ok</title>"));
        var endpoint = I2pProxyEndpoint.Parse(proxy.Uri);
        var connections = new List<IPEndPoint>();
        using var dns = new NameResolutionObserver();
        using var transport = new I2pHttpTransport(endpoint, TimeSpan.FromSeconds(5), async (actual, token) =>
        {
            connections.Add(actual);
            var socket = new Socket(actual.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try { await socket.ConnectAsync(actual, token); return new NetworkStream(socket, ownsSocket: true); }
            catch { socket.Dispose(); throw; }
        });
        var result = await new I2pHttpProbe(transport).ProbeAsync(Seed);
        Assert.IsTrue(result.HomepageRequestAttempted);
        Assert.HasCount(2, connections);
        foreach (var actual in connections)
        {
            Assert.AreEqual(endpoint.Address, actual.Address.ToString());
            Assert.AreEqual(endpoint.Port, actual.Port);
        }
        Assert.IsEmpty(dns.Hostnames);
        AssertRequests(proxy, 2);
    }

    private static void AssertRobotsPreserved(I2pProbeResult result)
    {
        Assert.AreEqual(I2pRequestOutcome.ProxyHttpResponse, result.RequestOutcome);
        Assert.AreEqual(200, result.RobotsResponse!.StatusCode);
        Assert.AreEqual(I2pProxyResponseClassification.LikelyOriginResponse, result.ProxyResponseClassification);
        Assert.AreEqual(I2pRobotsObservation.AllowsRoot, result.Robots);
        Assert.AreEqual(I2pOriginEvidence.Unproven, result.OriginEvidence);
    }

    private static void AssertRequests(FakeProxy proxy, int count)
    {
        Assert.HasCount(count, proxy.Requests);
        var requests = proxy.Requests.ToArray();
        for (var i = 0; i < count; i++)
        {
            Assert.AreEqual($"GET http://{Root.DestinationHostname}/{(i == 0 ? "robots.txt" : "")} HTTP/1.1", requests[i][0]);
            Assert.Contains("Host: " + Root.DestinationHostname, requests[i]);
            Assert.Contains("User-Agent: I2PAtlas/0.1", requests[i]);
        }
    }

    private static byte[] Response(int status, string body, string headers = "") => Response(status, Encoding.UTF8.GetBytes(body), headers);
    private static byte[] Response(int status, byte[] body, string headers) => Encoding.ASCII.GetBytes(
        $"HTTP/1.1 {status} Test\r\nContent-Length: {body.Length}\r\nConnection: close\r\n{headers}\r\n").Concat(body).ToArray();

    private sealed class NameResolutionObserver : EventListener
    {
        public ConcurrentQueue<string> Hostnames { get; } = new();
        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "System.Net.NameResolution") EnableEvents(source, EventLevel.Verbose);
        }
        protected override void OnEventWritten(EventWrittenEventArgs data)
        {
            if (data.EventName == "ResolutionStart" && data.Payload?.FirstOrDefault() is string host) Hostnames.Enqueue(host);
        }
    }

    private sealed class FakeProxy : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stopped = new();
        private readonly Task serving;
        public ConcurrentQueue<string[]> Requests { get; } = new();
        public TaskCompletionSource HomepageReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Uri { get; }

        public FakeProxy(Func<int, byte[]?> responses, bool holdHomepageOpen = false, bool stopBeforeFirstResponse = false)
        {
            listener.Start();
            Uri = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
            serving = ServeAsync(responses, holdHomepageOpen, stopBeforeFirstResponse);
        }

        private async Task ServeAsync(Func<int, byte[]?> responses, bool holdHomepageOpen, bool stopBeforeFirstResponse)
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
                    if (Requests.Count > 2) throw new AssertFailedException("No third request is permitted.");
                    if (Requests.Count == 2) HomepageReceived.TrySetResult();
                    if (stopBeforeFirstResponse) listener.Stop();
                    var response = responses(Requests.Count);
                    if (response != null)
                    {
                        try { await stream.WriteAsync(response, stopped.Token); }
                        catch (IOException) when (response.Length > I2pHttpTransport.MaxResponseBytes) { }
                    }
                    if (response == null || (holdHomepageOpen && Requests.Count == 2))
                        await Task.Delay(Timeout.InfiniteTimeSpan, stopped.Token);
                    if (stopBeforeFirstResponse) break;
                }
            }
            catch (OperationCanceledException) when (stopped.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (stopped.IsCancellationRequested) { }
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
