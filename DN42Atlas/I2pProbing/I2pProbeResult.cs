using DN42Atlas.AddressBook;

namespace DN42Atlas.I2pProbing;

public enum I2pRequestOutcome
{
    RejectedBeforeNetworking,
    ProxyUnavailable,
    TimedOut,
    TransportFailure,
    ProxyHttpResponse
}

public enum I2pOriginEvidence { NotObserved, Unproven }

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
    SkippedOriginUnproven
}

/// <summary>Private proxy observation; neither status nor body authenticates an I2P origin.</summary>
public sealed record I2pResponseObservation(
    int StatusCode,
    string? ContentType,
    int BodyBytes,
    string? RedirectLocation,
    bool RedirectLocationTruncated);

/// <summary>
/// Private single-seed input/result. The current generic proxy supplies no origin proof,
/// so every disposition intentionally skips the homepage. Cancellation throws instead
/// of being conflated with a deadline or a proxy failure.
/// </summary>
public sealed record I2pProbeResult(
    I2pSeed? Seed,
    string? DestinationHostname,
    I2pRequestOutcome RequestOutcome,
    I2pOriginEvidence OriginEvidence,
    I2pResponseObservation? RobotsResponse,
    I2pRobotsObservation Robots,
    I2pHomepageDisposition Homepage)
{
    public bool HomepageRequestAttempted => false;
}
