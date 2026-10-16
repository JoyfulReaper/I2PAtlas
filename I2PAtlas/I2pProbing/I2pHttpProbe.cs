using System.Net.Sockets;
using System.Text;
using I2PAtlas.AddressBook;
using I2PAtlas.I2pTransport;
using I2PAtlas.Robots;

namespace I2PAtlas.I2pProbing;

/// <summary>Robots, then optionally root, through a caller-owned I2P transport only.</summary>
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
                I2pProxyResponseClassification.Ambiguous, null, I2pRobotsObservation.NotObserved, I2pHomepageDisposition.SkippedRequestRejected);

        var target = new I2pProbeTarget.KnownSeed(seed);
        var observation = await ProbeTargetAsync(target, cancellationToken);
        return new(seed, target.DestinationHostname, observation.RequestOutcome, observation.OriginEvidence,
            observation.ProxyResponseClassification, observation.RobotsResponse, observation.Robots, observation.Homepage,
            observation.HomepageRequestOutcome, observation.HomepageResponse, observation.HomepageMetadata,
            observation.HomepageProxyResponseClassification);
    }

    // One state machine for both closed target variants. Host-only responses
    // remain unproven proxy observations, with no inferred destination identity.
    internal async Task<I2pProbeObservation> ProbeTargetAsync(I2pProbeTarget target, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(target);
        var request = new I2pProbeRequest(target, ProbeResource.Robots);
        I2pProbeObservation result;
        try
        {
            using var response = await transport.GetAsync(request, cancellationToken);
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken); // already capped by transport
            cancellationToken.ThrowIfCancellationRequested();
            var observation = ObserveResponse(response, bytes.Length);
            var robots = ObserveRobots(response, bytes, cancellationToken);
            var classification = I2pProxyResponseClassifier.ClassifyRobots(response, robots, bytes);
            cancellationToken.ThrowIfCancellationRequested();
            var homepage = classification == I2pProxyResponseClassification.LikelyProxyFailure
                ? I2pHomepageDisposition.SkippedProxyFailure
                : robots switch
                {
                    I2pRobotsObservation.DisallowsRoot => I2pHomepageDisposition.SkippedObservedDisallow,
                    I2pRobotsObservation.AllowsRoot => I2pHomepageDisposition.SkippedAmbiguousResponse,
                    _ => I2pHomepageDisposition.SkippedRobotsUnknown
                };
            result = new(I2pRequestOutcome.ProxyHttpResponse,
                I2pOriginEvidence.Unproven, classification, observation, robots, homepage);
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
            return Failed(FailureOutcome(ex));
        }

        if (result.ProxyResponseClassification != I2pProxyResponseClassification.LikelyOriginResponse ||
            result.Robots != I2pRobotsObservation.AllowsRoot) return result;

        // This is an explicit heuristic authorization, not a promotion of Unproven.
        return await ProbeHomepageAsync(result, new I2pProbeRequest(target, ProbeResource.Root), cancellationToken);

        I2pProbeObservation Failed(I2pRequestOutcome outcome) => new(outcome,
            I2pOriginEvidence.NotObserved, I2pProxyResponseClassification.Ambiguous, null, I2pRobotsObservation.NotObserved,
            I2pHomepageDisposition.SkippedRequestFailed);
    }

    private async Task<I2pProbeObservation> ProbeHomepageAsync(I2pProbeObservation robots, I2pProbeRequest target, CancellationToken token)
    {
        try
        {
            using var response = await transport.GetAsync(target, token);
            var bytes = await response.Content.ReadAsByteArrayAsync(token);
            token.ThrowIfCancellationRequested();
            var observation = ObserveResponse(response, bytes.Length);
            // Parse only successful, uncompressed page bytes. HTTP errors/redirects
            // remain response observations, not homepage title/link evidence.
            var metadata = response.StatusCode == System.Net.HttpStatusCode.OK && response.Content.Headers.ContentEncoding.Count == 0
                ? I2pPageMetadata.Read(target, bytes, observation.ContentType, token) : null;
            return robots with
            {
                Homepage = I2pHomepageDisposition.ResponseObserved,
                HomepageRequestOutcome = I2pRequestOutcome.ProxyHttpResponse,
                HomepageResponse = observation,
                HomepageMetadata = metadata,
                HomepageProxyResponseClassification = I2pProxyResponseClassifier.ClassifyStatus(observation.StatusCode)
            };
        }
        catch (TimeoutException)
        {
            token.ThrowIfCancellationRequested();
            return Failed(I2pRequestOutcome.TimedOut);
        }
        catch (HttpRequestException ex)
        {
            token.ThrowIfCancellationRequested();
            return Failed(FailureOutcome(ex));
        }

        I2pProbeObservation Failed(I2pRequestOutcome outcome) => robots with
        {
            Homepage = I2pHomepageDisposition.RequestFailed,
            HomepageRequestOutcome = outcome
        };
    }

    private static I2pResponseObservation ObserveResponse(HttpResponseMessage response, int bodyBytes)
    {
        var location = response.Headers.TryGetValues("Location", out var values) ? values.FirstOrDefault() : null;
        var contentType = response.Content.Headers.TryGetValues("Content-Type", out var types) ? types.FirstOrDefault() : null;
        return new((int)response.StatusCode, Bound(contentType, MaxContentTypeCharacters), bodyBytes,
            Bound(location, MaxRedirectCharacters), location?.Length > MaxRedirectCharacters);
    }

    private static I2pRequestOutcome FailureOutcome(HttpRequestException ex) =>
        ex.HttpRequestError == HttpRequestError.ConnectionError && HasSocketCause(ex)
            ? I2pRequestOutcome.ProxyUnavailable : I2pRequestOutcome.TransportFailure;

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
