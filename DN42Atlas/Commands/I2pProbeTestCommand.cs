using System.Diagnostics;
using System.Globalization;
using System.Text;
using DN42Atlas.AddressBook;
using DN42Atlas.I2pProbing;
using DN42Atlas.I2pTransport;
using DN42Atlas.Seeds;

namespace DN42Atlas.Commands;

/// <summary>Explicit one-seed integration diagnostic, independent of DN42 runtime setup.</summary>
public static class I2pProbeTestCommand
{
    public const int MaxReportCharacters = 4096;
    private const string Usage = "Usage: dn42atlas i2p-probe-test <snapshot-file> <hostname.i2p> <proxy-uri> [--timeout-ms <1-60000>]";

    public static async Task<int> ExecuteAsync(string[] args, TextWriter? output = null, TextWriter? error = null,
        CancellationToken cancellationToken = default)
    {
        output ??= Console.Out;
        error ??= Console.Error;
        var timeout = I2pHttpTransport.DefaultTimeout;
        if (args.Length is not (4 or 6) || args.Skip(1).Any(string.IsNullOrWhiteSpace) ||
            (args.Length == 6 && (args[4] != "--timeout-ms" ||
                !int.TryParse(args[5], NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds) ||
                milliseconds is < 1 or > 60000)))
        {
            await output.WriteLineAsync(Usage);
            return 2;
        }
        if (args.Length == 6) timeout = TimeSpan.FromMilliseconds(int.Parse(args[5], CultureInfo.InvariantCulture));

        I2pProxyEndpoint endpoint;
        string hostname;
        try
        {
            endpoint = I2pProxyEndpoint.Parse(args[3]);
            hostname = I2pSeed.NormalizeHostname(args[2]);
        }
        catch (I2pProxyConfigurationException)
        {
            await error.WriteLineAsync("An explicit HTTP proxy URI with a literal loopback IP and port is required.");
            return 2;
        }
        catch (FormatException)
        {
            await error.WriteLineAsync("Requested hostname must be a valid .i2p hostname.");
            return 2;
        }

        I2pSeedSnapshot snapshot;
        try { snapshot = await SeedSnapshotReader.ReadAsync(args[1], cancellationToken); }
        catch (InvalidDataException ex)
        {
            // Reader validation messages contain field names/indices, never input values.
            await error.WriteLineAsync("Invalid I2P seed snapshot: " + ex.Message);
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Filesystem/JSON exception strings could disclose private paths or input.
            await error.WriteLineAsync("Unable to read the local I2P seed snapshot.");
            return 1;
        }
        var seed = snapshot.Seeds.SingleOrDefault(seed => seed.Hostname == hostname);
        if (seed == null)
        {
            await error.WriteLineAsync("Requested hostname is absent from the validated seed snapshot.");
            return 1;
        }

        using var transport = new I2pHttpTransport(endpoint.Uri.AbsoluteUri, timeout);
        var stopwatch = Stopwatch.StartNew();
        var result = await new I2pHttpProbe(transport).ProbeAsync(seed, cancellationToken);
        stopwatch.Stop();
        await output.WriteAsync(Report(result, endpoint, stopwatch.Elapsed));
        return result.RequestOutcome == I2pRequestOutcome.ProxyHttpResponse ? 0 : 1;
    }

    private static string Report(I2pProbeResult result, I2pProxyEndpoint endpoint, TimeSpan elapsed)
    {
        var report = new StringBuilder();
        report.AppendLine("I2P proxy diagnostic observation (private; no authenticated origin evidence)");
        report.AppendLine($"Friendly hostname: {result.Seed!.Hostname}");
        report.AppendLine($"Destination-derived hostname: {result.DestinationHostname}");
        report.AppendLine($"Configured proxy: {endpoint.Uri.AbsoluteUri}");
        report.AppendLine($"Request outcome: {result.RequestOutcome}");
        report.AppendLine($"Origin evidence: {result.OriginEvidence}");
        report.AppendLine($"HTTP status: {result.RobotsResponse?.StatusCode.ToString(CultureInfo.InvariantCulture) ?? "not observed"}");
        report.AppendLine($"Content type (media type only): {MediaType(result.RobotsResponse?.ContentType)}");
        report.AppendLine($"Body bytes: {result.RobotsResponse?.BodyBytes.ToString(CultureInfo.InvariantCulture) ?? "not observed"}");
        report.AppendLine($"Robots observation: {result.Robots}");
        report.AppendLine($"Homepage disposition: {result.Homepage}");
        report.AppendLine($"Homepage request attempted: {result.HomepageRequestAttempted}");
        report.AppendLine($"Redirect location (redacted): {Redirect(result.RobotsResponse?.RedirectLocation)}");
        report.AppendLine($"Redirect location truncated by probe: {result.RobotsResponse?.RedirectLocationTruncated ?? false}");
        report.AppendLine($"Elapsed probe milliseconds: {elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture)}");
        report.AppendLine("Proxy response != authenticated origin response. No redirects or discovered links are followed.");
        // All variable strings above are validated/bounded or projected below. Keep
        // a final output cap as well, even if additional fields are introduced later.
        return report.Length <= MaxReportCharacters ? report.ToString() : report.ToString(0, MaxReportCharacters);
    }

    private static string MediaType(string? value)
    {
        if (value == null) return "not observed";
        var media = value.Split(';', 2)[0].Trim();
        return media.Length is > 0 and <= 80 && media.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '.' or '+')
            ? media : "[invalid or oversized media type redacted]";
    }

    private static string Redirect(string? value)
    {
        if (value == null) return "none observed";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return "[relative, invalid or unsupported location redacted]";
        // Keep only a small, DNS-shaped authority; credentials, paths, queries and
        // fragments may contain destinations, address helpers, tokens or secrets.
        var host = uri.Host;
        if (host.Length is 0 or > 253 || host.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-'))
            return "[location authority redacted]";
        return $"{uri.Scheme}://{host}:{uri.Port}/[path/query/fragment redacted]";
    }
}
