using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Net;
using System.Net.Sockets;
using System.Text;
using I2PAtlas.AddressBook;
using I2PAtlas.I2pTransport;

namespace I2PAtlas.Tests;

[TestClass]
public sealed class I2pHttpTransportTests
{
    private static readonly I2pSeed Seed = new("Friendly.I2P.", MakeDestination());
    private const string BoundHost = "afrg26l6uwxelohtn7mjqfskfzrm2dsckb6vgyvw4hnk7cgyfipq.b32.i2p";

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow("127.0.0.1:4444")]
    [DataRow("http://127.0.0.1")]
    [DataRow("http://127.0.0.1:0")]
    [DataRow("http://127.0.0.1:65536")]
    [DataRow("http://127.0.0.1:-1")]
    [DataRow("http://127.0.0.1:+4444")]
    [DataRow("http://127.0.0.1:bad")]
    [DataRow("http://localhost:4444")]
    [DataRow("http://proxy.i2p:4444")]
    [DataRow("http://proxy.example:4444")]
    [DataRow("http://192.0.2.1:4444")]
    [DataRow("http://0.0.0.0:4444")]
    [DataRow("http://[::]:4444")]
    [DataRow("http://[2001:db8::1]:4444")]
    [DataRow("http://[::ffff:127.0.0.1]:4444")]
    [DataRow("http://[::1%1]:4444")]
    [DataRow("http://::1:4444")]
    [DataRow("https://127.0.0.1:4444")]
    [DataRow("socks5://127.0.0.1:4444")]
    [DataRow("http://user:pass@127.0.0.1:4444")]
    [DataRow("http://@127.0.0.1:4444")]
    [DataRow("http://127.0.0.1:4444/path")]
    [DataRow("http://127.0.0.1:4444//")]
    [DataRow("http://127.0.0.1:4444?")]
    [DataRow("http://127.0.0.1:4444#")]
    [DataRow("http://127.0.0.1:4444\r\n")]
    [DataRow(" http://127.0.0.1:4444")]
    [DataRow("http://127.1:4444")]
    [DataRow("http://2130706433:4444")]
    [DataRow("http://0x7f000001:4444")]
    public void MissingOrUnsafeProxyConfigurationIsRejected(string? endpoint)
    {
        Assert.ThrowsExactly<I2pProxyConfigurationException>(() => I2pProxyEndpoint.Parse(endpoint));
        Assert.ThrowsExactly<I2pProxyConfigurationException>(() => new I2pHttpTransport(endpoint));
    }

    [TestMethod]
    [DataRow("http://127.0.0.1:4444", "127.0.0.1")]
    [DataRow("HTTP://127.0.0.2:4444/", "127.0.0.2")]
    [DataRow("http://[::1]:4444", "::1")]
    [DataRow("http://[0:0:0:0:0:0:0:1]:4444/", "::1")]
    public void LiteralLoopbackConfigurationIsImmutable(string value, string expected)
    {
        var proxy = I2pProxyEndpoint.Parse(value);
        Assert.AreEqual(expected, proxy.Address);
        Assert.AreEqual(4444, proxy.Port);
        var copy = proxy.CreateEndPoint();
        copy.Address = IPAddress.Parse("192.0.2.1");
        copy.Port = 80;
        Assert.AreEqual(IPAddress.Parse(expected), proxy.CreateEndPoint().Address);
        Assert.AreEqual(4444, proxy.CreateEndPoint().Port);
        Assert.IsTrue(typeof(I2pProxyEndpoint).GetProperties().All(p => p.SetMethod == null));
    }

    [TestMethod]
    [DataRow(0, "afrg26l6uwxelohtn7mjqfskfzrm2dsckb6vgyvw4hnk7cgyfipq.b32.i2p")]
    [DataRow(4, "x7ufwbcphjcpysxhtq2c7i7rrhhguu75ggss2ent3oazgh4rjx4a.b32.i2p")]
    [DataRow(300, "nq6igiepkxnfczohw7civqevfuy765bjflddke25xbi35yhbsmla.b32.i2p")]
    public void BindingHashesBinaryDestinationAndPreservesSeedText(int payloadLength, string expected)
    {
        // Expected vectors independently computed with Node SHA-256 and bit-string Base32 encoding.
        var destination = MakeDestination(payloadLength);
        var seed = new I2pSeed("Friendly.I2P.", destination);
        Assert.AreEqual(("friendly.i2p", expected), I2pSeedIdentity.FromSeed(seed));
        var target = new I2pHttpRequestTarget(seed, "HTTP://FRIENDLY.I2P.:80/test%20path");
        Assert.AreEqual(expected, target.DestinationHostname);
        Assert.AreEqual("http://" + expected + "/test%20path", target.RequestUri.AbsoluteUri);
        Assert.AreEqual("friendly.i2p", target.FriendlyHostname);
        Assert.AreSame(seed, target.Seed);
        Assert.AreEqual(destination, seed.Destination);
        Assert.AreEqual(expected, new I2pHttpRequestTarget(
            new I2pSeed("alias.i2p", destination.TrimEnd('=')), "http://alias.i2p/test").DestinationHostname);
        Assert.AreEqual(expected, new I2pHttpRequestTarget(seed, "http://" + expected + "/test").DestinationHostname);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("/relative")]
    [DataRow("//friendly.i2p/test")]
    [DataRow("http://example.com/test")]
    [DataRow("http://friendly.i2p.evil.example/test")]
    [DataRow("http://other.i2p/test")]
    [DataRow("http://127.0.0.1/test")]
    [DataRow("http://[::1]/test")]
    [DataRow("http://[fd42::1]/test")]
    [DataRow("https://friendly.i2p/test")]
    [DataRow("ftp://friendly.i2p/test")]
    [DataRow("http://friendly.i2p:8080/test")]
    [DataRow("http://friendly.i2p:080/test")]
    [DataRow("http://friendly.i2p:/test")]
    [DataRow("http://user@friendly.i2p/test")]
    [DataRow("http://@friendly.i2p/test")]
    [DataRow("http://friendly.i2p@clearnet.example/test")]
    [DataRow("http://friendly.i2p/test#fragment")]
    [DataRow("http://friendly.i2p/test#")]
    [DataRow("http://friendly.i2p/test?")]
    [DataRow("http://friendly.i2p/test?i2paddresshelper=other")]
    [DataRow("http://friendly.i2p/test?%69%32%70addresshelper=other")]
    [DataRow("http://friendly.i2p\\@example.com/test")]
    [DataRow("http://friendly.i2p/has space")]
    [DataRow("http://friendly.i2p/\r\nHost: example.com")]
    [DataRow("http://friendly.i2p../test")]
    [DataRow("http://fr%69endly.i2p/test")]
    [DataRow("http://frıendly.i2p/test")]
    public void UnapprovedTargetsAreRejectedBeforeAnyConnection(string url)
    {
        var calls = 0;
        using var transport = new I2pHttpTransport(I2pProxyEndpoint.Parse("http://127.0.0.1:4444"),
            TimeSpan.FromSeconds(1), (_, _) => { calls++; throw new AssertFailedException("Must not connect."); });
        Assert.ThrowsExactly<ArgumentException>(() => new I2pHttpRequestTarget(Seed, url));
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public void TargetLimitsAndAnotherDestinationIdentityAreRejected()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new I2pHttpRequestTarget(Seed,
            "http://friendly.i2p/" + new string('x', I2pHttpRequestTarget.MaxUrlCharacters)));
        var other = new I2pSeed("friendly.i2p", MakeDestination(salt: 1));
        Assert.ThrowsExactly<ArgumentException>(() => new I2pHttpRequestTarget(other, "http://" + BoundHost + "/test"));
        Assert.ThrowsExactly<ArgumentNullException>(() => new I2pHttpRequestTarget(null!, "http://friendly.i2p/test"));
        Assert.ThrowsExactly<ArgumentNullException>(() => new I2pHttpRequestTarget(Seed, null!));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ValidTargetUsesOnlyLiteralConfiguredProxyAndDoesNotInvokeOsDns(bool ipv6)
    {
        if (ipv6 && !Socket.OSSupportsIPv6) { Assert.Inconclusive("IPv6 is unavailable."); return; }
        await using var proxy = new FakeProxy(Response(200, "proxy response"), ipv6: ipv6);
        using var dns = new NameResolutionObserver();
        using var transport = new I2pHttpTransport(proxy.Uri);
        var target = new I2pHttpRequestTarget(Seed, "http://friendly.i2p:80/test%20path");
        using var response = await transport.GetAsync(target).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("proxy response", await response.Content.ReadAsStringAsync());
        var captured = proxy.Requests.Single();
        Assert.AreEqual($"GET http://{BoundHost}/test%20path HTTP/1.1", captured[0]);
        Assert.Contains($"Host: {BoundHost}", captured);
        Assert.IsFalse(captured.Any(line => line.Contains("friendly.i2p", StringComparison.OrdinalIgnoreCase)));
        Assert.HasCount(1, proxy.Requests);
        Assert.IsEmpty(dns.Hostnames, "Even the proxy is connected by literal IP; no OS name resolution is needed.");
    }

    [TestMethod]
    public async Task EnvironmentAndDefaultProxyCannotChangeOrBypassRouting()
    {
        var names = new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY", "http_proxy", "https_proxy", "all_proxy", "no_proxy" };
        var previous = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var defaultProxy = HttpClient.DefaultProxy;
        var trap = new RejectingProxy();
        try
        {
            foreach (var name in names)
                Environment.SetEnvironmentVariable(name, name.Equals("NO_PROXY", StringComparison.OrdinalIgnoreCase) ? "*" : "http://127.0.0.1:1");
            HttpClient.DefaultProxy = trap;
            await using var proxy = new FakeProxy(Response(200, "configured proxy"));
            using var transport = new I2pHttpTransport(proxy.Uri);
            using var response = await transport.GetAsync(Target()).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual("configured proxy", await response.Content.ReadAsStringAsync());
            Assert.HasCount(1, proxy.Requests);
            Assert.AreEqual(0, trap.Calls);
        }
        finally
        {
            HttpClient.DefaultProxy = defaultProxy;
            foreach (var pair in previous) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }

    [TestMethod]
    public async Task OnlyTheConfiguredEndpointReachesConnectorAndFailureHasNoDirectFallback()
    {
        var proxy = I2pProxyEndpoint.Parse("http://127.0.0.1:4444");
        var endpoints = new List<IPEndPoint>();
        using var dns = new NameResolutionObserver();
        using var transport = new I2pHttpTransport(proxy, TimeSpan.FromSeconds(2), (endpoint, _) =>
        {
            endpoints.Add(endpoint);
            throw new SocketException((int)SocketError.ConnectionRefused);
        });
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => transport.GetAsync(Target()));
        Assert.IsTrue(Exceptions(error).OfType<SocketException>().Any(e => e.SocketErrorCode == SocketError.ConnectionRefused));
        Assert.HasCount(1, endpoints);
        Assert.AreEqual(new IPEndPoint(IPAddress.Loopback, 4444), endpoints[0]);
        Assert.IsEmpty(dns.Hostnames);
    }

    [TestMethod]
    public async Task RealProxyConnectionRefusalIsPreservedAsConnectionFailure()
    {
        // Reserve a port without listening: the kernel refuses connections, with no port-reuse race.
        using var reserved = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        reserved.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = (IPEndPoint)reserved.LocalEndPoint!;
        using var transport = new I2pHttpTransport($"http://127.0.0.1:{endpoint.Port}");
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => transport.GetAsync(Target()));
        Assert.IsTrue(Exceptions(error).OfType<SocketException>().Any(e => e.SocketErrorCode == SocketError.ConnectionRefused));
    }

    [TestMethod]
    [DataRow(301, "http://example.com/test")]
    [DataRow(302, "http://other.i2p/test")]
    [DataRow(303, "/another-path")]
    [DataRow(307, "https://friendly.i2p/test")]
    [DataRow(308, "http://friendly.i2p/test")]
    public async Task RedirectsAreReturnedAndNeverFollowed(int code, string location)
    {
        await using var proxy = new FakeProxy(Response(code, "redirect", $"Location: {location}\r\n"));
        using var transport = new I2pHttpTransport(proxy.Uri);
        using var response = await transport.GetAsync(Target()).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(code, (int)response.StatusCode);
        Assert.AreEqual(location, response.Headers.Location!.OriginalString);
        Assert.HasCount(1, proxy.Requests);
    }

    [TestMethod]
    [DataRow(200)]
    [DataRow(403)]
    [DataRow(407)]
    [DataRow(502)]
    [DataRow(503)]
    public async Task ProxyPagesAreUnclassifiedResponsesRatherThanDestinationReachability(int code)
    {
        await using var proxy = new FakeProxy(Response(code, "I2P proxy generated page"));
        using var transport = new I2pHttpTransport(proxy.Uri);
        using var response = await transport.GetAsync(Target());
        Assert.AreEqual(code, (int)response.StatusCode);
        Assert.AreEqual("I2P proxy generated page", await response.Content.ReadAsStringAsync());
        Assert.HasCount(1, proxy.Requests);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task DeadlineAndCallerCancellationCoverBothHeadersAndBody(bool partialBody, bool callerCancels)
    {
        await using var proxy = new FakeProxy(partialBody
            ? "HTTP/1.1 200 OK\r\nContent-Length: 10\r\n\r\nx" : null, holdOpen: true);
        using var transport = new I2pHttpTransport(proxy.Uri,
            callerCancels ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(300));
        using var cancellation = new CancellationTokenSource();
        var pending = transport.GetAsync(Target(), cancellation.Token);
        await proxy.Received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (callerCancels)
        {
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(3)));
        }
        else
        {
            var error = await Assert.ThrowsExactlyAsync<TimeoutException>(() => pending.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.IsInstanceOfType<OperationCanceledException>(error.InnerException);
        }
        Assert.HasCount(1, proxy.Requests);
    }

    [TestMethod]
    public async Task CancellationAndTimeoutDuringConnectDoNotTryAnotherEndpoint()
    {
        foreach (var callerCancels in new[] { false, true })
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            using var cancellation = new CancellationTokenSource();
            using var transport = new I2pHttpTransport(I2pProxyEndpoint.Parse("http://127.0.0.1:4444"),
                callerCancels ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(100), async (endpoint, token) =>
                {
                    Assert.AreEqual(new IPEndPoint(IPAddress.Loopback, 4444), endpoint);
                    calls++;
                    started.SetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    throw new AssertFailedException("Canceled connector must not return.");
                });
            var pending = transport.GetAsync(Target(), cancellation.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            if (callerCancels)
            {
                cancellation.Cancel();
                await Assert.ThrowsAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(3)));
            }
            else await Assert.ThrowsExactlyAsync<TimeoutException>(() => pending.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.AreEqual(1, calls);
        }
    }

    [TestMethod]
    public async Task ApprovedTargetCannotBeChangedThroughUriCopiesAndTransportIsReusableWithoutCookies()
    {
        await using var proxy = new FakeProxy(Response(200, "ok", $"Set-Cookie: tracking=1; Domain={BoundHost}; Path=/\r\n"));
        using var transport = new I2pHttpTransport(proxy.Uri);
        var target = Target();
        var copiedUri = new UriBuilder(target.RequestUri) { Host = "clearnet.example" };
        Assert.AreEqual("clearnet.example", copiedUri.Host);
        Assert.IsTrue(typeof(I2pHttpRequestTarget).GetProperties().All(p => p.SetMethod == null));
        for (var i = 0; i < 2; i++)
        {
            using var response = await transport.GetAsync(target);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }
        Assert.HasCount(2, proxy.Requests);
        foreach (var request in proxy.Requests)
        {
            Assert.AreEqual($"GET http://{BoundHost}/test HTTP/1.1", request[0]);
            Assert.IsFalse(request.Any(line => line.StartsWith("Cookie:") || line.StartsWith("Authorization:") || line.StartsWith("Proxy-Authorization:")));
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OversizedProxyResponsesFailWithinTransportLimits(bool oversizedHeaders)
    {
        var responseText = oversizedHeaders
            ? Response(200, "", "X-Large: " + new string('x', 17 * 1024) + "\r\n")
            : $"HTTP/1.1 200 OK\r\nContent-Length: {I2pHttpTransport.MaxResponseBytes + 1}\r\nConnection: close\r\n\r\n";
        await using var proxy = new FakeProxy(responseText);
        using var transport = new I2pHttpTransport(proxy.Uri);
        await Assert.ThrowsAsync<HttpRequestException>(() => transport.GetAsync(Target()));
        Assert.HasCount(1, proxy.Requests);
    }

    [TestMethod]
    public async Task NullTargetsPreCancellationAndDisposalDoNotConnect()
    {
        var calls = 0;
        using var transport = new I2pHttpTransport(I2pProxyEndpoint.Parse("http://127.0.0.1:4444"), TimeSpan.FromSeconds(1),
            (_, _) => { calls++; throw new AssertFailedException("Must not connect."); });
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => transport.GetAsync(null!));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => transport.GetAsync(Target(), cancellation.Token));
        transport.Dispose();
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => transport.GetAsync(Target()));
        Assert.AreEqual(0, calls);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new I2pHttpTransport("http://127.0.0.1:4444", TimeSpan.Zero));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new I2pHttpTransport("http://127.0.0.1:4444", Timeout.InfiniteTimeSpan));
    }

    private static I2pHttpRequestTarget Target() => new(Seed, "http://friendly.i2p/test");

    private static string MakeDestination(int payloadLength = 0, int salt = 0)
    {
        var bytes = new byte[387 + payloadLength];
        for (var i = 0; i < 384; i++) bytes[i] = (byte)(i * 73 + salt);
        if (payloadLength > 0)
        {
            bytes[384] = 5;
            bytes[385] = (byte)(payloadLength >> 8);
            bytes[386] = (byte)payloadLength;
            bytes[388] = 7;
        }
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '~');
    }

    private static string Response(int code, string body, string headers = "") =>
        $"HTTP/1.1 {code} Test\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n{headers}\r\n{body}";

    private static IEnumerable<Exception> Exceptions(Exception error)
    {
        for (Exception? current = error; current != null; current = current.InnerException) yield return current;
    }

    private sealed class RejectingProxy : IWebProxy
    {
        public int Calls { get; private set; }
        public ICredentials? Credentials { get; set; }
        public Uri GetProxy(Uri destination) { Calls++; throw new AssertFailedException("Default proxy was consulted."); }
        public bool IsBypassed(Uri host) { Calls++; throw new AssertFailedException("Default proxy bypass was consulted."); }
    }

    private sealed class NameResolutionObserver : EventListener
    {
        public ConcurrentQueue<string> Hostnames { get; } = new();
        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "System.Net.NameResolution") EnableEvents(eventSource, EventLevel.Verbose);
        }
        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventName == "ResolutionStart" && eventData.Payload?.FirstOrDefault() is string host)
                Hostnames.Enqueue(host);
        }
    }

    private sealed class FakeProxy : IAsyncDisposable
    {
        private readonly TcpListener listener;
        private readonly CancellationTokenSource stopped = new();
        private readonly Task serving;
        public ConcurrentQueue<string[]> Requests { get; } = new();
        public TaskCompletionSource Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Uri { get; }

        public FakeProxy(string? response, bool holdOpen = false, bool ipv6 = false)
        {
            listener = new TcpListener(ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback, 0);
            listener.Start();
            Uri = $"http://{(ipv6 ? "[::1]" : "127.0.0.1")}:{((IPEndPoint)listener.LocalEndpoint).Port}";
            serving = ServeAsync(response, holdOpen);
        }

        private async Task ServeAsync(string? response, bool holdOpen)
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
                    if (response != null) await stream.WriteAsync(Encoding.UTF8.GetBytes(response), stopped.Token);
                    if (holdOpen) await Task.Delay(Timeout.InfiniteTimeSpan, stopped.Token);
                }
            }
            catch (OperationCanceledException) when (stopped.IsCancellationRequested) { }
            catch (SocketException) when (stopped.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (stopped.IsCancellationRequested) { }
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
