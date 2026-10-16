using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using I2PAtlas.AddressBook;
using I2PAtlas.I2pProbing;
using I2PAtlas.I2pTransport;

namespace I2PAtlas.Tests;

[TestClass]
public sealed class I2pHostProbeTests
{
    private const string Allow = "User-agent: *\nAllow: /";
    private const string Plain = "Content-Type: text/plain; charset=utf-8\r\n";
    private const string B32 = "afrg26l6uwxelohtn7mjqfskfzrm2dsckb6vgyvw4hnk7cgyfipq.b32.i2p";
    private static readonly I2pSeed Seed = new("foo.i2p", SeedSnapshotTestData.Destination());

    [TestMethod]
    [DataRow(" FOO.I2P.\t", "foo.i2p")]
    [DataRow("Sub.Example.I2P", "sub.example.i2p")]
    [DataRow("xn--bcher-kva.i2p", "xn--bcher-kva.i2p")]
    public void FriendlyTargetsNormalizeWithoutInventingDestinationMaterial(string input, string expected)
    {
        var target = new I2pProbeTarget.DiscoveredHost(input);
        Assert.AreEqual(expected, target.Hostname);
        Assert.AreEqual(I2pDiscoveredHostForm.Friendly, target.HostForm);
        Assert.AreEqual(expected, target.RequestHostname);
    }

    [TestMethod]
    [DataRow("")][DataRow(" ")][DataRow("i2p")][DataRow(".i2p")]
    [DataRow("example.com")][DataRow("foo.i2p.example.com")][DataRow("foo.i2p.alt")]
    [DataRow("localhost")][DataRow("localhost.localdomain")][DataRow("foo.localhost")]
    [DataRow("127.0.0.1")][DataRow("127.1")][DataRow("2130706433")]
    [DataRow("::1")][DataRow("[::1]")][DataRow("192.168.1.1")]
    [DataRow("http://foo.i2p")][DataRow("https://foo.i2p/")][DataRow("//foo.i2p")]
    [DataRow("foo.i2p/path")][DataRow("foo.i2p:80")][DataRow("foo.i2p:8080")]
    [DataRow("user@foo.i2p")][DataRow("foo.i2p?x=1")][DataRow("foo.i2p#x")]
    [DataRow("foo.i2p?i2paddresshelper=secret")][DataRow("foo.i2p?%69%32%70addresshelper=secret")]
    [DataRow("foo.i2p\\@example.com")][DataRow("foo.i2p\r\nHost: example.com")]
    [DataRow("foo_bar.i2p")][DataRow("-foo.i2p")][DataRow("foo-.i2p")]
    [DataRow("foo..i2p")][DataRow("foo.i2p..")][DataRow("fóo.i2p")][DataRow("fr%69endly.i2p")]
    [DataRow("foo.i2p\u00a0")][DataRow("foo\tbar.i2p")]
    public void UnsafeHostInputIsRejectedBeforeAnyConnectorOrDnsWork(string hostname)
    {
        using var dns = new NameResolutionObserver();
        var calls = 0;
        using var transport = new I2pHttpTransport(I2pProxyEndpoint.Parse("http://127.0.0.1:4444"),
            TimeSpan.FromSeconds(1), (_, _) => { calls++; throw new AssertFailedException("Unexpected connection."); });
        Assert.ThrowsExactly<FormatException>(() => new I2pProbeRequest(
            new I2pProbeTarget.DiscoveredHost(hostname), ProbeResource.Robots));
        Assert.AreEqual(0, calls);
        Assert.IsEmpty(dns.Hostnames);
    }

