using I2PAtlas.I2pCrawling;

namespace I2PAtlas.Commands;

// Called serially by the service. Throttles completion events, never prints identities.
internal sealed class I2pCrawlProgressWriter(TextWriter writer)
{
    private int depth = -1;
    private int lastCompleted;
    private double lastElapsed;
    private bool failed;

    internal void Report(I2pCrawlProgress value)
    {
        if (failed) return;
        if (!value.WaveCompleted && value.Depth == depth && value.CompletedTargets - lastCompleted < 25 &&
            value.ElapsedMilliseconds - lastElapsed < 20000) return;
        try
        {
            if (value.WaveCompleted)
                writer.WriteLine(FormattableString.Invariant($"Depth {value.Depth} complete: {value.CompletedTargets} probed, {value.EligibleReferences} eligible references, {value.AdmittedHosts} hosts admitted."));
            else
                writer.WriteLine(FormattableString.Invariant($"Depth {value.Depth}: {value.CompletedTargets}/{value.TotalTargets} complete | {value.HomepageAttemptedCount} homepage attempts | {value.HomepageResponseCount} responses | {TimeSpan.FromMilliseconds(value.ElapsedMilliseconds):c}"));
            depth = value.Depth;
            lastCompleted = value.CompletedTargets;
            lastElapsed = value.ElapsedMilliseconds;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { failed = true; }
    }
}
