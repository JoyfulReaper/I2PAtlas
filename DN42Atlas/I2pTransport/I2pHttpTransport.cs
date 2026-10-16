using System.Net;
using System.Net.Sockets;

namespace DN42Atlas.I2pTransport;

/// <summary>Reusable proxy-only GET transport. A response is a proxy observation, not destination reachability evidence.</summary>
public sealed class I2pHttpTransport : IDisposable
{
    public const int MaxResponseBytes = 256 * 1024;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);
    private readonly HttpClient client;
    private readonly TimeSpan requestTimeout;

    public I2pHttpTransport(string? proxyUri, TimeSpan? requestTimeout = null)
        : this(I2pProxyEndpoint.Parse(proxyUri), requestTimeout ?? DefaultTimeout, ConnectProxyAsync) { }

    // The only connection seam is internal for tests; callers cannot supply a handler,
    // mutable request, arbitrary headers, proxy overrides or a destination connector.
    internal I2pHttpTransport(I2pProxyEndpoint proxy, TimeSpan requestTimeout,
        Func<IPEndPoint, CancellationToken, ValueTask<Stream>> connect)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        ArgumentNullException.ThrowIfNull(connect);
        if (requestTimeout <= TimeSpan.Zero || requestTimeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        this.requestTimeout = requestTimeout;
        var handler = new SocketsHttpHandler
        {
            UseProxy = true,
            Proxy = new FixedProxy(proxy.Uri),
            AllowAutoRedirect = false,
            UseCookies = false,
            Credentials = null,
            DefaultProxyCredentials = null,
            PreAuthenticate = false,
            AutomaticDecompression = DecompressionMethods.None,
            MaxResponseHeadersLength = 16,
            ConnectCallback = (context, token) =>
            {
                var endpoint = proxy.CreateEndPoint();
                // If handler behavior ever requests a direct/non-proxy connection, fail
                // before DNS/socket work. The connector always receives the fixed IP.
                if (context.DnsEndPoint.Port != endpoint.Port ||
                    !IPAddress.TryParse(context.DnsEndPoint.Host, out var address) ||
                    !address.Equals(endpoint.Address))
                    throw new HttpRequestException("Unexpected I2P proxy connection endpoint.");
                return connect(endpoint, token);
            }
        };
        client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            MaxResponseContentBufferSize = MaxResponseBytes
        };
    }

    /// <summary>Returns all proxy status codes unchanged. Caller owns the buffered response and must dispose it.</summary>
    public Task<HttpResponseMessage> GetAsync(I2pHttpRequestTarget target, CancellationToken cancellationToken = default) =>
        GetAsync(target, null, cancellationToken);

    // A fixed crawler identity for the probe; no public arbitrary-header surface.
    internal async Task<HttpResponseMessage> GetAsync(I2pHttpRequestTarget target, string? userAgent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(requestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, target.RequestUri)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        request.Headers.Host = target.DestinationHostname;
        if (userAgent != null) request.Headers.UserAgent.ParseAdd(userAgent);
        try
        {
            // Buffer within a fixed cap so the same deadline/cancellation covers both
            // headers and body. Returning headers alone would let body reads hang later.
            return await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, deadline.Token);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("I2P request canceled by caller.", ex, cancellationToken);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException("I2P proxy request timed out.", ex);
        }
    }

    private static async ValueTask<Stream> ConnectProxyAsync(IPEndPoint endpoint, CancellationToken token)
    {
        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(endpoint, token);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch { socket.Dispose(); throw; }
    }

    // No bypass rules, local-host checks, PAC/system/environment discovery or credentials.
    private sealed class FixedProxy(Uri uri) : IWebProxy
    {
        public ICredentials? Credentials { get; set; }
        public Uri GetProxy(Uri destination) => uri;
        public bool IsBypassed(Uri host) => false;
    }

    public void Dispose() => client.Dispose();
}
