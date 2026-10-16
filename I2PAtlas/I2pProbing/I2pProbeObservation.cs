namespace I2PAtlas.I2pProbing;

// Identity-neutral, private observations. Host-only probes do not acquire a seed
// or destination, and proxy name resolution need not be pinned between requests.
internal sealed record I2pProbeObservation(
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
    internal bool HomepageRequestAttempted => HomepageRequestOutcome.HasValue;
    internal I2pOriginEvidence HomepageOriginEvidence => HomepageResponse == null ? I2pOriginEvidence.NotObserved : I2pOriginEvidence.Unproven;
}
