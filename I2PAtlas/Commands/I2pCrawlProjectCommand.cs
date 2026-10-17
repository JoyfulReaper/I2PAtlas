using I2PAtlas.I2pPublishing;

namespace I2PAtlas.Commands;

public static class I2pCrawlProjectCommand
{
    public static async Task<int> ExecuteAsync(string[] args, TextWriter? output = null, TextWriter? error = null,
        CancellationToken cancellationToken = default)
    {
        output ??= Console.Out;
        error ??= Console.Error;
        if (args.Length != 3 || args.Skip(1).Any(string.IsNullOrWhiteSpace))
        {
            await output.WriteLineAsync("Usage: i2patlas i2p-crawl-project <private-raw-crawl-file> <public-output-file>");
            return 2;
        }
        var installed = false;
        try
        {
            var result = await new I2pCrawlProjectionService().ProjectAsync(args[1], args[2], cancellationToken);
            installed = true;
            await output.WriteLineAsync($"Public crawl projection installed: {result.KnownSeedCount} known seeds, {result.DiscoveredHostCount} discovered hosts. Proxy observations remain unauthenticated.");
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await error.WriteLineAsync(installed ? "Public crawl artifact was installed, but summary writing was cancelled."
                : "Crawl projection cancelled; no new public artifact was installed.");
            return 130;
        }
        catch (ArgumentException) when (!installed)
        {
            await error.WriteLineAsync("Invalid crawl projection paths; use separate explicit local input/output files without filesystem links.");
            return 2;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await error.WriteLineAsync(installed ? "Public crawl artifact was installed, but writing the summary failed."
                : "Crawl projection failed: invalid artifact, resource limit or local I/O failure. Previous public output was preserved.");
            return 1;
        }
    }
}
