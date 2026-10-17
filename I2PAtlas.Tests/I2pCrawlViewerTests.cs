using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using I2PAtlas.I2pPublishing;
using I2PAtlas.I2pReporting;

namespace I2PAtlas.Tests;

[TestClass]
public sealed class I2pCrawlViewerTests
{
    [TestMethod]
    public async Task ValidPublicCrawlRendersCompleteDeterministicStaticDirectoryAndDetailsWithoutNetworking()
    {
        using var files = new TemporaryFiles();
        var publicCrawl = Fixture();
        var input = Write(files, publicCrawl);
        var original = await File.ReadAllBytesAsync(input);
        var output = Path.Combine(files.DirectoryPath, "crawl.html");
        using var network = new I2pCrawlProjectionTests.NetworkObserver();
        var read = await new I2pCrawlViewerGenerator().GenerateAsync(input, output);
        var html = await File.ReadAllTextAsync(output);
        Assert.HasCount(4, read.Entries);
        Assert.HasCount(4, Regex.Matches(html, "<tr data-entry="));
        Assert.HasCount(4, Regex.Matches(html, "<article class=\"crawl-detail\""));
        Assert.Contains("Crawl started UTC:", html);
        Assert.Contains("Crawl completed UTC:", html);
        Assert.Contains("2026-10-16 02:00:00 UTC", html);
        foreach (var stat in new[] { "<strong>2</strong> known seeds", "<strong>2</strong> discovered hosts", "<strong>4</strong> total public entries",
            "<strong>3</strong> homepage responses observed", "<strong>3</strong> entries with titles" }) Assert.Contains(stat, html);
        foreach (var entry in read.Entries)
        {
            var detail = Detail(html, entry);
            Assert.Contains(WebUtility.HtmlEncode(entry.Hostname), detail);
            foreach (var label in new[] { "Summary state", "Robots request outcome", "Robots proxy classification", "Robots observation", "Robots HTTP status",
                "Homepage attempted", "Homepage request outcome", "Homepage proxy classification", "Homepage HTTP status", "Homepage content type", "Title",
                "Discovered I2P hostnames", "Extraction incomplete" }) Assert.Contains(label, detail);
            Assert.Contains("href=\"#" + I2pCrawlViewerGenerator.Id(entry) + "\"", html);
        }
        foreach (Match tag in Regex.Matches(html, "<(?:tr data-entry=|article class=\"crawl-detail\")[^>]*>")) Assert.DoesNotContain(" hidden", tag.Value);
        Assert.Contains("<noscript>", html);
        Assert.Contains("id=\"crawl-controls\" class=\"controls\" hidden", html);
        Assert.Contains("observations through an unauthenticated proxy", html);
        Assert.Contains("not origin verification, ownership or liveness claims", html);
        await new I2pCrawlViewerGenerator().GenerateAsync(input, output);
        Assert.AreEqual(html, await File.ReadAllTextAsync(output));
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(input));
        Assert.IsEmpty(network.Activity);
        Assert.IsEmpty(Directory.GetFiles(files.DirectoryPath, ".i2p-crawl-view-*.tmp"));
    }

    [TestMethod]
    public void KnownAndDiscoveredKindsKeepTheirIdentitiesAndExactReferrersDistinct()
    {
        var crawl = Fixture();
        var html = I2pCrawlViewerGenerator.GenerateHtml(crawl);
        var known = crawl.Entries.OfType<I2pPublicCrawlEntry.KnownSeed>().ToArray();
        Assert.AreEqual(known[0].Hostname, known[1].Hostname);
        Assert.AreNotEqual(known[0].DestinationHostname, known[1].DestinationHostname);
        Assert.AreNotEqual(I2pCrawlViewerGenerator.Id(known[0]), I2pCrawlViewerGenerator.Id(known[1]));
        foreach (var seed in known)
        {
            var detail = Detail(html, seed);
            Assert.Contains("Known seed", detail);
            Assert.Contains("<dt>Depth</dt><dd>0</dd>", detail);
            Assert.Contains("<dt>Destination-derived B32 hostname</dt><dd>" + seed.DestinationHostname, detail);
            Assert.Contains("<dt>Referrer</dt><dd>None (seed)</dd>", detail);
            Assert.Contains($"href=\"http://{seed.DestinationHostname}/\"", detail);
        }
        foreach (var host in crawl.Entries.OfType<I2pPublicCrawlEntry.DiscoveredHost>())
        {
            var detail = Detail(html, host);
            Assert.Contains("Discovered host", detail);
            Assert.Contains("<dt>Depth</dt><dd>1</dd>", detail);
            Assert.Contains("<dt>Host form</dt><dd>" + host.HostForm, detail);
            Assert.Contains("Host-only reference; no imported destination identity", detail);
            Assert.DoesNotContain("<dt>Destination-derived B32 hostname</dt>", detail);
            var parent = known.Single(k => k.Hostname == host.ReferrerHostname && k.DestinationHostname == host.ReferrerDestinationHostname);
            Assert.Contains($"href=\"#{I2pCrawlViewerGenerator.Id(parent)}\">{host.ReferrerHostname}</a>", detail);
            Assert.Contains("<dt>Referrer destination-derived B32 hostname</dt><dd>" + host.ReferrerDestinationHostname, detail);
            Assert.Contains($"href=\"http://{host.Hostname}/\"", detail);
            Assert.DoesNotContain($"href=\"http://{host.ReferrerDestinationHostname}/\"", detail);
        }
    }

    [TestMethod]
    public async Task HostileButValidTitleIsTextAndUnicodeAndPlaceholderTextRoundTrip()
    {
        using var files = new TemporaryFiles();
        const string title = "Grüße 東京 🧅 <script>alert(1)</script><img src=x onerror='bad'> & \"quote\" {{rows}}";
        var crawl = Fixture();
        var seed = (I2pPublicCrawlEntry.KnownSeed)crawl.Entries[0];
        crawl = new(crawl.CrawlStartedAtUtc, crawl.CrawlCompletedAtUtc,
            crawl.Entries.SetItem(0, new I2pPublicCrawlEntry.KnownSeed(seed.Hostname, seed.DestinationHostname, seed.Observation with { Title = title })));
        var output = Path.Combine(files.DirectoryPath, "crawl.html");
        await new I2pCrawlViewerGenerator().GenerateAsync(Write(files, crawl), output);
        var html = await File.ReadAllTextAsync(output);
        Assert.Contains(WebUtility.HtmlEncode(title), html);
        Assert.DoesNotContain("<script>alert", html);
        Assert.DoesNotContain("<img src=x", html);
        Assert.Contains("{{rows}}", html); // Never recursively treated as template content.
        Assert.Contains(title, WebUtility.HtmlDecode(html));
        Assert.HasCount(1, Regex.Matches(html, "<script>"));
        Assert.HasCount(4, Regex.Matches(html, "<tr data-entry="));
    }

    [TestMethod]
    public void RendererEncodesAllStringsAndRefusesUnsafeLinksEvenIfInternalCallerBypassesReader()
    {
        const string hostile = "\"><svg onload='bad()'> & \".i2p";
        var crawl = Fixture();
        var observation = crawl.Entries[0].Observation with { Title = hostile, HomepageContentType = hostile, DiscoveredI2pHostnames = [hostile] };
        var known = new I2pPublicCrawlEntry.KnownSeed(hostile, hostile + ".b32.i2p", observation);
        var discovered = new I2pPublicCrawlEntry.DiscoveredHost(hostile + "child", I2pPublicCrawlHostForm.Friendly, hostile, hostile, observation);
        var html = I2pCrawlViewerGenerator.GenerateHtml(new(crawl.CrawlStartedAtUtc, crawl.CrawlCompletedAtUtc, [known, discovered]));
        Assert.Contains(WebUtility.HtmlEncode(hostile), html);
        Assert.DoesNotContain("<svg", html);
        Assert.DoesNotContain("href=\"http://", html);
        Assert.HasCount(2, Regex.Matches(html, "<article class=\"crawl-detail\""));
    }

    [TestMethod]
    public void HtmlContainsOnlyExplicitRootLinksNoPrivateDataAndNoAutomaticNetworkResources()
    {
        var html = I2pCrawlViewerGenerator.GenerateHtml(Fixture());
        foreach (var destination in CrawlProjectionTestData.PrivateArtifact().KnownSeedResults.Select(k => k.Destination)) Assert.DoesNotContain(destination, html);
        foreach (var privateValue in new[] { "127.0.0.2", "43891", "PRIVATE_CHARSET", "PRIVATE_PATH", "PRIVATE_QUERY", "PRIVATE_REDIRECT",
            "UnexpectedProbeFailure", "partial request state", "fileSha256", "sourceSha256", "durationMilliseconds", "sourcePath", "i2paddresshelper" }) Assert.DoesNotContain(privateValue, html);
        Assert.Contains("default-src 'none'", html);
        Assert.Contains("connect-src 'none'", html);
        Assert.Contains("base-uri 'none'", html);
        Assert.Contains("form-action 'none'", html);
        Assert.Contains("<link rel=\"icon\" href=\"data:,\">", html);
        Assert.DoesNotContain("<iframe", html);
        Assert.DoesNotContain("<img", html);
        Assert.IsFalse(Regex.IsMatch(html, "<(?:script|link|img)[^>]+(?:src|href)=\"https?://", RegexOptions.IgnoreCase));
        var script = Regex.Match(html, "<script>([\\s\\S]*?)</script>").Groups[1].Value;
        foreach (var unsafeApi in new[] { "fetch(", "XMLHttpRequest", "WebSocket", "EventSource", "sendBeacon", "innerHTML", "insertAdjacentHTML", "document.write", "history.", "const crawl", "a.i2p" }) Assert.DoesNotContain(unsafeApi, script);
        var external = Regex.Matches(html, "<a href=\"(http[^\"]*)\"[^>]*>");
        Assert.IsNotEmpty(external.Cast<Match>().ToArray());
        foreach (Match anchor in external)
        {
            var url = new Uri(WebUtility.HtmlDecode(anchor.Groups[1].Value));
            Assert.AreEqual("http", url.Scheme);
            Assert.IsTrue(url.Host.EndsWith(".i2p", StringComparison.Ordinal));
            Assert.AreEqual("/", url.AbsolutePath);
            Assert.AreEqual("", url.Query + url.Fragment + url.UserInfo);
            Assert.Contains("target=\"_blank\"", anchor.Value);
            Assert.Contains("rel=\"noopener noreferrer\"", anchor.Value);
        }
    }

    [TestMethod]
    public void StableIdsIncludeOnlyKindAndDeclaredIdentityOrExactParentProvenance()
    {
        var crawl = Fixture();
        var known = (I2pPublicCrawlEntry.KnownSeed)crawl.Entries[0];
        var host = (I2pPublicCrawlEntry.DiscoveredHost)crawl.Entries[2];
        Assert.AreEqual(Hash("KnownSeed\n" + known.Hostname + "\n" + known.DestinationHostname), I2pCrawlViewerGenerator.Id(known));
        Assert.AreEqual(Hash("DiscoveredHost\n" + host.Hostname + "\n" + host.ReferrerHostname + "\n" + host.ReferrerDestinationHostname), I2pCrawlViewerGenerator.Id(host));
        var changed = known.Observation with { Title = "Changed", HomepageStatusCode = 403, SummaryState = I2pPublicSummaryState.Ambiguous,
            HomepageContentType = "text/plain", LinkExtractionIncomplete = true, DiscoveredI2pHostnames = [] };
        Assert.AreEqual(I2pCrawlViewerGenerator.Id(known), I2pCrawlViewerGenerator.Id(new I2pPublicCrawlEntry.KnownSeed(known.Hostname, known.DestinationHostname, changed)));
        Assert.AreEqual(I2pCrawlViewerGenerator.Id(host), I2pCrawlViewerGenerator.Id(new I2pPublicCrawlEntry.DiscoveredHost(host.Hostname, host.HostForm, host.ReferrerHostname, host.ReferrerDestinationHostname, changed)));
        Assert.AreNotEqual(I2pCrawlViewerGenerator.Id(host), I2pCrawlViewerGenerator.Id(new I2pPublicCrawlEntry.DiscoveredHost(host.Hostname, host.HostForm, host.ReferrerHostname, CrawlTestData.ClassicB32, host.Observation)));
        var reordered = new I2pPublicCrawl(crawl.CrawlStartedAtUtc.AddDays(1), crawl.CrawlCompletedAtUtc.AddDays(1), crawl.Entries.Reverse().ToImmutableArray());
        CollectionAssert.AreEquivalent(crawl.Entries.Select(I2pCrawlViewerGenerator.Id).ToArray(), reordered.Entries.Select(I2pCrawlViewerGenerator.Id).ToArray());
        static string Hash(string text) => "i2p-crawl-entry-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    [TestMethod]
    [DataRow("schema")][DataRow("version")][DataRow("unknown-private")][DataRow("hostname")][DataRow("depth")]
    [DataRow("referrer")][DataRow("duplicate")][DataRow("title-bound")][DataRow("json")]
    public async Task StrictPublicValidationPreservesPreviousHtml(string failure)
    {
        using var files = new TemporaryFiles();
        var root = JsonNode.Parse(JsonSerializer.Serialize(Fixture(), I2pCrawlProjectionService.JsonOptions))!;
        switch (failure)
        {
            case "schema": root["schema"] = "i2patlas.raw-crawl"; break;
            case "version": root["version"] = 2; break;
            case "unknown-private": root["sourcePath"] = "PRIVATE_INPUT_PATH"; break;
            case "hostname": root["entries"]![0]!["hostname"] = "\"><img src=x>.i2p"; break;
            case "depth": root["entries"]![2]!["depth"] = 0; break;
            case "referrer": root["entries"]![2]!["referrerHostname"] = "absent.i2p"; break;
            case "duplicate": root["entries"]![1] = root["entries"]![0]!.DeepClone(); break;
            case "title-bound": root["entries"]![0]!["observation"]!["title"] = new string('x', 161); break;
        }
        var input = files.Write("public.json", failure == "json" ? "PRIVATE_INVALID_JSON" : root.ToJsonString());
        var output = files.Write("crawl.html", "previous HTML");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new I2pCrawlViewerGenerator().GenerateAsync(input, output));
        Assert.AreEqual("previous HTML", await File.ReadAllTextAsync(output));
        Assert.IsEmpty(Directory.GetFiles(files.DirectoryPath, ".i2p-crawl-view-*.tmp"));
    }

    [TestMethod]
    [DataRow("cancel-before")][DataRow("cancel-staged")][DataRow("io-staged")][DataRow("render-limit")]
    public async Task CancellationRenderingAndWritingFailuresPreservePriorOutputAndCleanStaging(string failure)
    {
        using var files = new TemporaryFiles();
        var input = Write(files, Fixture());
        var output = files.Write("crawl.html", "previous HTML");
        using var cancel = new CancellationTokenSource();
        if (failure == "cancel-before") cancel.Cancel();
        var generator = new I2pCrawlViewerGenerator(staged =>
        {
            using (File.Open(staged, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            Assert.Contains("</html>", File.ReadAllText(staged));
            if (!OperatingSystem.IsWindows()) Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(staged));
            if (failure == "cancel-staged") cancel.Cancel();
            if (failure == "io-staged") throw new IOException("PRIVATE_IO_ERROR");
        }, failure == "render-limit" ? 100 : I2pCrawlViewerGenerator.MaxHtmlCharacters);
        try { await generator.GenerateAsync(input, output, cancel.Token); Assert.Fail("Expected failure before replacement."); }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidDataException) { }
        Assert.AreEqual("previous HTML", await File.ReadAllTextAsync(output));
        Assert.IsEmpty(Directory.GetFiles(files.DirectoryPath, ".i2p-crawl-view-*.tmp"));
    }

    [TestMethod]
    public async Task EmptyAndUnknownObservationsRemainUsefulAndHtmlLimitIncludesTheCompleteTemplate()
    {
        using var files = new TemporaryFiles();
        var empty = new I2pPublicCrawl(Fixture().CrawlStartedAtUtc, Fixture().CrawlCompletedAtUtc, []);
        var input = Write(files, empty);
        var output = Path.Combine(files.DirectoryPath, "empty.html");
        await new I2pCrawlViewerGenerator().GenerateAsync(input, output);
        var html = await File.ReadAllTextAsync(output);
        Assert.Contains("No public crawl entries.", html);
        Assert.HasCount(0, Regex.Matches(html, "<tr data-entry="));
        Assert.AreEqual(html, I2pCrawlViewerGenerator.GenerateHtml(empty, htmlLimit: html.Length));
        Assert.ThrowsExactly<InvalidDataException>(() => I2pCrawlViewerGenerator.GenerateHtml(empty, htmlLimit: html.Length - 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new I2pCrawlViewerGenerator(null, I2pCrawlViewerGenerator.MaxHtmlCharacters + 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => I2pCrawlViewerGenerator.GenerateHtml(empty, htmlLimit: 0));
        var unknown = Fixture().Entries.Single(e => e.Observation.RobotsRequestOutcome == null);
        Assert.Contains("<dt>Homepage attempted</dt><dd>Not observed</dd>", Detail(I2pCrawlViewerGenerator.GenerateHtml(Fixture()), unknown));
    }

    internal static I2pPublicCrawl Fixture()
    {
        var projected = I2pPublicCrawlProjector.Project(CrawlProjectionTestData.PrivateArtifact(), default);
        return new(projected.CrawlStartedAtUtc, projected.CrawlCompletedAtUtc, projected.Entries.Select(e => e switch
        {
            I2pPublicCrawlEntry.DiscoveredHost h => (I2pPublicCrawlEntry)new I2pPublicCrawlEntry.DiscoveredHost(h.Hostname, h.HostForm, h.ReferrerHostname,
                h.ReferrerDestinationHostname, h.Observation with { Title = "Terminal metadata" }),
            _ => e
        }).ToImmutableArray());
    }
    internal static string Write(TemporaryFiles files, I2pPublicCrawl crawl) => files.Write("public-crawl.json", JsonSerializer.Serialize(crawl, I2pCrawlProjectionService.JsonOptions));
    private static string Detail(string html, I2pPublicCrawlEntry entry) => Regex.Match(html,
        "<article class=\"crawl-detail\" id=\"" + I2pCrawlViewerGenerator.Id(entry) + "\"[\\s\\S]*?</article>").Value;
}
