namespace I2PAtlas.I2pCrawling;

// Aggregate-only boundary: deliberately contains no target identities or response text.
internal sealed record I2pCrawlProgress(int Depth, int CompletedTargets, int TotalTargets,
    int HomepageAttemptedCount, int HomepageResponseCount, double ElapsedMilliseconds,
    bool WaveCompleted = false, long EligibleReferences = 0, int AdmittedHosts = 0);
