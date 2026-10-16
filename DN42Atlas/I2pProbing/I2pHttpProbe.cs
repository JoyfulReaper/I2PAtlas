using System.Net.Sockets;
using System.Text;
using DN42Atlas.AddressBook;
using DN42Atlas.I2pTransport;
using DN42Atlas.Probing;

namespace DN42Atlas.I2pProbing;

/// <summary>One seed, one robots request, through a caller-owned I2P transport only.</summary>
public sealed class I2pHttpProbe
{
    public const string CrawlerProduct = "I2PAtlas";
    public const string UserAgent = CrawlerProduct + "/0.1";
    public const int MaxRobotsBytes = 64 * 1024;
    public const int MaxContentTypeCharacters = 256;
    public const int MaxRedirectCharacters = I2pHttpRequestTarget.MaxUrlCharacters;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private readonly I2pHttpTransport transport;

    public I2pHttpProbe(I2pHttpTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        this.transport = transport;
    }

    public async Task<I2pProbeResult> ProbeAsync(I2pSeed? seed, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (seed == null)
            return new(null, null, I2pRequestOutcome.RejectedBeforeNetworking, I2pOriginEvidence.NotObserved,
                null, I2pRobotsObservation.NotObserved, I2pHomepageDisposition.SkippedRequestRejected);

        // Both URI and Host are bound to this seed's destination by the existing transport.
        var target = new I2pHttpRequestTarget(seed, $"http://{seed.Hostname}/robots.txt");
        try
        {
            using var response = await transport.GetAsync(target, UserAgent, cancellationToken);
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken); // already capped by transport
            cancellationToken.ThrowIfCancellationRequested();
            var location = response.Headers.TryGetValues("Location", out var values) ? values.FirstOrDefault() : null;
            var contentType = response.Content.Headers.TryGetValues("Content-Type", out var types) ? types.FirstOrDefault() : null;
            var observation = new I2pResponseObservation((int)response.StatusCode,
                Bound(contentType, MaxContentTypeCharacters), bytes.Length,
                Bound(location, MaxRedirectCharacters), location?.Length > MaxRedirectCharacters);
            var robots = ObserveRobots(response, bytes, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var homepage = robots switch
            {
                I2pRobotsObservation.DisallowsRoot => I2pHomepageDisposition.SkippedObservedDisallow,
                I2pRobotsObservation.AllowsRoot => I2pHomepageDisposition.SkippedOriginUnproven,
                _ => I2pHomepageDisposition.SkippedRobotsUnknown
            };
            // There is deliberately no status/header/body heuristic or caller override
            // that can convert an unauthenticated proxy response into origin evidence.
            return new(seed, target.DestinationHostname, I2pRequestOutcome.ProxyHttpResponse,
                I2pOriginEvidence.Unproven, observation, robots, homepage);
        }
        catch (TimeoutException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Failed(I2pRequestOutcome.TimedOut);
        }
        catch (HttpRequestException ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Only a connection error with socket evidence means the proxy connection
            // failed. Invalid/oversized HTTP is not labeled "proxy unreachable".
            var unavailable = ex.HttpRequestError == HttpRequestError.ConnectionError && HasSocketCause(ex);
            return Failed(unavailable ? I2pRequestOutcome.ProxyUnavailable : I2pRequestOutcome.TransportFailure);
        }

        I2pProbeResult Failed(I2pRequestOutcome outcome) => new(seed, target.DestinationHostname, outcome,
            I2pOriginEvidence.NotObserved, null, I2pRobotsObservation.NotObserved,
            I2pHomepageDisposition.SkippedRequestFailed);
    }

    private static I2pRobotsObservation ObserveRobots(HttpResponseMessage response, byte[] bytes, CancellationToken token)
    {
        var status = (int)response.StatusCode;
        if (status is >= 300 and <= 399) return I2pRobotsObservation.Redirect;
        if (status != 200) return I2pRobotsObservation.UnknownStatus; // including ambiguous 404
        if (bytes.Length > MaxRobotsBytes) return I2pRobotsObservation.TooLarge;
        if (response.Content.Headers.ContentEncoding.Count > 0) return I2pRobotsObservation.UnsupportedRepresentation;
        var type = response.Content.Headers.ContentType;
        if (type != null && (!string.Equals(type.MediaType, "text/plain", StringComparison.OrdinalIgnoreCase) ||
            (type.CharSet != null && !string.Equals(type.CharSet.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase))))
            return I2pRobotsObservation.UnsupportedRepresentation;
        try
        {
            var text = StrictUtf8.GetString(bytes);
            if (text.Any(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t'))
                return I2pRobotsObservation.Malformed;
            return RobotsPolicy.IsPathAllowed(text, CrawlerProduct, "/", token) switch
            {
                true => I2pRobotsObservation.AllowsRoot,
                false => I2pRobotsObservation.DisallowsRoot,
                null => I2pRobotsObservation.Malformed
            };
        }
        catch (DecoderFallbackException) { return I2pRobotsObservation.Malformed; }
    }

    private static bool HasSocketCause(Exception error)
    {
        for (Exception? current = error; current != null; current = current.InnerException)
            if (current is SocketException) return true;
        return false;
    }

    internal static string? Bound(string? value, int limit) => value?.Length > limit ? value[..limit] : value;
}
