using I2PAtlas.AddressBook;

namespace I2PAtlas.I2pProbing;

public enum I2pRequestOutcome
{
    RejectedBeforeNetworking,
    ProxyUnavailable,
    TimedOut,
    TransportFailure,
    ProxyHttpResponse
}

public enum I2pOriginEvidence { NotObserved, Unproven }

// Heuristic observations only. None of these authenticate a response author.
public enum I2pProxyResponseClassification { Ambiguous, LikelyProxyFailure, LikelyOriginResponse }

// These describe observed bytes, never an authoritative destination policy.
public enum I2pRobotsObservation
{
    NotObserved,
    UnknownStatus,
    Redirect,
    UnsupportedRepresentation,
    TooLarge,
    Malformed,
    AllowsRoot,
    DisallowsRoot
}

public enum I2pHomepageDisposition
{
    SkippedRequestRejected,
    SkippedRequestFailed,
    SkippedRobotsUnknown,
    SkippedObservedDisallow,
    SkippedAmbiguousResponse,
    SkippedProxyFailure,
    ResponseObserved,
    RequestFailed
}

/// <summary>Private proxy observation; neither status nor body authenticates an I2P origin.</summary>
public sealed record I2pResponseObservation(
    int StatusCode,
    string? ContentType,
    int BodyBytes,
    string? RedirectLocation,
    bool RedirectLocationTruncated);

/// <summary>
/// Private single-seed result. Top-level request outcome, evidence and classification
/// describe robots; homepage observations/failures never overwrite them. Cancellation
/// throws instead of being conflated with a deadline or a proxy failure.
/// </summary>
public sealed record I2pProbeResult(
    I2pSeed? Seed,
    string? DestinationHostname,
    I2pRequestOutcome RequestOutcome,
    I2pOriginEvidence OriginEvidence,
    I2pProxyResponseClassification ProxyResponseClassification,
    I2pResponseObservation? RobotsResponse,
    I2pRobotsObservation Robots,
    I2pHomepageDisposition Homepage,
    I2pRequestOutcome? HomepageRequestOutcome = null,
    I2pResponseObservation? HomepageResponse = null,
    I2pPageMetadata? HomepageMetadata = null,
    I2pProxyResponseClassification? HomepageProxyResponseClassification = null)
{
    public bool HomepageRequestAttempted => HomepageRequestOutcome.HasValue;
    public I2pOriginEvidence HomepageOriginEvidence => HomepageResponse == null ? I2pOriginEvidence.NotObserved : I2pOriginEvidence.Unproven;
}
