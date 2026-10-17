using I2PAtlas.I2pReporting;

namespace I2PAtlas.Commands;

public static class I2pCrawlViewCommand
{
    public static async Task<int> ExecuteAsync(string[] args, TextWriter? output = null, TextWriter? error = null,
        CancellationToken cancellationToken = default)
    {
        output ??= Console.Out;
        error ??= Console.Error;
        if (args.Length != 3 || args.Skip(1).Any(string.IsNullOrWhiteSpace))
        {
            await output.WriteLineAsync("Usage: i2patlas i2p-crawl-view <public-crawl-file> <html-output-file>");
            return 2;
        }
        var installed = false;
        try
        {
            var crawl = await new I2pCrawlViewerGenerator().GenerateAsync(args[1], args[2], cancellationToken);
            installed = true;
            await output.WriteLineAsync($"I2PAtlas crawl viewer installed: {crawl.KnownSeedCount} known seeds, {crawl.DiscoveredHostCount} discovered hosts. Proxy observations remain unauthenticated.");
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await error.WriteLineAsync(installed ? "Crawl viewer installed, but summary writing was cancelled."
                : "Crawl viewer cancelled; previous HTML preserved.");
            return 130;
        }
        catch (ArgumentException) when (!installed)
        {
            await error.WriteLineAsync("Use separate explicit local crawl input/output files without filesystem links.");
            return 2;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await error.WriteLineAsync(installed ? "Crawl viewer installed, but writing its summary failed."
                : "Crawl viewer failed: invalid public artifact, rendering limit or local I/O failure. Previous HTML preserved.");
            return 1;
        }
    }
}