    [TestMethod]
    [DataRow("proxy.i2p")][DataRow("ROUTER.I2P.")][DataRow("console.i2p")]
    [DataRow("sub.proxy.i2p")][DataRow("a.b.router.i2p")][DataRow("sub.console.i2p")]
    public void RouterControlNamesAreRejectedAsANarrowRoutingRule(string hostname)
    {
        var error = Assert.ThrowsExactly<FormatException>(() => new I2pProbeTarget.DiscoveredHost(hostname));
        Assert.Contains("control", error.Message);
        Assert.AreEqual(I2pDiscoveredHostForm.Friendly, new I2pProbeTarget.DiscoveredHost("notproxy.i2p").HostForm);
        // This rule does not change destination-bound seed behavior.
        Assert.AreEqual(B32, new I2pProbeTarget.KnownSeed(new I2pSeed(hostname, Seed.Destination)).RequestHostname);
    }

    [TestMethod]
    [DataRow('a')][DataRow('q')]
    public void ClassicBase32IsCanonicalAndHasNoImportedDestination(char last)
    {
        var hostname = new string('a', 51) + last + ".b32.i2p";
        var target = new I2pProbeTarget.DiscoveredHost(hostname.ToUpperInvariant() + ".");
        Assert.AreEqual(hostname, target.Hostname);
        Assert.AreEqual(I2pDiscoveredHostForm.ClassicBase32, target.HostForm);
        Assert.AreEqual(hostname, new I2pProbeRequest(target, ProbeResource.Root).HostHeader);
    }

    [TestMethod]
    [DataRow("b32.i2p")]
    [DataRow("abc.b32.i2p")]
    [DataRow("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaab.b32.i2p")]
    [DataRow("0aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.b32.i2p")]
    [DataRow("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa1.b32.i2p")]
    [DataRow("sub.afrg26l6uwxelohtn7mjqfskfzrm2dsckb6vgyvw4hnk7cgyfipq.b32.i2p")]
    public void MalformedClassicBase32CannotFallBackToFriendlyRouting(string hostname)
    {
        Assert.ThrowsExactly<FormatException>(() => new I2pProbeTarget.DiscoveredHost(hostname));
    }

    [TestMethod]
    [DataRow(51)][DataRow(53)][DataRow(54)][DataRow(55)]
    public void UnsupportedBase32LengthsAreRejected(int length) =>
        Assert.ThrowsExactly<FormatException>(() => new I2pProbeTarget.DiscoveredHost(new string('a', length) + ".b32.i2p"));

    [TestMethod]
    [DataRow(56)][DataRow(60)][DataRow(63)]
    public void ExtendedBase32IsExplicitlyUnsupported(int length)
    {
        var error = Assert.ThrowsExactly<FormatException>(() => new I2pProbeTarget.DiscoveredHost(new string('a', length) + ".b32.i2p"));
        Assert.Contains("Extended/encrypted-lease-set", error.Message);
    }

