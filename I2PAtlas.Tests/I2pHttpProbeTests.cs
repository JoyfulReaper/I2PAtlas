using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Net;
using System.Net.Sockets;
using System.Text;
using I2PAtlas.AddressBook;
using I2PAtlas.I2pProbing;
using I2PAtlas.I2pTransport;
using I2PAtlas.Robots;

namespace I2PAtlas.Tests;

[TestClass]
public sealed class I2pHttpProbeTests
{
    private static readonly I2pSeed Seed = new("Friendly.I2P.", Destination());
    private static readonly I2pHttpRequestTarget Root = new(Seed, "http://friendly.i2p/");

    [TestMethod]
    public async Task NullSeedIsRejectedBeforeAnyConnection()
    {
        var calls = 0;
        using var transport = new I2pHttpTransport(I2pProxyEndpoint.Parse("http://127.0.0.1:4444"), TimeSpan.FromSeconds(1),
            (_, _) => { calls++; throw new AssertFailedException("Must not connect."); });
        var result = await new I2pHttpProbe(transport).ProbeAsync(null);
        Assert.AreEqual(I2pRequestOutcome.RejectedBeforeNetworking, result.RequestOutcome);
        Assert.AreEqual(I2pOriginEvidence.NotObserved, result.OriginEvidence);
        Assert.AreEqual(I2pHomepageDisposition.SkippedRequestRejected, result.Homepage);
        Assert.IsNull(result.RobotsResponse);
        Assert.IsFalse(result.HomepageRequestAttempted);
        Assert.AreEqual(0, calls);
        Assert.ThrowsExactly<ArgumentNullException>(() => new I2pHttpProbe(null!));
    }

