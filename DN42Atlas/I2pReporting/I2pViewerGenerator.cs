using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DN42Atlas.AddressBook;
using DN42Atlas.I2pProbing;
using DN42Atlas.I2pPublishing;
using DN42Atlas.IO;

namespace DN42Atlas.I2pReporting;

/// <summary>Standalone reporting from a validated public artifact, with staged single-file installation.</summary>
public sealed class I2pViewerGenerator
{
    private const int MaxHtmlCharacters = 64 * 1024 * 1024;
    private readonly Action<string>? beforeReplace;
    public I2pViewerGenerator() { }
    internal I2pViewerGenerator(Action<string> beforeReplace) => this.beforeReplace = beforeReplace;

    public async Task<I2pPublicScan> GenerateAsync(string inputPath, string outputPath, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var input = I2pProjectionService.LocalPath(inputPath);
        var output = I2pProjectionService.LocalPath(outputPath);
        if (input.Equals(output, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Viewer input and output must be separate local files.");
        RejectLinks(input);
        RejectLinks(output);
        var directory = Path.GetDirectoryName(output)!;
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("Viewer output directory does not exist.");
        if (Directory.Exists(output)) throw new IOException("Viewer output must be a file.");
        var scan = await I2pPublicScanReader.ReadAsync(input, token);
        var html = GenerateHtml(scan, token);
        var staged = Path.Combine(directory, $".i2p-view-{Guid.NewGuid():N}.tmp");
        var owned = false;
        try
        {
            await using (var stream = PrivateFile.CreateNew(staged))
            {
                owned = true;
                await using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 81920, leaveOpen: true))
                {
                    await writer.WriteAsync(html.AsMemory(), token);
                    await writer.FlushAsync(token);
                }
                await stream.FlushAsync(token);
                stream.Flush(flushToDisk: true);
            }
            beforeReplace?.Invoke(staged);
            token.ThrowIfCancellationRequested();
            File.Move(staged, output, overwrite: true);
            owned = false;
        }
        finally { if (owned) File.Delete(staged); }
        return scan;
    }

