using System.Globalization;
using I2PAtlas.I2pCrawling;
using I2PAtlas.I2pTransport;

namespace I2PAtlas.Commands;

public static class I2pCrawlCommand
{
    private const string Usage = "Usage: i2patlas i2p-crawl <snapshot-file> <private-output-file> <proxy-uri> [--concurrency <1-4>] [--timeout-ms <1-60000>] [--max-seeds <1-10000>] [--max-discovered-hosts <0-1000>]";

    public static async Task<int> ExecuteAsync(string[] args, TextWriter? output = null, TextWriter? error = null,
        CancellationToken cancellationToken = default)
    {
        output ??= Console.Out;
        error ??= Console.Error;
        var concurrency = 2;
        var timeout = 10000;
        var seeds = 10000;
        var hosts = 100;
        var valid = args.Length is >= 4 and <= 12 && args.Length % 2 == 0 && !args.Skip(1).Any(string.IsNullOrWhiteSpace);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (valid)
            for (var i = 4; i < args.Length; i += 2)
            {
                if (!seen.Add(args[i]) || !int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                { valid = false; break; }
                switch (args[i])
                {
                    case "--concurrency": concurrency = value; break;
                    case "--timeout-ms": timeout = value; break;
                    case "--max-seeds": seeds = value; break;
                    case "--max-discovered-hosts": hosts = value; break;
                    default: valid = false; break;
                }
            }
        I2pCrawlSettings? settings = null;
        if (valid)
            try { settings = new(concurrency, timeout, seeds, hosts); I2pProxyEndpoint.Parse(args[3]); }
            catch (ArgumentException) { valid = false; }
        if (!valid) { await output.WriteLineAsync(Usage); return 2; }

        var installed = false;
        try
        {
            var crawl = await new I2pCrawlService().CrawlAsync(args[1], args[2], args[3], settings, cancellationToken);
            installed = true;
            await output.WriteLineAsync(FormattableString.Invariant(
                $"Private I2P one-hop crawl complete: {crawl.SelectedSeedCount} known seeds, {crawl.Discovery.AdmittedHostCount} discovered hosts, {crawl.Metrics.HomepageAttemptedCount} homepage attempts, {crawl.Metrics.HomepageResponseCount} homepage responses, {crawl.Metrics.TotalDurationMilliseconds:F1} ms. Responses remain unauthenticated."));
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await error.WriteLineAsync("I2P crawl cancelled; no new final artifact was installed.");
            return 130;
        }
        catch (ArgumentException)
        {
            await error.WriteLineAsync("Invalid I2P crawl paths or configuration; output must be a separate local file outside public roots and filesystem links.");
            return 2;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await error.WriteLineAsync(installed ? "Private crawl artifact was installed, but writing the summary failed."
                : "I2P crawl failed: invalid snapshot, resource limit, infrastructure or private output I/O failure. Previous output was preserved.");
            return 1;
        }
    }
}