    [TestMethod]
    public async Task RefusedConnectionIsProxyUnavailableAndDoesNotFallback()
    {
        using var reserved = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        reserved.Bind(new IPEndPoint(IPAddress.Loopback, 0)); // reserved, not listening
        using var transport = new I2pHttpTransport($"http://127.0.0.1:{((IPEndPoint)reserved.LocalEndPoint!).Port}");
        var result = await new I2pHttpProbe(transport).ProbeAsync(Seed);
        Assert.AreEqual(I2pRequestOutcome.ProxyUnavailable, result.RequestOutcome);
        Assert.AreEqual(I2pOriginEvidence.NotObserved, result.OriginEvidence);
        Assert.AreEqual(I2pHomepageDisposition.SkippedRequestFailed, result.Homepage);
        Assert.IsNull(result.RobotsResponse);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DeadlineCoversHeadersAndBody(bool partialBody)
    {
        await using var proxy = new FakeProxy(partialBody
            ? Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 100\r\n\r\nx") : null, holdOpen: true);
        using var transport = new I2pHttpTransport(proxy.Uri, TimeSpan.FromMilliseconds(200));
        var result = await new I2pHttpProbe(transport).ProbeAsync(Seed);
        Assert.AreEqual(I2pRequestOutcome.TimedOut, result.RequestOutcome);
        Assert.IsFalse(result.HomepageRequestAttempted);
        Assert.HasCount(1, proxy.Requests);
    }

    [TestMethod]
    public async Task CallerCancellationRemainsAnException()
    {
        await using var proxy = new FakeProxy(null, holdOpen: true);
        using var transport = new I2pHttpTransport(proxy.Uri, TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var pending = new I2pHttpProbe(transport).ProbeAsync(Seed, cancellation.Token);
        await proxy.Received.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        var error = await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        Assert.AreEqual(cancellation.Token, error.CancellationToken);
        Assert.HasCount(1, proxy.Requests);
        await Assert.ThrowsAsync<OperationCanceledException>(() => new I2pHttpProbe(transport).ProbeAsync(Seed, cancellation.Token));
        Assert.HasCount(1, proxy.Requests);
    }

    [TestMethod]
    [DataRow(200, "User-agent: *\nAllow: /", I2pRobotsObservation.AllowsRoot, I2pHomepageDisposition.ResponseObserved)]
    [DataRow(200, "", I2pRobotsObservation.AllowsRoot, I2pHomepageDisposition.SkippedAmbiguousResponse)]
    [DataRow(200, "User-agent: *\nDisallow: /", I2pRobotsObservation.DisallowsRoot, I2pHomepageDisposition.SkippedObservedDisallow)]
    [DataRow(200, "<html>proxy error</html>", I2pRobotsObservation.Malformed, I2pHomepageDisposition.SkippedRobotsUnknown)]
    [DataRow(200, "User-agent:\nAllow: /", I2pRobotsObservation.Malformed, I2pHomepageDisposition.SkippedRobotsUnknown)]
    [DataRow(200, "User-agent: *\nDisallow: nope", I2pRobotsObservation.Malformed, I2pHomepageDisposition.SkippedRobotsUnknown)]
    [DataRow(200, "Allow: /", I2pRobotsObservation.Malformed, I2pHomepageDisposition.SkippedRobotsUnknown)]
    [DataRow(404, "", I2pRobotsObservation.UnknownStatus, I2pHomepageDisposition.SkippedRobotsUnknown)]
    [DataRow(500, "User-agent: *\nAllow: /", I2pRobotsObservation.UnknownStatus, I2pHomepageDisposition.SkippedRobotsUnknown)]
    [DataRow(407, "", I2pRobotsObservation.UnknownStatus, I2pHomepageDisposition.SkippedRobotsUnknown)]
    public async Task ProxyStatusAndObservedPolicyNeverAuthenticateOrigin(int status, string body,
        I2pRobotsObservation expectedRobots, I2pHomepageDisposition expectedHomepage)
    {
        // Self-asserted headers cannot upgrade provenance, even with a plausible policy.
        await using var proxy = new FakeProxy(Response(status, body,
            "Content-Type: text/plain; charset=utf-8\r\nX-I2P-Origin-Verified: true\r\nX-I2P-DestHash: alleged\r\n"));
        using var transport = new I2pHttpTransport(proxy.Uri);
        var result = await new I2pHttpProbe(transport).ProbeAsync(Seed);
        Assert.AreEqual(I2pRequestOutcome.ProxyHttpResponse, result.RequestOutcome);
        Assert.AreEqual(I2pOriginEvidence.Unproven, result.OriginEvidence);
        Assert.AreEqual(status, result.RobotsResponse!.StatusCode);
        Assert.AreEqual(Encoding.UTF8.GetByteCount(body), result.RobotsResponse.BodyBytes);
        Assert.AreEqual(expectedRobots, result.Robots);
        Assert.AreEqual(expectedHomepage, result.Homepage);
        Assert.AreEqual(expectedHomepage == I2pHomepageDisposition.ResponseObserved, result.HomepageRequestAttempted);
        Assert.AreEqual(Seed, result.Seed);
        Assert.AreEqual(Root.DestinationHostname, result.DestinationHostname);
        if (result.HomepageRequestAttempted) AssertRobotsAndRootRequests(proxy);
        else AssertOneRobotsRequest(proxy);
    }

    [TestMethod]
    [DataRow("User-agent: *\nDisallow: /\nUser-agent: I2PAtlas\nAllow: /", true)]
    [DataRow("User-agent: *\nAllow: /\nUser-agent: i2patlas\nDisallow: /", false)]
    [DataRow("User-agent: *\nDisallow: /\nAllow: /", true)]
    [DataRow("User-agent: *\nDisallow: /*$", false)]
    [DataRow("User-agent: *\nDisallow: /private", true)]
    [DataRow("\uFEFF# comment\nUser-agent: I2PAtlas\nDisallow:", true)]
    public async Task SharedPolicyEvaluatesCrawlerGroupsAndSpecificityWithoutAuthorizingHomepage(string body, bool allows)
    {
        await using var proxy = new FakeProxy(Response(200, body));
        using var transport = new I2pHttpTransport(proxy.Uri);
        var result = await new I2pHttpProbe(transport).ProbeAsync(Seed);
        Assert.AreEqual(allows ? I2pRobotsObservation.AllowsRoot : I2pRobotsObservation.DisallowsRoot, result.Robots);
        Assert.IsFalse(result.HomepageRequestAttempted);
        AssertOneRobotsRequest(proxy);
    }

    [TestMethod]
    [DataRow("Content-Type: text/html\r\n")]
    [DataRow("Content-Type: text/plain; charset=iso-8859-1\r\n")]
    [DataRow("Content-Encoding: gzip\r\n")]
    public async Task UnsupportedRobotsRepresentationIsUnknown(string headers)
    {
        await using var proxy = new FakeProxy(Response(200, "User-agent: *\nAllow: /", headers));
        using var transport = new I2pHttpTransport(proxy.Uri);
        var result = await new I2pHttpProbe(transport).ProbeAsync(Seed);
        Assert.AreEqual(I2pRobotsObservation.UnsupportedRepresentation, result.Robots);
        Assert.AreEqual(I2pHomepageDisposition.SkippedRobotsUnknown, result.Homepage);
        AssertOneRobotsRequest(proxy);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InvalidUtf8AndControlBytesAreMalformed(bool invalidUtf8)
    {
        var body = invalidUtf8 ? new byte[] { 0xC3, 0x28 } : Encoding.UTF8.GetBytes("User-agent: *\nAllow: /\0");
        await using var proxy = new FakeProxy(Response(200, body));
        using var transport = new I2pHttpTransport(proxy.Uri);
        Assert.AreEqual(I2pRobotsObservation.Malformed, (await new I2pHttpProbe(transport).ProbeAsync(Seed)).Robots);
        AssertOneRobotsRequest(proxy);
    }

    [TestMethod]
    [DataRow(I2pHttpProbe.MaxRobotsBytes, I2pRobotsObservation.AllowsRoot)]
    [DataRow(I2pHttpProbe.MaxRobotsBytes + 1, I2pRobotsObservation.TooLarge)]
    public async Task RobotsByteLimitIsExplicit(int length, I2pRobotsObservation expected)
    {
        await using var proxy = new FakeProxy(Response(200, new string(' ', length)));
        using var transport = new I2pHttpTransport(proxy.Uri);
        var result = await new I2pHttpProbe(transport).ProbeAsync(Seed);
        Assert.AreEqual(expected, result.Robots);
        Assert.AreEqual(length, result.RobotsResponse!.BodyBytes);
        Assert.IsFalse(result.HomepageRequestAttempted);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OversizedHttpIsNotMisreportedAsProxyUnreachable(bool headers)
    {
        var bytes = headers ? Response(200, "", "X-Large: " + new string('x', 17 * 1024) + "\r\n")
            : Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {I2pHttpTransport.MaxResponseBytes + 1}\r\n\r\n");
        await using var proxy = new FakeProxy(bytes);
        using var transport = new I2pHttpTransport(proxy.Uri);
        var result = await new I2pHttpProbe(transport).ProbeAsync(Seed);
        Assert.AreEqual(I2pRequestOutcome.TransportFailure, result.RequestOutcome);
        Assert.AreEqual(I2pHomepageDisposition.SkippedRequestFailed, result.Homepage);
        AssertOneRobotsRequest(proxy);
    }

    [TestMethod]
    [DataRow("http://friendly.i2p/")]
    [DataRow("http://other.i2p/")]
    [DataRow("http://example.com/")]
    [DataRow("http://friendly.i2p/?i2paddresshelper=changed")]
    [DataRow("/?%69%32%70addresshelper=changed")]
    public async Task RedirectsAndHelpersAreRecordedAndNeverFollowed(string location)
    {
        await using var proxy = new FakeProxy(Response(302, "", $"Location: {location}\r\n"));
        using var transport = new I2pHttpTransport(proxy.Uri);
        var result = await new I2pHttpProbe(transport).ProbeAsync(Seed);
        Assert.AreEqual(I2pRobotsObservation.Redirect, result.Robots);
        Assert.AreEqual(location, result.RobotsResponse!.RedirectLocation);
        Assert.IsFalse(result.RobotsResponse.RedirectLocationTruncated);
        Assert.AreEqual(I2pOriginEvidence.Unproven, result.OriginEvidence);
        AssertOneRobotsRequest(proxy);
    }

    [TestMethod]
    public async Task ResponseMetadataIsBounded()
    {
        await using var proxy = new FakeProxy(Response(302, "", "Location: /" + new string('x', 5000) +
            "\r\nContent-Type: text/plain; padding=" + new string('x', 300) + "\r\n"));
        using var transport = new I2pHttpTransport(proxy.Uri);
        var result = await new I2pHttpProbe(transport).ProbeAsync(Seed);
        Assert.AreEqual(I2pHttpProbe.MaxRedirectCharacters, result.RobotsResponse!.RedirectLocation!.Length);
        Assert.IsTrue(result.RobotsResponse.RedirectLocationTruncated);
        Assert.AreEqual(I2pHttpProbe.MaxContentTypeCharacters, result.RobotsResponse.ContentType!.Length);
        AssertOneRobotsRequest(proxy);
    }

    [TestMethod]
    public async Task ProbeConnectionsRemainPinnedToLiteralProxyWithNoDns()
    {
        await using var proxy = new FakeProxy(Response(200, "User-agent: *\nAllow: /"));
        var approved = I2pProxyEndpoint.Parse(proxy.Uri);
        var connections = new List<IPEndPoint>();
        using var dns = new NameResolutionObserver();
        using var transport = new I2pHttpTransport(approved, TimeSpan.FromSeconds(3), async (endpoint, token) =>
        {
            connections.Add(endpoint);
            var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try { await socket.ConnectAsync(endpoint, token); return new NetworkStream(socket, ownsSocket: true); }
            catch { socket.Dispose(); throw; }
        });
        await new I2pHttpProbe(transport).ProbeAsync(Seed);
        Assert.HasCount(1, connections);
        Assert.AreEqual(approved.Address, connections[0].Address.ToString());
        Assert.AreEqual(approved.Port, connections[0].Port);
        Assert.IsEmpty(dns.Hostnames);
        AssertOneRobotsRequest(proxy);
    }

    [TestMethod]
    public async Task HomepageMetadataIsInertAndDoesNotTreatProxyHtmlAsAHomepage()
    {
        const string html = """
            <base href="http://unapproved.i2p/">
            <title> A &amp; B
              site </title>
            <a href="/relative#fragment">relative</a>
            <a href="http://Other.I2P/path">I2P</a>
            <a href="https://example.com/path?a=1&amp;b=2#fragment">clearnet</a>
            <a href="http://other.i2p/?%69%32%70addresshelper=untrusted">helper</a>
            <a href="http://other.i2p/?I2PADDRESSHELPER=x">helper</a>
            <a href="http://user:secret@example.com/">credentials</a>
            <a href="http://127.0.0.1/">IP</a><a href="javascript:bad()">bad</a>
            <a href="http://bad_name.i2p/">bad host</a>
            """;
        await using var proxy = new FakeProxy(Response(200, html, "Content-Type: text/html\r\n"));
        using var transport = new I2pHttpTransport(proxy.Uri);
        var result = await new I2pHttpProbe(transport).ProbeAsync(Seed);
        Assert.AreEqual(I2pRobotsObservation.UnsupportedRepresentation, result.Robots);
        Assert.IsFalse(result.HomepageRequestAttempted);

        // Fixture-only extraction: there is deliberately no trusted-origin override
        // just to make the generic probe fetch a homepage in tests.
        var metadata = I2pPageMetadata.Read(Root, Encoding.UTF8.GetBytes(html), "text/html; charset=utf-8");
        Assert.AreEqual("A & B site", metadata.Title);
        Assert.AreEqual("text/html; charset=utf-8", metadata.ContentType);
        Assert.IsFalse(metadata.ExtractionIncomplete);
        Assert.HasCount(4, metadata.Links);
        Assert.Contains(new I2pDiscoveredLink("http://unapproved.i2p/", I2pDiscoveredLinkKind.I2p), metadata.Links);
        Assert.Contains(new I2pDiscoveredLink($"http://{Root.DestinationHostname}/relative", I2pDiscoveredLinkKind.I2p), metadata.Links);
        Assert.Contains(new I2pDiscoveredLink("http://other.i2p/path", I2pDiscoveredLinkKind.I2p), metadata.Links);
        Assert.Contains(new I2pDiscoveredLink("https://example.com/path?a=1&b=2", I2pDiscoveredLinkKind.Clearnet), metadata.Links);
        AssertOneRobotsRequest(proxy);
    }

    [TestMethod]
    public void HomepageBodiesTitlesLinksAndContentTypesAreBounded()
    {
        var prefix = "<title>" + new string('t', 1000) + "</title>";
        var body = prefix.PadRight(I2pPageMetadata.MaxBodyBytes, ' ') + "<a href='http://beyond.i2p/'>outside cap</a>";
        var metadata = I2pPageMetadata.Read(Root, Encoding.UTF8.GetBytes(body), "text/html; padding=" + new string('x', 1000));
        Assert.AreEqual(I2pPageMetadata.MaxBodyBytes, metadata.BodyBytesExamined);
        Assert.IsTrue(metadata.BodyTruncated);
        Assert.IsTrue(metadata.ExtractionIncomplete);
        Assert.AreEqual(I2pPageMetadata.MaxTitleCharacters, metadata.Title!.Length);
        Assert.AreEqual(I2pHttpProbe.MaxContentTypeCharacters, metadata.ContentType!.Length);
        Assert.IsEmpty(metadata.Links);
        var exact = I2pPageMetadata.Read(Root, new byte[I2pPageMetadata.MaxBodyBytes], "application/octet-stream");
        Assert.IsFalse(exact.BodyTruncated);
        Assert.IsNull(exact.Title);
    }

    [TestMethod]
    public void HomepageLinkDeduplicationOrderingAndLimitsAreDeterministic()
    {
        var html = "http://z.i2p/ <a href='http://a.i2p/'>a</a> http://z.i2p/";
        var metadata = I2pPageMetadata.Read(Root, Encoding.UTF8.GetBytes(html), "text/plain");
        CollectionAssert.AreEqual(new[] { "http://a.i2p/", "http://z.i2p/" }, metadata.Links.Select(l => l.Url).ToArray());
        html = string.Join(' ', Enumerable.Range(0, I2pPageMetadata.MaxLinks + 1).Select(i => $"http://host{i}.i2p/"));
        metadata = I2pPageMetadata.Read(Root, Encoding.UTF8.GetBytes(html), "text/html");
        Assert.HasCount(I2pPageMetadata.MaxLinks, metadata.Links);
        Assert.IsTrue(metadata.ExtractionIncomplete);
        html = string.Join(' ', Enumerable.Repeat("http://a.i2p/", 1001));
        metadata = I2pPageMetadata.Read(Root, Encoding.UTF8.GetBytes(html), null);
        Assert.HasCount(1, metadata.Links);
        Assert.IsTrue(metadata.ExtractionIncomplete);
        metadata = I2pPageMetadata.Read(Root, Encoding.UTF8.GetBytes("http://other.i2p/" + new string('x', 4096)), null);
        Assert.IsEmpty(metadata.Links);
    }

    [TestMethod]
    public void PureParsersHonorCallerCancellation()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            I2pPageMetadata.Read(Root, Encoding.UTF8.GetBytes("<title>x</title>"), "text/html", cancelled.Token));
        Assert.ThrowsExactly<OperationCanceledException>(() => RobotsPolicy.IsPathAllowed("", "I2PAtlas", "/", cancelled.Token));
    }

    private static void AssertOneRobotsRequest(FakeProxy proxy)
    {
        Assert.HasCount(1, proxy.Requests);
        var headers = proxy.Requests.Single();
        Assert.AreEqual($"GET http://{Root.DestinationHostname}/robots.txt HTTP/1.1", headers[0]);
        Assert.Contains($"Host: {Root.DestinationHostname}", headers);
        Assert.Contains("User-Agent: " + I2pHttpProbe.UserAgent, headers);
    }

    private static void AssertRobotsAndRootRequests(FakeProxy proxy)
    {
        Assert.HasCount(2, proxy.Requests);
        var requests = proxy.Requests.ToArray();
        Assert.AreEqual($"GET http://{Root.DestinationHostname}/robots.txt HTTP/1.1", requests[0][0]);
        Assert.AreEqual($"GET http://{Root.DestinationHostname}/ HTTP/1.1", requests[1][0]);
        foreach (var headers in requests)
        {
            Assert.Contains($"Host: {Root.DestinationHostname}", headers);
            Assert.Contains("User-Agent: " + I2pHttpProbe.UserAgent, headers);
        }
    }

    private static string Destination() => Convert.ToBase64String(new byte[387]).Replace('+', '-').Replace('/', '~');
    private static byte[] Response(int status, string body, string headers = "") => Response(status, Encoding.UTF8.GetBytes(body), headers);
    private static byte[] Response(int status, byte[] body, string headers = "") =>
        Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Test\r\nContent-Length: {body.Length}\r\nConnection: close\r\n{headers}\r\n")
            .Concat(body).ToArray();

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
        public TaskCompletionSource Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Uri { get; }

        public FakeProxy(byte[]? response, bool holdOpen = false)
        {
            listener.Start();
            Uri = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
            serving = ServeAsync(response, holdOpen);
        }

        private async Task ServeAsync(byte[]? response, bool holdOpen)
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
                    Received.TrySetResult();
                    if (response != null) await stream.WriteAsync(response, stopped.Token);
                    if (holdOpen) await Task.Delay(Timeout.InfiniteTimeSpan, stopped.Token);
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
