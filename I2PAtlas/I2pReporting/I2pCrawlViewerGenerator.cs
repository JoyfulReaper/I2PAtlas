using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using I2PAtlas.AddressBook;
using I2PAtlas.I2pPublishing;
using I2PAtlas.IO;

namespace I2PAtlas.I2pReporting;

/// <summary>Standalone inspection of strictly validated public-crawl data. Reads no private artifacts.</summary>
public sealed class I2pCrawlViewerGenerator
{
    internal const int MaxHtmlCharacters = 64 * 1024 * 1024;
    private readonly Action<string>? beforeReplace;
    private readonly int htmlLimit = MaxHtmlCharacters;
    public I2pCrawlViewerGenerator() { }
    internal I2pCrawlViewerGenerator(Action<string>? beforeReplace, int htmlLimit = MaxHtmlCharacters)
    {
        if (htmlLimit is < 1 or > MaxHtmlCharacters) throw new ArgumentOutOfRangeException(nameof(htmlLimit));
        this.beforeReplace = beforeReplace;
        this.htmlLimit = htmlLimit;
    }

    public async Task<I2pPublicCrawl> GenerateAsync(string inputPath, string outputPath, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var input = I2pCrawlProjectionService.LocalPathWithoutLinks(inputPath);
        var output = I2pCrawlProjectionService.LocalPathWithoutLinks(outputPath);
        if (input.Equals(output, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Crawl viewer requires separate local input and output files.");
        var directory = Path.GetDirectoryName(output)!;
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException();
        if (Directory.Exists(output)) throw new IOException("Crawl viewer output must be a file.");
        var crawl = await I2pPublicCrawlReader.ReadAsync(input, token);
        var html = GenerateHtml(crawl, token, htmlLimit);
        var staged = Path.Combine(directory, $".i2p-crawl-view-{Guid.NewGuid():N}.tmp");
        var owned = false;
        try
        {
            await using (var file = PrivateFile.CreateNew(staged))
            {
                owned = true;
                await using (var writer = new StreamWriter(file, new UTF8Encoding(false, true), 81920, leaveOpen: true))
                {
                    await writer.WriteAsync(html.AsMemory(), token);
                    await writer.FlushAsync(token);
                }
                await file.FlushAsync(token);
                file.Flush(flushToDisk: true);
            }
            beforeReplace?.Invoke(staged);
            token.ThrowIfCancellationRequested();
            File.Move(staged, output, overwrite: true);
            owned = false;
        }
        finally { if (owned) File.Delete(staged); }
        return crawl;
    }

    // Only GenerateAsync is a production entry point; it uses the strict public reader first.
    internal static string GenerateHtml(I2pPublicCrawl crawl, CancellationToken token = default, int htmlLimit = MaxHtmlCharacters)
    {
        token.ThrowIfCancellationRequested();
        if (htmlLimit is < 1 or > MaxHtmlCharacters) throw new ArgumentOutOfRangeException(nameof(htmlLimit));
        using var resource = typeof(I2pCrawlViewerGenerator).Assembly.GetManifestResourceStream("I2PAtlas.I2pReporting.crawl-viewer.html")
            ?? throw new InvalidOperationException("Bundled crawl viewer template is missing.");
        using var reader = new StreamReader(resource);
        var template = reader.ReadToEnd();
        var values = new Dictionary<string, StringBuilder>
        { ["times"] = new(), ["stats"] = new(), ["states"] = new(), ["rows"] = new(), ["details"] = new(), ["empty"] = new() };
        var placeholders = Regex.Matches(template, @"\{\{(\w+)\}\}");
        if (placeholders.Count != values.Count || placeholders.Select(m => m.Groups[1].Value).Distinct().Count() != values.Count ||
            placeholders.Any(m => !values.ContainsKey(m.Groups[1].Value))) throw new InvalidOperationException("Invalid crawl viewer template.");
        long retained = template.Length - placeholders.Sum(m => m.Length);
        Add("times", $"<p>Crawl started UTC: {Time(crawl.CrawlStartedAtUtc)}</p><p>Crawl completed UTC: {Time(crawl.CrawlCompletedAtUtc)}</p>");
        Stat(crawl.KnownSeedCount, "known seeds");
        Stat(crawl.DiscoveredHostCount, "discovered hosts");
        Stat(crawl.Entries.Length, "total public entries");
        Stat(crawl.Entries.Count(e => e.Observation.HomepageStatusCode.HasValue), "homepage responses observed");
        Stat(crawl.Entries.Count(e => e.Observation.Title != null), "entries with titles");
        foreach (var state in Enum.GetValues<I2pPublicSummaryState>())
            Add("states", $"<option value=\"{E(state.ToString())}\">{E(I2pViewerGenerator.StateLabel(state))}</option>");
        if (crawl.Entries.Length == 0) Add("empty", "<p>No public crawl entries.</p>");
        var parents = crawl.Entries.OfType<I2pPublicCrawlEntry.KnownSeed>()
            .ToDictionary(k => (k.Hostname, k.DestinationHostname), k => Id(k));
        var hostParents = crawl.Entries.OfType<I2pPublicCrawlEntry.DiscoveredHost>()
            .ToDictionary(h => h.Hostname, h => Id(h), StringComparer.Ordinal);
        foreach (var entry in crawl.Entries)
        {
            token.ThrowIfCancellationRequested();
            var id = Id(entry);
            var e = entry.Observation;
            var kind = entry is I2pPublicCrawlEntry.KnownSeed ? "Known seed" : "Discovered host";
            var title = e.Title ?? "(no title)";
            var state = I2pViewerGenerator.StateLabel(e.SummaryState);
            var referrer = entry is I2pPublicCrawlEntry.DiscoveredHost host ? ParentLink(host) : "None (seed)";
            Add("rows", $"""
                <tr data-entry="{id}" data-hostname="{E(entry.Hostname)}" data-kind="{E(entry.Kind.ToString())}" data-summary="{E(e.SummaryState.ToString())}" data-http="{Number(e.HomepageStatusCode)}" data-search="{E(entry.Hostname + " " + (e.Title ?? ""))}">
                <td headers="column-kind"><span class="badge">{E(kind)}</span></td>
                <th scope="row" headers="column-hostname"><a href="#{id}">{E(entry.Hostname)}</a> · {Link(entry.Hostname, "Open ↗", directoryAction: true)}</th>
                <td headers="column-depth">{entry.Depth}</td><td headers="column-title">{E(title)}</td>
                <td headers="column-summary">{E(state)}</td><td headers="column-http">{E(Number(e.HomepageStatusCode) is { Length: > 0 } http ? http : "Not observed")}</td>
                <td headers="column-referrer">{referrer}</td></tr>
                """);
            Add("details", $"<article class=\"crawl-detail\" id=\"{id}\" aria-labelledby=\"title-{id}\"><h3 id=\"title-{id}\">{E(entry.Hostname)}</h3><dl>");
            Field("Kind", kind);
            Field("Hostname", entry.Hostname);
            Field("Depth", entry.Depth.ToString(CultureInfo.InvariantCulture));
            if (entry is I2pPublicCrawlEntry.KnownSeed known)
            {
                Field("Destination-derived B32 hostname", known.DestinationHostname);
                Field("Referrer", "None (seed)");
                Add("details", $"</dl><p>{Link(known.Hostname, "Open friendly hostname in I2P")} · {Link(known.DestinationHostname, "Open destination-derived B32 in I2P")}</p><dl>");
            }
            else if (entry is I2pPublicCrawlEntry.DiscoveredHost discovered)
            {
                Field("Host form", discovered.HostForm.ToString());
                Field("Identity", "Host-only reference; no imported destination identity");
                Add("details", $"<dt>Referrer hostname</dt><dd>{ParentLink(discovered)}</dd>");
                if (discovered.ReferrerDestinationHostname != null)
                    Field("Referrer destination-derived B32 hostname", discovered.ReferrerDestinationHostname);
                else Field("Referrer host form (host-only identity)", discovered.ReferrerHostForm?.ToString());
                Add("details", $"</dl><p>{Link(discovered.Hostname, "Open observed hostname in I2P")}</p><dl>");
            }
            Field("Summary state", state + " (" + e.SummaryState + ")");
            Field("Robots request outcome", e.RobotsRequestOutcome?.ToString());
            Field("Robots proxy classification", e.RobotsProxyClassification.ToString());
            Field("Robots observation", e.RobotsObservation.ToString());
            Field("Robots HTTP status", Number(e.RobotsStatusCode));
            Field("Homepage attempted", e.HomepageRequestAttempted is { } attempted ? attempted ? "Yes" : "No" : null);
            Field("Homepage request outcome", e.HomepageRequestOutcome?.ToString());
            Field("Homepage proxy classification", e.HomepageProxyClassification?.ToString());
            Field("Homepage HTTP status", Number(e.HomepageStatusCode));
            Field("Homepage content type", e.HomepageContentType);
            Field("Title", e.Title);
            Field("Extraction incomplete", e.LinkExtractionIncomplete ? "Yes" : "No");
            Add("details", "</dl><h4>Discovered I2P hostnames</h4>");
            if (e.LinkExtractionIncomplete) Add("details", "<p>Extraction is incomplete; the list may omit referenced hosts.</p>");
            if (e.DiscoveredI2pHostnames.IsEmpty) Add("details", "<p>No I2P hostnames recorded.</p>");
            else
            {
                Add("details", "<ul>");
                foreach (var name in e.DiscoveredI2pHostnames) Add("details", "<li>" + Link(name, name) + "</li>");
                Add("details", "</ul>");
            }
            Add("details", "<p><a href=\"#directory\">Back to directory</a></p></article>");
        }
        token.ThrowIfCancellationRequested();
        // One pass: public text resembling a placeholder can never become a template instruction.
        return Regex.Replace(template, @"\{\{(\w+)\}\}", m => values[m.Groups[1].Value].ToString());

        void Add(string area, string text)
        {
            token.ThrowIfCancellationRequested();
            if (retained + text.Length > htmlLimit) throw new InvalidDataException("Crawl viewer exceeds its HTML size limit.");
            retained += text.Length;
            values[area].Append(text);
        }
        void Stat(int count, string label) => Add("stats", $"<div class=\"stat\"><strong>{count}</strong> {E(label)}</div>");
        void Field(string label, string? value) => Add("details", $"<dt>{E(label)}</dt><dd>{E(string.IsNullOrEmpty(value) ? "Not observed" : value)}</dd>");
        string ParentLink(I2pPublicCrawlEntry.DiscoveredHost host)
        {
            var found = host.ReferrerDestinationHostname is { } b32
                ? parents.TryGetValue((host.ReferrerHostname, b32), out var parent)
                : hostParents.TryGetValue(host.ReferrerHostname, out parent);
            return found ? $"<a href=\"#{parent}\">{E(host.ReferrerHostname)}</a>" : E(host.ReferrerHostname);
        }
    }

    internal static string Id(I2pPublicCrawlEntry entry)
    {
        var material = entry switch
        {
            I2pPublicCrawlEntry.KnownSeed known => "KnownSeed\n" + known.Hostname + "\n" + known.DestinationHostname,
            I2pPublicCrawlEntry.DiscoveredHost host => "DiscoveredHost\n" + host.Hostname + "\n" + host.ReferrerHostname + "\n" +
                (host.ReferrerDestinationHostname ?? "HostOnly:" + host.ReferrerHostForm),
            _ => throw new InvalidDataException("Unsupported crawl entry kind.")
        };
        return "i2p-crawl-entry-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }
    private static string Link(string host, string label, bool directoryAction = false)
    {
        try
        {
            if (I2pSeed.NormalizeHostname(host) == host)
                return $"<a href=\"http://{E(host)}/\" target=\"_blank\" rel=\"noopener noreferrer\"{(directoryAction ? " data-crawl-open" : "")}>{E(label)}</a>";
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException) { }
        return E(label);
    }
    private static string E(string value) => WebUtility.HtmlEncode(value);
    private static string Number(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";
    private static string Time(DateTimeOffset value) => $"<time datetime=\"{E(value.ToString("O", CultureInfo.InvariantCulture))}\">{E(value.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture))}</time>";
}
