using System.Globalization;
using DN42Atlas.I2pScanning;
using DN42Atlas.I2pTransport;

namespace DN42Atlas.Commands;

public static class I2pScanCommand
{
    private const string Usage = "Usage: dn42atlas i2p-scan <snapshot-file> <private-output-file> <proxy-uri> [--concurrency <1-4>] [--timeout-ms <1-60000>] [--max-seeds <1-10000>]";

    public static async Task<int> ExecuteAsync(string[] args, TextWriter? output = null, TextWriter? error = null,
        CancellationToken cancellationToken = default)
    {
        output ??= Console.Out;
        error ??= Console.Error;
        var concurrency = I2pScanSettings.DefaultConcurrency;
        var timeout = 10000;
        var maximumSeeds = 10000;
        var valid = args.Length is >= 4 and <= 10 && args.Length % 2 == 0 && !args.Skip(1).Any(string.IsNullOrWhiteSpace);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (valid)
        {
            for (var i = 4; i < args.Length; i += 2)
            {
                if (!seen.Add(args[i]) || !int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                { valid = false; break; }
                switch (args[i])
                {
                    case "--concurrency": concurrency = value; break;
                    case "--timeout-ms": timeout = value; break;
                    case "--max-seeds": maximumSeeds = value; break;
                    default: valid = false; break;
                }
            }
        }
        I2pScanSettings? settings = null;
        if (valid)
        {
            try { settings = new(concurrency, timeout, maximumSeeds); I2pProxyEndpoint.Parse(args[3]); }
            catch (ArgumentException) { valid = false; }
        }
        if (!valid)
        {
            await output.WriteLineAsync(Usage);
            return 2;
        }

        var installed = false;
        try
        {
            var scan = await new I2pScanService().ScanAsync(args[1], args[2], args[3], settings, cancellationToken);
            installed = true;
            await output.WriteLineAsync(FormattableString.Invariant(
                $"Private I2P scan complete: {scan.TotalSeeds} seeds, {scan.Metrics.HomepageAttemptedCount} homepage attempts, {scan.Metrics.HomepageResponseCount} homepage responses, {scan.Metrics.TotalDurationMilliseconds:F1} ms. Responses remain unauthenticated."));
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await error.WriteLineAsync("I2P scan cancelled; no new final artifact was installed.");
            return 130;
        }
        catch (ArgumentException)
        {
            await error.WriteLineAsync("Invalid I2P scan paths or configuration; output must be a separate local file outside public roots and filesystem links.");
            return 2;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            await error.WriteLineAsync(installed
                ? "Private I2P scan artifact was installed, but writing the diagnostic summary failed."
                : "I2P scan failed: invalid snapshot, resource limit, or private output I/O failure. Previous output was preserved.");
            return 1;
        }
    }
}