    [TestMethod]
    public void DescriptorHasOnlyTwoResourcesAndImmutableMatchingAuthorityAndHost()
    {
        var target = new I2pProbeTarget.DiscoveredHost("foo.i2p");
        foreach (var resource in new[] { ProbeResource.Robots, ProbeResource.Root })
        {
            var request = new I2pProbeRequest(target, resource);
            Assert.AreEqual(resource == ProbeResource.Robots ? "/robots.txt" : "/", request.RequestUri.AbsolutePath);
            Assert.AreEqual(request.HostHeader, request.RequestUri.Authority);
            Assert.AreEqual("http", request.RequestUri.Scheme);
            Assert.AreEqual(80, request.RequestUri.Port);
            Assert.AreEqual("", request.RequestUri.Query + request.RequestUri.Fragment + request.RequestUri.UserInfo);
            var mutableCopy = new UriBuilder(request.RequestUri) { Host = "example.com", Path = "/other", Query = "secret" };
            Assert.AreEqual("foo.i2p", request.HostHeader);
            Assert.AreEqual("foo.i2p", request.RequestUri.Host);
            Assert.AreEqual("example.com", mutableCopy.Host);
        }
        foreach (var invalid in new[] { -1, 2, int.MaxValue })
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new I2pProbeRequest(target, (ProbeResource)invalid));
        Assert.ThrowsExactly<ArgumentNullException>(() => new I2pProbeRequest(null!, ProbeResource.Robots));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DiscoveredTargetsSendOnlyRobotsAndRootWithExactWireAuthorityAndNoDns(bool classic)
    {
        var host = classic ? B32 : "foo.i2p";
        await using var proxy = new FakeProxy(n => n == 1 ? Response(200, Allow, Plain) :
            Response(200, "<title>Root</title><a href='/inert?q=1#x'>link</a>", "Content-Type: text/html\r\n"));
        var endpoint = I2pProxyEndpoint.Parse(proxy.Uri);
        var connections = new List<IPEndPoint>();
        using var dns = new NameResolutionObserver();
        using var transport = new I2pHttpTransport(endpoint, TimeSpan.FromSeconds(3), async (actual, token) =>
        {
            connections.Add(actual);
            Assert.AreEqual(endpoint.Address, actual.Address.ToString());
            Assert.AreEqual(endpoint.Port, actual.Port);
            var socket = new Socket(actual.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try { await socket.ConnectAsync(actual, token); return new NetworkStream(socket, ownsSocket: true); }
            catch { socket.Dispose(); throw; }
        });
        var result = await new I2pHttpProbe(transport).ProbeTargetAsync(new I2pProbeTarget.DiscoveredHost(host));
        Assert.AreEqual("Root", result.HomepageMetadata!.Title);
        Assert.AreEqual($"http://{host}/inert?q=1", result.HomepageMetadata.Links.Single().Url);
        Assert.AreEqual(I2pOriginEvidence.Unproven, result.OriginEvidence);
        Assert.AreEqual(I2pOriginEvidence.Unproven, result.HomepageOriginEvidence);
        Assert.HasCount(2, connections);
        Assert.IsEmpty(dns.Hostnames);
        AssertRequests(proxy, host, 2);
    }

    [TestMethod]
    [DataRow(200, Allow, Plain, I2pProxyResponseClassification.LikelyOriginResponse, I2pRobotsObservation.AllowsRoot, true)]
    [DataRow(200, "User-agent: *\nDisallow: /", Plain, I2pProxyResponseClassification.LikelyOriginResponse, I2pRobotsObservation.DisallowsRoot, false)]
    [DataRow(200, "User-agent: *\nDisallow: nope", Plain, I2pProxyResponseClassification.Ambiguous, I2pRobotsObservation.Malformed, false)]
    [DataRow(200, Allow, "Content-Type: text/html\r\n", I2pProxyResponseClassification.Ambiguous, I2pRobotsObservation.UnsupportedRepresentation, false)]
    [DataRow(200, Allow, "", I2pProxyResponseClassification.Ambiguous, I2pRobotsObservation.AllowsRoot, false)]
    [DataRow(200, "", Plain, I2pProxyResponseClassification.Ambiguous, I2pRobotsObservation.AllowsRoot, false)]
    [DataRow(403, Allow, Plain, I2pProxyResponseClassification.Ambiguous, I2pRobotsObservation.UnknownStatus, false)]
    [DataRow(404, "", Plain, I2pProxyResponseClassification.Ambiguous, I2pRobotsObservation.UnknownStatus, false)]
    [DataRow(502, Allow, Plain, I2pProxyResponseClassification.LikelyProxyFailure, I2pRobotsObservation.UnknownStatus, false)]
    [DataRow(503, Allow, Plain, I2pProxyResponseClassification.LikelyProxyFailure, I2pRobotsObservation.UnknownStatus, false)]
    [DataRow(504, Allow, Plain, I2pProxyResponseClassification.LikelyProxyFailure, I2pRobotsObservation.UnknownStatus, false)]
    [DataRow(302, Allow, "Location: http://other.i2p/?i2paddresshelper=secret\r\n", I2pProxyResponseClassification.Ambiguous, I2pRobotsObservation.Redirect, false)]
    public async Task DiscoveredAndSeededProbesShareExactlyTheSameRobotsGate(int status, string body, string headers,
        I2pProxyResponseClassification classification, I2pRobotsObservation robots, bool attempted)
    {
        var response = Response(status, body, headers);
        await using var hostProxy = new FakeProxy(n => n == 1 ? response : Response(200, "root"));
        await using var seedProxy = new FakeProxy(n => n == 1 ? response : Response(200, "root"));
        using var hostTransport = new I2pHttpTransport(hostProxy.Uri);
        using var seedTransport = new I2pHttpTransport(seedProxy.Uri);
        var host = await new I2pHttpProbe(hostTransport).ProbeTargetAsync(new I2pProbeTarget.DiscoveredHost("foo.i2p"));
        var seeded = await new I2pHttpProbe(seedTransport).ProbeAsync(Seed);
        Assert.AreEqual(classification, host.ProxyResponseClassification);
        Assert.AreEqual(robots, host.Robots);
        Assert.AreEqual(attempted, host.HomepageRequestAttempted);
        Assert.AreEqual(seeded.RequestOutcome, host.RequestOutcome);
        Assert.AreEqual(seeded.OriginEvidence, host.OriginEvidence);
        Assert.AreEqual(seeded.RobotsResponse, host.RobotsResponse);
        Assert.AreEqual(seeded.Homepage, host.Homepage);
        Assert.AreEqual(seeded.HomepageRequestOutcome, host.HomepageRequestOutcome);
        Assert.AreEqual(seeded.HomepageProxyResponseClassification, host.HomepageProxyResponseClassification);
        Assert.AreEqual(seeded.HomepageOriginEvidence, host.HomepageOriginEvidence);
        AssertRequests(hostProxy, "foo.i2p", attempted ? 2 : 1);
        AssertRequests(seedProxy, B32, attempted ? 2 : 1);
    }

    [TestMethod]
    [DataRow(200, "")][DataRow(200, "Content-Encoding: gzip\r\n")]
    [DataRow(302, "Location: http://other.i2p/?i2paddresshelper=secret\r\n")]
    [DataRow(403, "")][DataRow(502, "")][DataRow(503, "")][DataRow(504, "")]
    public async Task HomepageMetadataStillRequiresHttp200WithoutContentEncoding(int status, string extraHeaders)
    {
        const string body = "<title>Root</title><a href='http://never-fetch.i2p/path'>inert</a>";
        await using var proxy = new FakeProxy(n => n == 1 ? Response(200, Allow, Plain) :
            Response(status, body, "Content-Type: text/html\r\n" + extraHeaders));
        using var transport = new I2pHttpTransport(proxy.Uri);
        var result = await new I2pHttpProbe(transport).ProbeTargetAsync(new I2pProbeTarget.DiscoveredHost("foo.i2p"));
        Assert.IsTrue(result.HomepageRequestAttempted);
        Assert.AreEqual(status, result.HomepageResponse!.StatusCode);
        Assert.AreEqual(I2pOriginEvidence.Unproven, result.HomepageOriginEvidence);
        Assert.AreEqual(status is 502 or 503 or 504 ? I2pProxyResponseClassification.LikelyProxyFailure :
            I2pProxyResponseClassification.Ambiguous, result.HomepageProxyResponseClassification);
        if (status == 200 && extraHeaders.Length == 0) Assert.AreEqual("Root", result.HomepageMetadata!.Title);
        else Assert.IsNull(result.HomepageMetadata);
        AssertRequests(proxy, "foo.i2p", 2); // Neither links, redirects nor helpers become requests.
    }

    [TestMethod]
    public async Task HostOnlyTransportIgnoresEnvironmentAndDefaultProxy()
    {
        var names = new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY", "http_proxy", "https_proxy", "all_proxy", "no_proxy" };
        var previous = names.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        var previousDefault = HttpClient.DefaultProxy;
        var trap = new RejectingProxy();
        try
        {
            foreach (var name in names) Environment.SetEnvironmentVariable(name,
                name.Equals("NO_PROXY", StringComparison.OrdinalIgnoreCase) ? "*" : "http://127.0.0.1:1");
            HttpClient.DefaultProxy = trap;
            await using var proxy = new FakeProxy(_ => Response(403, "denied"));
            using var dns = new NameResolutionObserver();
            using var transport = new I2pHttpTransport(proxy.Uri);
            var result = await new I2pHttpProbe(transport).ProbeTargetAsync(new I2pProbeTarget.DiscoveredHost("foo.i2p"));
            Assert.AreEqual(I2pRequestOutcome.ProxyHttpResponse, result.RequestOutcome);
            Assert.AreEqual(0, trap.Calls);
            Assert.IsEmpty(dns.Hostnames);
            AssertRequests(proxy, "foo.i2p", 1);
        }
        finally
        {
            HttpClient.DefaultProxy = previousDefault;
            foreach (var entry in previous) Environment.SetEnvironmentVariable(entry.Key, entry.Value);
        }
    }

    [TestMethod]
    public async Task RefusedProxyCannotFallBackToTheDiscoveredHost()
    {
        var proxy = I2pProxyEndpoint.Parse("http://127.0.0.1:4444");
        var endpoints = new List<IPEndPoint>();
        using var dns = new NameResolutionObserver();
        using var transport = new I2pHttpTransport(proxy, TimeSpan.FromSeconds(1), (endpoint, _) =>
        {
            endpoints.Add(endpoint);
            throw new SocketException((int)SocketError.ConnectionRefused);
        });
        var result = await new I2pHttpProbe(transport).ProbeTargetAsync(new I2pProbeTarget.DiscoveredHost("absent-from-snapshot.i2p"));
        Assert.AreEqual(I2pRequestOutcome.ProxyUnavailable, result.RequestOutcome);
        Assert.AreEqual(I2pOriginEvidence.NotObserved, result.OriginEvidence);
        Assert.IsFalse(result.HomepageRequestAttempted);
        Assert.IsNotEmpty(endpoints);
        Assert.IsTrue(endpoints.All(e => e.Address.Equals(IPAddress.Loopback) && e.Port == 4444));
        Assert.IsEmpty(dns.Hostnames);
    }

    [TestMethod]
    [DataRow(false)][DataRow(true)]
    public async Task ConnectDeadlineAndCallerCancellationRetainTheirDistinctSemantics(bool callerCancels)
    {
        var endpoint = I2pProxyEndpoint.Parse("http://127.0.0.1:4444");
        var connecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var dns = new NameResolutionObserver();
        using var stop = new CancellationTokenSource();
        using var transport = new I2pHttpTransport(endpoint,
            callerCancels ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(500), async (actual, token) =>
            {
                Assert.AreEqual(endpoint.Address, actual.Address.ToString());
                Assert.AreEqual(endpoint.Port, actual.Port);
                connecting.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new AssertFailedException("An interrupted connect cannot return a stream.");
            });
        var pending = new I2pHttpProbe(transport).ProbeTargetAsync(new I2pProbeTarget.DiscoveredHost("foo.i2p"), stop.Token);
        await connecting.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (callerCancels)
        {
            stop.Cancel();
            var error = await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
            Assert.AreEqual(stop.Token, error.CancellationToken);
        }
        else
        {
            var observation = await pending;
            Assert.AreEqual(I2pRequestOutcome.TimedOut, observation.RequestOutcome);
            Assert.IsFalse(observation.HomepageRequestAttempted);
        }
        Assert.IsEmpty(dns.Hostnames);
    }

    [TestMethod]
    [DataRow(false, false)][DataRow(false, true)][DataRow(true, false)][DataRow(true, true)]
    public async Task HostOnlyResponsesKeepTheSameHeaderAndBodyCaps(bool homepage, bool oversizedHeaders)
    {
        var phase = homepage ? 2 : 1;
        var oversized = oversizedHeaders
            ? Response(200, Allow, Plain + "X-Padding: " + new string('x', 17 * 1024) + "\r\n")
            : Response(200, new string('x', I2pHttpTransport.MaxResponseBytes + 1), Plain);
        await using var proxy = new FakeProxy(n => n < phase ? Response(200, Allow, Plain) : oversized);
        using var transport = new I2pHttpTransport(proxy.Uri);
        var observation = await new I2pHttpProbe(transport).ProbeTargetAsync(new I2pProbeTarget.DiscoveredHost("foo.i2p"));
        if (homepage)
        {
            Assert.AreEqual(I2pRequestOutcome.ProxyHttpResponse, observation.RequestOutcome);
            Assert.AreEqual(I2pRequestOutcome.TransportFailure, observation.HomepageRequestOutcome);
            Assert.IsNull(observation.HomepageResponse);
        }
        else
        {
            Assert.AreEqual(I2pRequestOutcome.TransportFailure, observation.RequestOutcome);
            Assert.IsNull(observation.RobotsResponse);
        }
        Assert.IsNull(observation.HomepageMetadata);
        AssertRequests(proxy, "foo.i2p", phase);
    }

    [TestMethod]
    [DataRow("duplicate-charset")][DataRow("range")][DataRow("encoded")]
    [DataRow("invalid-utf8")][DataRow("oversized")]
    public async Task HostOnlyRobotsRepresentationCannotWeakenTheHomepageGate(string failure)
    {
        var headers = failure switch
        {
            "duplicate-charset" => "Content-Type: text/plain; charset=utf-8; charset=iso-8859-1\r\n",
            "range" => Plain + "Content-Range: bytes 0-1/100\r\n",
            "encoded" => Plain + "Content-Encoding: gzip\r\n",
            _ => Plain
        };
        var response = failure == "invalid-utf8"
            ? Response(200, "!!", headers)[..^2].Concat(new byte[] { 0xc3, 0x28 }).ToArray()
            : Response(200, failure == "oversized" ? Allow.PadRight(I2pHttpProbe.MaxRobotsBytes + 1) : Allow, headers);
        await using var proxy = new FakeProxy(_ => response);
        using var transport = new I2pHttpTransport(proxy.Uri);
        var observation = await new I2pHttpProbe(transport).ProbeTargetAsync(new I2pProbeTarget.DiscoveredHost("foo.i2p"));
        Assert.AreEqual(I2pProxyResponseClassification.Ambiguous, observation.ProxyResponseClassification);
        Assert.IsFalse(observation.HomepageRequestAttempted);
        AssertRequests(proxy, "foo.i2p", 1);
    }

    [TestMethod]
    [DataRow(false, false)][DataRow(false, true)][DataRow(true, false)][DataRow(true, true)]
    public async Task RequestDeadlineStillCoversHeadersAndBodyInEitherPhase(bool homepage, bool partialBody)
    {
        var phase = homepage ? 2 : 1;
        var partial = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 100\r\nContent-Type: text/plain\r\n\r\nabc");
        await using var proxy = new FakeProxy(n => n < phase ? Response(200, Allow, Plain) : partialBody ? partial : null,
            holdAfterRequest: phase);
        using var transport = new I2pHttpTransport(proxy.Uri, TimeSpan.FromMilliseconds(500));
        var result = await new I2pHttpProbe(transport).ProbeTargetAsync(new I2pProbeTarget.DiscoveredHost("foo.i2p"));
        if (homepage)
        {
            Assert.AreEqual(I2pRequestOutcome.ProxyHttpResponse, result.RequestOutcome);
            Assert.AreEqual(I2pRobotsObservation.AllowsRoot, result.Robots);
            Assert.AreEqual(I2pRequestOutcome.TimedOut, result.HomepageRequestOutcome);
            Assert.AreEqual(I2pHomepageDisposition.RequestFailed, result.Homepage);
            Assert.AreEqual(I2pOriginEvidence.NotObserved, result.HomepageOriginEvidence);
        }
        else
        {
            Assert.AreEqual(I2pRequestOutcome.TimedOut, result.RequestOutcome);
            Assert.AreEqual(I2pOriginEvidence.NotObserved, result.OriginEvidence);
            Assert.IsFalse(result.HomepageRequestAttempted);
        }
        AssertRequests(proxy, "foo.i2p", phase);
    }

    [TestMethod]
    [DataRow(false)][DataRow(true)]
    public async Task CallerCancellationPropagatesItsTokenInEitherPhase(bool homepage)
    {
        var phase = homepage ? 2 : 1;
        await using var proxy = new FakeProxy(n => n < phase ? Response(200, Allow, Plain) : null);
        using var transport = new I2pHttpTransport(proxy.Uri, TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var pending = new I2pHttpProbe(transport).ProbeTargetAsync(new I2pProbeTarget.DiscoveredHost("foo.i2p"), cancellation.Token);
        await (homepage ? proxy.SecondRequest.Task : proxy.FirstRequest.Task).WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        var error = await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        Assert.AreEqual(cancellation.Token, error.CancellationToken);
        AssertRequests(proxy, "foo.i2p", phase);
    }

    [TestMethod]
    public async Task HomepageRefusalPreservesRobotsWithoutNameOrDirectFallback()
    {
        await using var proxy = new FakeProxy(_ => Response(200, Allow, Plain), stopAfterFirst: true);
        using var transport = new I2pHttpTransport(proxy.Uri);
        var result = await new I2pHttpProbe(transport).ProbeTargetAsync(new I2pProbeTarget.DiscoveredHost("foo.i2p"));
        Assert.AreEqual(I2pProxyResponseClassification.LikelyOriginResponse, result.ProxyResponseClassification);
        Assert.AreEqual(I2pRobotsObservation.AllowsRoot, result.Robots);
        Assert.AreEqual(I2pRequestOutcome.ProxyUnavailable, result.HomepageRequestOutcome);
        Assert.IsTrue(result.HomepageRequestAttempted);
        AssertRequests(proxy, "foo.i2p", 1);
    }

    [TestMethod]
    public async Task NullSeedAndPreCancelledTargetPreserveExistingContractWithoutConnecting()
    {
        var calls = 0;
        using var transport = new I2pHttpTransport(I2pProxyEndpoint.Parse("http://127.0.0.1:4444"),
            TimeSpan.FromSeconds(1), (_, _) => { calls++; throw new AssertFailedException("Unexpected connection."); });
        var probe = new I2pHttpProbe(transport);
        var result = await probe.ProbeAsync(null); // Must remain unambiguous.
        Assert.AreEqual(I2pRequestOutcome.RejectedBeforeNetworking, result.RequestOutcome);
        Assert.IsNull(result.Seed);
        Assert.IsNull(result.DestinationHostname);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => probe.ProbeAsync(null, stop.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => probe.ProbeTargetAsync(new I2pProbeTarget.DiscoveredHost("foo.i2p"), stop.Token));
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public async Task SeededRelativeLinkMetadataMatchesPreRefactorGoldenValuesAndSerializedBytes()
    {
        const string html = "<title> A &amp; B\n site </title><a href='/relative?q=1#x'>r</a>" +
            "<a href='child'>c</a><a href='//OTHER.I2P./path#x'>other</a>" +
            "<a href='https://example.com/path?q=1#x'>external</a>" +
            "<a href='http://other.i2p/?%69%32%70addresshelper=secret'>helper</a>";
        var bytes = Encoding.UTF8.GetBytes(html);
        const string contentType = "text/html; charset=utf-8";
        var expected = new I2pPageMetadata(contentType, bytes.Length, false, "A & B site",
            [new($"http://{B32}/child", I2pDiscoveredLinkKind.I2p),
             new($"http://{B32}/relative?q=1", I2pDiscoveredLinkKind.I2p),
             new("http://other.i2p./path", I2pDiscoveredLinkKind.I2p),
             new("https://example.com/path?q=1", I2pDiscoveredLinkKind.Clearnet)], false);
        var expectedBytes = JsonSerializer.SerializeToUtf8Bytes(expected);
        var legacy = I2pPageMetadata.Read(new I2pHttpRequestTarget(Seed, "http://foo.i2p/"), bytes, contentType);
        var descriptor = I2pPageMetadata.Read(new I2pProbeRequest(new I2pProbeTarget.KnownSeed(Seed), ProbeResource.Root), bytes, contentType);
        CollectionAssert.AreEqual(expectedBytes, JsonSerializer.SerializeToUtf8Bytes(legacy));
        CollectionAssert.AreEqual(expectedBytes, JsonSerializer.SerializeToUtf8Bytes(descriptor));
        await using var proxy = new FakeProxy(n => n == 1 ? Response(200, Allow, Plain) :
            Response(200, html, "Content-Type: " + contentType + "\r\n"));
        using var transport = new I2pHttpTransport(proxy.Uri);
        var result = await new I2pHttpProbe(transport).ProbeAsync(Seed);
        Assert.AreSame(Seed, result.Seed);
        Assert.AreEqual(B32, result.DestinationHostname);
        CollectionAssert.AreEqual(expectedBytes, JsonSerializer.SerializeToUtf8Bytes(result.HomepageMetadata));
        AssertRequests(proxy, B32, 2);
    }

    private static void AssertRequests(FakeProxy proxy, string authority, int count)
    {
        var requests = proxy.Requests.ToArray();
        Assert.HasCount(count, requests);
        for (var i = 0; i < count; i++)
        {
            Assert.AreEqual($"GET http://{authority}/{(i == 0 ? "robots.txt" : "")} HTTP/1.1", requests[i][0]);
            Assert.Contains("Host: " + authority, requests[i]);
            Assert.Contains("User-Agent: I2PAtlas/0.1", requests[i]);
            Assert.IsFalse(requests[i].Any(line => line.StartsWith("Referer:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase)));
        }
    }

    private static byte[] Response(int status, string body, string headers = "") => Encoding.UTF8.GetBytes(
        $"HTTP/1.1 {status} Test\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n{headers}\r\n{body}");

    private sealed class RejectingProxy : IWebProxy
    {
        public int Calls { get; private set; }
        public ICredentials? Credentials { get; set; }
        public Uri GetProxy(Uri destination) { Calls++; throw new AssertFailedException("Ambient proxy consulted."); }
        public bool IsBypassed(Uri host) { Calls++; throw new AssertFailedException("Ambient bypass consulted."); }
    }

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
        public string Uri { get; }
        public ConcurrentQueue<string[]> Requests { get; } = new();
        public TaskCompletionSource FirstRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeProxy(Func<int, byte[]?> responses, int holdAfterRequest = 0, bool stopAfterFirst = false)
        {
            listener.Start();
            Uri = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
            serving = ServeAsync(responses, holdAfterRequest, stopAfterFirst);
        }

        private async Task ServeAsync(Func<int, byte[]?> responses, int holdAfterRequest, bool stopAfterFirst)
        {
            try
            {
                while (!stopped.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(stopped.Token);
                    using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                    var lines = new List<string>();
                    var size = 0;
                    while (await reader.ReadLineAsync(stopped.Token) is { Length: > 0 } line)
                    {
                        size += line.Length;
                        if (size > 8192) throw new AssertFailedException("Unexpectedly large request.");
                        lines.Add(line);
                    }
                    Requests.Enqueue(lines.ToArray());
                    var count = Requests.Count;
                    if (count == 1) FirstRequest.TrySetResult();
                    if (count == 2) SecondRequest.TrySetResult();
                    var response = responses(count);
                    if (stopAfterFirst) listener.Stop();
                    if (response != null)
                    {
                        try { await stream.WriteAsync(response, stopped.Token); }
                        catch (IOException) when (response.Length > I2pHttpTransport.MaxResponseBytes) { }
                    }
                    if (response == null || count == holdAfterRequest) await Task.Delay(Timeout.InfiniteTimeSpan, stopped.Token);
                    if (stopAfterFirst) break;
                }
            }
            catch (Exception ex) when (stopped.IsCancellationRequested &&
                ex is OperationCanceledException or SocketException or ObjectDisposedException or IOException or InvalidOperationException) { }
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