    // Production callers enter through GenerateAsync and the strict public reader.
    internal static string GenerateHtml(I2pPublicScan scan, CancellationToken token = default)
    {
        var byHost = scan.Entries.GroupBy(e => e.Hostname, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var discovered = scan.Entries.SelectMany(e => e.DiscoveredI2pHostnames).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        var stats = new StringBuilder();
        Stat(scan.SeedCount, "public destination entries");
        Stat(scan.UniqueHostnameCount, "unique friendly hostnames");
        Stat(scan.Entries.Count(e => e.SummaryState == I2pPublicSummaryState.HomepageResponseObserved), "homepage responses observed");
        Stat(scan.Entries.Count(e => !string.IsNullOrWhiteSpace(e.Title)), "entries with titles");
        Stat(byHost.Count(g => g.Value.Length > 1), "multi-destination friendly hostnames");
        Stat(discovered.Length, "unique discovered I2P hostnames");
        void Stat(int count, string label) => stats.Append($"<div class=\"stat\"><strong>{count}</strong><span>{E(label)}</span></div>");
        var rows = new StringBuilder();
        var details = new StringBuilder();
        var hosts = new StringBuilder();
        foreach (var entry in scan.Entries)
        {
            token.ThrowIfCancellationRequested();
            var id = Id(entry);
            var title = entry.Title ?? "(no title)";
            var state = StateLabel(entry.SummaryState);
            var search = string.Join(" ", new[] { entry.Hostname, entry.DestinationHostname, entry.Title, entry.HomepageContentType }
                .Concat(entry.DiscoveredI2pHostnames)).ToLowerInvariant();
            var badge = entry.DestinationsForHostname > 1 ? $" <span class=\"badge\">{entry.DestinationsForHostname} destinations</span>" : "";
            rows.Append($"""
                <tr data-service="{id}" data-search="{E(search)}" data-hostname="{E(entry.Hostname)}" data-identity="{E(entry.Hostname + "\n" + entry.DestinationHostname)}"
                    data-title="{E(entry.Title ?? "")}" data-state="{entry.SummaryState}" data-robots="{entry.RobotsObservation}"
                    data-http="{entry.HomepageStatusCode?.ToString(CultureInfo.InvariantCulture)}" data-discovered="{entry.DiscoveredI2pHostnames.Length}"
                    data-has-title="{Bool(entry.Title != null)}" data-multiple="{Bool(entry.DestinationsForHostname > 1)}" data-incomplete="{Bool(entry.LinkExtractionIncomplete)}" role="row">
                    <th scope="row" role="rowheader" id="row-{id}" headers="column-hostname"><span class="cell-label" aria-hidden="true">Hostname</span><span class="cell-value"><a href="#{id}" aria-label="View details for {E(entry.Hostname)}, {E(entry.DestinationHostname)}">{E(entry.Hostname)}</a>{badge}</span></th>
                    <td role="cell" headers="column-title row-{id}"><span class="cell-label" aria-hidden="true">Title</span><span class="cell-value">{E(title)}</span></td>
                    <td role="cell" headers="column-state row-{id}"><span class="cell-label" aria-hidden="true">State</span><span class="cell-value">{E(state)}</span></td>
                    <td role="cell" headers="column-http row-{id}"><span class="cell-label" aria-hidden="true">HTTP</span><span class="cell-value">{E(HttpLabel(entry))}</span></td>
                    <td role="cell" headers="column-discovered row-{id}"><span class="cell-label" aria-hidden="true">Discovered</span><span class="cell-value">{entry.DiscoveredI2pHostnames.Length}{(entry.LinkExtractionIncomplete ? " · incomplete" : "")}</span></td>
                </tr>
                """);
            details.Append($"""
                <article class="card service-detail" id="{id}" tabindex="-1" aria-labelledby="title-{id}">
                    <h3 id="title-{id}">{E(entry.Hostname)}{badge}</h3>
                    <p>{E(title)}</p>
                    <dl><dt>Friendly hostname</dt><dd>{E(entry.Hostname)}</dd>
                    <dt>Destination-derived B32 hostname</dt><dd>{E(entry.DestinationHostname)}</dd>
                    <dt>Destinations for friendly hostname</dt><dd>{entry.DestinationsForHostname}</dd>
                    <dt>Summary state</dt><dd>{E(state)} <span class="muted">({entry.SummaryState})</span></dd></dl>
                    <p>{Link(entry.DestinationHostname, "Open destination in I2P")} · {Link(entry.Hostname, "Open friendly hostname in I2P")}</p>
                    <h4>Robots observation</h4><dl>
                """);
            Field("Request outcome", entry.RobotsRequestOutcome?.ToString());
            Field("Proxy classification", entry.RobotsProxyClassification.ToString());
            Field("Observation", entry.RobotsObservation.ToString());
            Field("HTTP status", entry.RobotsStatusCode?.ToString(CultureInfo.InvariantCulture));
            details.Append("</dl><h4>Homepage observation</h4><dl>");
            Field("Attempted", entry.HomepageRequestAttempted is { } attempted ? attempted ? "Yes" : "No" : null);
            Field("Request outcome", entry.HomepageRequestOutcome?.ToString());
            Field("Proxy classification", entry.HomepageProxyClassification?.ToString());
            Field("HTTP status", entry.HomepageStatusCode?.ToString(CultureInfo.InvariantCulture));
            Field("Content type", entry.HomepageContentType);
            Field("Title", entry.Title);
            details.Append("</dl><h4>Discovered I2P hostnames</h4>");
            if (entry.LinkExtractionIncomplete) details.Append("<p class=\"notice\">Link extraction is incomplete; this list may omit referenced hosts.</p>");
            if (entry.DiscoveredI2pHostnames.Length == 0) details.Append("<p class=\"muted\">No I2P hostnames recorded.</p>");
            else
            {
                details.Append("<ul>");
                foreach (var host in entry.DiscoveredI2pHostnames) details.Append("<li>" + HostHtml(host, byHost) + "</li>");
                details.Append("</ul>");
            }
            details.Append("<p><a href=\"#directory\">Back to directory</a></p></article>");
            RequireBound();
            void Field(string label, string? value) => details.Append($"<dt>{E(label)}</dt><dd>{E(value ?? "Not observed")}</dd>");
        }
        HostGroup("Discovered friendly hostnames already in Atlas", discovered.Where(byHost.ContainsKey));
        HostGroup("Discovered hostnames outside the friendly seed set", discovered.Where(h => !byHost.ContainsKey(h)));
        void HostGroup(string heading, IEnumerable<string> names)
        {
            var items = names.ToArray();
            hosts.Append($"<details><summary>{E(heading)} ({items.Length})</summary><ul>");
            foreach (var host in items)
            {
                token.ThrowIfCancellationRequested();
                hosts.Append("<li>" + HostHtml(host, byHost) + "</li>");
                RequireBound();
            }
            hosts.Append("</ul></details>");
        }
        void RequireBound()
        {
            if (rows.Length + (long)details.Length + hosts.Length > MaxHtmlCharacters)
                throw new InvalidDataException("Viewer exceeds its HTML size limit.");
        }
        if (rows.Length + (long)details.Length + hosts.Length > MaxHtmlCharacters)
            throw new InvalidDataException("Viewer exceeds its HTML size limit.");
        var values = new Dictionary<string, string>
        {
            ["stats"] = stats.ToString(), ["rows"] = rows.ToString(), ["details"] = details.ToString(), ["hosts"] = hosts.ToString(),
            ["times"] = $"<p>Scan started: {Time(scan.ScanStartedAtUtc)}</p><p>Scan completed: {Time(scan.ScanCompletedAtUtc)}</p>",
            ["states"] = string.Join("", Enum.GetValues<I2pPublicSummaryState>().Select(s => $"<option value=\"{s}\">{E(StateLabel(s))}</option>")),
            ["robots"] = string.Join("", Enum.GetValues<I2pRobotsObservation>().Select(s => $"<option value=\"{s}\">{s}</option>")),
            ["empty"] = scan.SeedCount == 0 ? "<p>No public destination entries.</p>" : ""
        };
        using var resource = typeof(I2pViewerGenerator).Assembly.GetManifestResourceStream("DN42Atlas.I2pReporting.viewer.html")
            ?? throw new InvalidOperationException("Bundled I2P viewer template is missing.");
        using var reader = new StreamReader(resource);
        // One replacement pass prevents untrusted strings resembling placeholders from becoming template instructions.
        return Regex.Replace(reader.ReadToEnd(), @"\{\{(\w+)\}\}", m => values[m.Groups[1].Value]);
    }

    internal static string Id(I2pPublicEntry entry) => "i2p-service-" +
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(entry.Hostname + "\n" + entry.DestinationHostname)));

    internal static string StateLabel(I2pPublicSummaryState state) => state switch
    {
        I2pPublicSummaryState.HomepageResponseObserved => "Homepage response observed",
        I2pPublicSummaryState.HomepageAttemptFailed => "Homepage attempt failed",
        I2pPublicSummaryState.RobotsDisallowed => "Robots disallowed",
        I2pPublicSummaryState.ProxyFailureObserved => "Proxy failure observed",
        I2pPublicSummaryState.RequestTimedOut => "Request timed out",
        I2pPublicSummaryState.TransportFailure => "Transport failure",
        I2pPublicSummaryState.RobotsRedirect => "Robots redirect",
        I2pPublicSummaryState.Ambiguous => "Ambiguous",
        _ => throw new InvalidDataException("Unknown summary state.")
    };
    private static string HttpLabel(I2pPublicEntry e) => e.HomepageStatusCode is { } status ? "Homepage " + status :
        e.RobotsStatusCode is { } robots ? "Robots " + robots : e.RobotsRequestOutcome switch
        {
            I2pRequestOutcome.RejectedBeforeNetworking => "Request rejected",
            I2pRequestOutcome.ProxyUnavailable => "Proxy unavailable",
            I2pRequestOutcome.TimedOut => "Timed out",
            I2pRequestOutcome.TransportFailure => "Transport failure",
            _ => "Not observed"
        };
    private static string HostHtml(string host, IReadOnlyDictionary<string, I2pPublicEntry[]> byHost)
    {
        if (!byHost.TryGetValue(host, out var entries)) return Link(host, host);
        return E(host) + " · " + string.Join(" · ", entries.Select(e =>
            $"<a href=\"#{Id(e)}\" title=\"{E(e.DestinationHostname)}\" aria-label=\"View {E(e.Hostname)}, {E(e.DestinationHostname)}\">View destination{(entries.Length > 1 ? " " + (System.Array.IndexOf(entries, e) + 1).ToString(CultureInfo.InvariantCulture) : "")}</a>"));
    }
    private static string Link(string host, string label)
    {
        try
        {
            if (I2pSeed.NormalizeHostname(host) == host)
                return $"<a href=\"http://{E(host)}/\" target=\"_blank\" rel=\"noopener noreferrer\">{E(label)}</a>";
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException) { }
        return E(label);
    }
    private static string E(string value) => WebUtility.HtmlEncode(value);
    private static string Bool(bool value) => value ? "true" : "false";
    private static string Time(DateTimeOffset value) =>
        $"<time datetime=\"{value.ToString("O", CultureInfo.InvariantCulture)}\">{value.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)}</time>";
    private static void RejectLinks(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("Viewer paths must not use filesystem links.");
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(path)!); parent != null && parent.Exists; parent = parent.Parent)
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Viewer paths must not use filesystem links.");
    }
}
