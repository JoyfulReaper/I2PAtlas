using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DN42Atlas.Commands;
using DN42Atlas.I2pProbing;
using DN42Atlas.I2pPublishing;
using DN42Atlas.I2pReporting;

namespace DN42Atlas.Tests;

[TestClass]
public sealed class I2pViewerTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    [TestMethod]
    public async Task ValidPublicV1ProducesDeterministicCompleteStaticHtmlFromPublicInputOnly()
    {
        using var files = new TemporaryFiles();
        var input = Write(files);
        var original = await File.ReadAllBytesAsync(input);
        var output = Path.Combine(files.DirectoryPath, "viewer.html");
        var scan = await new I2pViewerGenerator().GenerateAsync(input, output);
        var html = await File.ReadAllTextAsync(output);
        Assert.AreEqual(8, scan.SeedCount);
        Assert.AreEqual(6, scan.UniqueHostnameCount);
        Assert.HasCount(8, Regex.Matches(html, "<tr data-service="));
        Assert.HasCount(8, Regex.Matches(html, "<article class=\"card service-detail\""));
        Assert.Contains("<title>I2PAtlas</title>", html);
        Assert.Contains("<noscript>", html);
        Assert.Contains("Scan started:", html);
        Assert.Contains("Scan completed:", html);
        Assert.Contains("public destination entries", html);
        Assert.Contains("2026-10-16 00:00:00 UTC", html);
        Assert.Contains("id=\"controls\" hidden", html);
        Assert.Contains("<label for=\"homepage\">Homepage <select id=\"homepage\">", html);
        Assert.Contains("<option value=\"200\" selected>HTTP 200 observed</option>", html);
        Assert.Contains("<option value=\"response\">Any homepage response</option>", html);
        Assert.Contains("<option value=\"none\">No homepage response</option>", html);
        Assert.Contains("<option value=\"\">All entries</option>", html);
        // Filtering is enhancement only: every static row and detail remains present and unhidden.
        foreach (Match row in Regex.Matches(html, "<tr data-service=[^>]*>"))
            Assert.DoesNotContain(" hidden", row.Value);
        var noResponse = scan.Entries.First(e => e.HomepageStatusCode == null);
        Assert.Contains("data-http=\"\"", html);
        Assert.Contains("id=\"" + I2pViewerGenerator.Id(noResponse) + "\"", html);
        foreach (Match article in Regex.Matches(html, "<article class=\"card service-detail\"[^>]*>"))
            Assert.DoesNotContain(" hidden", article.Value);
        Assert.DoesNotContain("const scan", html);
        Assert.DoesNotContain("innerHTML", html);
        foreach (var privateField in new[] { "destination\"", "seedInput", "sourcePath", "fileSha256", "addressBookSha256",
            "proxyAddress", "PRIVATE_ERROR", "i2paddresshelper", "private-raw-scan" })
            Assert.DoesNotContain(privateField, html);
        await new I2pViewerGenerator().GenerateAsync(input, output);
        Assert.AreEqual(html, await File.ReadAllTextAsync(output));
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(input));
    }

    [TestMethod]
    [DataRow("schema")][DataRow("version")][DataRow("version-string")][DataRow("unknown-root")]
    [DataRow("unknown-entry")][DataRow("missing-root")][DataRow("missing-entry")][DataRow("enum-number")]
    [DataRow("enum-case")][DataRow("enum-unknown")][DataRow("robots-enum")][DataRow("outcome-enum")]
    [DataRow("classification-enum")][DataRow("duplicate-entry")][DataRow("entry-order")]
    [DataRow("seed-count")][DataRow("unique-count")][DataRow("destination-count")][DataRow("negative-count")]
    [DataRow("hostname")][DataRow("hostname-case")][DataRow("hostname-dot")][DataRow("b32-length")]
    [DataRow("b32-alphabet")][DataRow("b32-unused-bits")][DataRow("links-order")][DataRow("links-duplicate")]
    [DataRow("links-url")][DataRow("links-hostile")][DataRow("title-length")][DataRow("title-control")]
    [DataRow("title-whitespace")][DataRow("content-type")][DataRow("http-code")][DataRow("http-string")]
    [DataRow("summary-mismatch")][DataRow("attempt-mismatch")][DataRow("timestamp-order")][DataRow("non-utc")]
    [DataRow("null-entries")][DataRow("wrong-boolean")][DataRow("too-many-links")]
    public async Task StrictReaderRejectsMalformedContractWithoutReplacingHtml(string change)
    {
        using var files = new TemporaryFiles();
        var root = Node();
        var entries = root["entries"]!.AsArray();
        var page = entries.Single(e => e!["summaryState"]!.GetValue<string>() == "HomepageResponseObserved")!;
        var first = entries[0]!;
        switch (change)
        {
            case "schema": root["schema"] = "i2patlas.raw-scan"; break;
            case "version": root["version"] = 2; break;
            case "version-string": root["version"] = "1"; break;
            case "unknown-root": root["sourcePath"] = "PRIVATE_PATH_SENTINEL"; break;
            case "unknown-entry": first["destination"] = new string('A', 516); break;
            case "missing-root": root.AsObject().Remove("seedCount"); break;
            case "missing-entry": first.AsObject().Remove("title"); break;
            case "enum-number": first["summaryState"] = 0; break;
            case "enum-case": first["summaryState"] = "ambiguous"; break;
            case "enum-unknown": first["summaryState"] = "Verified"; break;
            case "robots-enum": first["robotsObservation"] = "Yes"; break;
            case "outcome-enum": first["robotsRequestOutcome"] = "Invalid"; break;
            case "classification-enum": first["robotsProxyClassification"] = "Authenticated"; break;
            case "duplicate-entry": entries[1] = first.DeepClone(); break;
            case "entry-order": root["entries"] = new JsonArray(entries.Reverse().Select(e => e!.DeepClone()).ToArray()); break;
            case "seed-count": root["seedCount"] = 9; break;
            case "unique-count": root["uniqueHostnameCount"] = 0; break;
            case "destination-count": first["destinationsForHostname"] = 3; break;
            case "negative-count": root["seedCount"] = -1; break;
            case "hostname": first["hostname"] = "\"><img src=x>.i2p"; break;
            case "hostname-case": first["hostname"] = "UPPER.i2p"; break;
            case "hostname-dot": first["hostname"] = "a.i2p."; break;
            case "b32-length": first["destinationHostname"] = "abc.b32.i2p"; break;
            case "b32-alphabet": first["destinationHostname"] = new string('0', 52) + ".b32.i2p"; break;
            case "b32-unused-bits": first["destinationHostname"] = new string('a', 51) + "b.b32.i2p"; break;
            case "links-order": page["discoveredI2pHostnames"] = new JsonArray("z.i2p", "a.i2p"); break;
            case "links-duplicate": page["discoveredI2pHostnames"] = new JsonArray("a.i2p", "a.i2p"); break;
            case "links-url": page["discoveredI2pHostnames"] = new JsonArray("http://a.i2p/private?secret=1"); break;
            case "links-hostile": page["discoveredI2pHostnames"] = new JsonArray("<script>.i2p"); break;
            case "title-length": page["title"] = new string('x', 161); break;
            case "title-control": page["title"] = "a\u202eb"; break;
            case "title-whitespace": page["title"] = " title "; break;
            case "content-type": page["homepageContentType"] = "text/html\"><img src=x>"; break;
            case "http-code": page["homepageStatusCode"] = 1000; break;
            case "http-string": page["homepageStatusCode"] = "200"; break;
            case "summary-mismatch": page["summaryState"] = "RobotsDisallowed"; break;
            case "attempt-mismatch": page["homepageRequestAttempted"] = false; break;
            case "timestamp-order": root["scanCompletedAtUtc"] = "2026-10-15T00:00:00Z"; break;
            case "non-utc": root["scanStartedAtUtc"] = "2026-10-15T20:00:00-04:00"; break;
            case "null-entries": root["entries"] = null; break;
            case "wrong-boolean": page["linkExtractionIncomplete"] = "true"; break;
            case "too-many-links": page["discoveredI2pHostnames"] = new JsonArray(Enumerable.Range(0, 251).Select(i => (JsonNode?)JsonValue.Create($"h{i:D3}.i2p")).ToArray()); break;
        }
        var input = files.Write("public.json", root.ToJsonString());
        var output = files.Write("viewer.html", "previous HTML");
        await Assert.ThrowsAsync<InvalidDataException>(() => new I2pViewerGenerator().GenerateAsync(input, output));
        Assert.AreEqual("previous HTML", await File.ReadAllTextAsync(output));
        Assert.IsEmpty(Directory.GetFiles(files.DirectoryPath, ".i2p-view-*.tmp"));
    }

    [TestMethod]
    [DataRow("{")][DataRow("{}")][DataRow("null")]
    [DataRow("{\"schema\":\"i2patlas.public-scan\",\"schema\":\"i2patlas.public-scan\"}")]
    public async Task MalformedJsonAndDuplicatePropertiesAreRejected(string json)
    {
        using var files = new TemporaryFiles();
        await Assert.ThrowsAsync<InvalidDataException>(() => I2pPublicScanReader.ReadAsync(files.Write("public.json", json)));
    }

    [TestMethod]
    public async Task BoundsEncodingAndNullTypesAreEnforcedWithoutReadingPrivateArtifacts()
    {
        using var files = new TemporaryFiles();
        var input = files.Write("oversized.json", "");
        using (var file = new FileStream(input, FileMode.Open, FileAccess.Write)) file.SetLength(I2pPublicScanReader.MaxArtifactBytes + 1L);
        await Assert.ThrowsAsync<InvalidDataException>(() => I2pPublicScanReader.ReadAsync(input));
        await File.WriteAllBytesAsync(input, [0xff, 0xfe, 0xff]);
        await Assert.ThrowsAsync<InvalidDataException>(() => I2pPublicScanReader.ReadAsync(input));
        var root = Node();
        root["seedCount"] = 10001;
        await Assert.ThrowsAsync<InvalidDataException>(() => I2pPublicScanReader.ReadAsync(files.Write("count.json", root.ToJsonString())));
    }

    [TestMethod]
    [DataRow(true)][DataRow(false)]
    public async Task DuplicatePropertyNamesInOtherwiseValidPublicDocumentsAreRejected(bool rootProperty)
    {
        using var files = new TemporaryFiles();
        var json = Node().ToJsonString();
        var property = rootProperty ? "schema" : "hostname";
        var value = rootProperty ? "i2patlas.public-scan" : "i2p-javadocs.i2p";
        json = json.Replace("\"" + property + "\":\"" + value + "\"", "\"" + property + "\":\"" + value + "\",\"" + property + "\":\"" + value + "\"", StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidDataException>(() => I2pPublicScanReader.ReadAsync(files.Write("duplicate.json", json)));
    }

    [TestMethod]
    [DataRow(I2pRobotsObservation.NotObserved)][DataRow(I2pRobotsObservation.UnknownStatus)]
    [DataRow(I2pRobotsObservation.Redirect)][DataRow(I2pRobotsObservation.UnsupportedRepresentation)]
    [DataRow(I2pRobotsObservation.TooLarge)][DataRow(I2pRobotsObservation.Malformed)]
    [DataRow(I2pRobotsObservation.AllowsRoot)][DataRow(I2pRobotsObservation.DisallowsRoot)]
    public async Task EveryPublicRobotsObservationCanBeReadWithItsPublicMeaning(I2pRobotsObservation observation)
    {
        using var files = new TemporaryFiles();
        var scan = Fixture();
        var entry = scan.Entries[^1] with { RobotsObservation = observation,
            RobotsStatusCode = observation == I2pRobotsObservation.NotObserved ? null : observation == I2pRobotsObservation.Redirect ? 302 :
                observation == I2pRobotsObservation.UnknownStatus ? 403 : 200,
            RobotsRequestOutcome = observation == I2pRobotsObservation.NotObserved ? I2pRequestOutcome.RejectedBeforeNetworking : I2pRequestOutcome.ProxyHttpResponse,
            SummaryState = observation == I2pRobotsObservation.DisallowsRoot ? I2pPublicSummaryState.RobotsDisallowed :
                observation == I2pRobotsObservation.Redirect ? I2pPublicSummaryState.RobotsRedirect : I2pPublicSummaryState.Ambiguous };
        var one = new I2pPublicScan(scan.ScanStartedAtUtc, scan.ScanCompletedAtUtc, [entry]);
        var result = await I2pPublicScanReader.ReadAsync(files.Write("observation.json", JsonSerializer.Serialize(one, JsonOptions)));
        Assert.AreEqual(observation, result.Entries[0].RobotsObservation);
    }

    [TestMethod]
    public void BothKnownMultiDestinationNamesHaveIndependentStableIdentityHashes()
    {
        var scan = Fixture();
        foreach (var hostname in new[] { "i2p-javadocs.i2p", "plugins.i2p" })
        {
            var entries = scan.Entries.Where(e => e.Hostname == hostname).ToArray();
            Assert.HasCount(2, entries);
            Assert.AreNotEqual(I2pViewerGenerator.Id(entries[0]), I2pViewerGenerator.Id(entries[1]));
            var changed = entries[0] with { Title = "Changed title", SummaryState = I2pPublicSummaryState.Ambiguous };
            Assert.AreEqual(I2pViewerGenerator.Id(entries[0]), I2pViewerGenerator.Id(changed));
        }
        var html = I2pViewerGenerator.GenerateHtml(scan);
        Assert.Contains("2 destinations", html);
        Assert.DoesNotContain("preferred destination is selected", html);
        foreach (var entry in scan.Entries)
            Assert.Contains("id=\"" + I2pViewerGenerator.Id(entry) + "\"", html);
    }

    [TestMethod]
    public void EscapingIsTheRendererBoundaryEvenForHostileStringsAndTemplateTokens()
    {
        const string hostile = "\"><img src=x onerror=alert(1)> & {{rows}} </script><script>alert(2)</script>";
        var entry = Fixture().Entries.Single(e => e.Title != null) with
        {
            Hostname = hostile + ".i2p", DestinationHostname = hostile + ".b32.i2p",
            Title = hostile, HomepageContentType = hostile, DiscoveredI2pHostnames = [hostile + ".i2p"]
        };
        var scan = new I2pPublicScan(Fixture().ScanStartedAtUtc, Fixture().ScanCompletedAtUtc, [entry]);
        var html = I2pViewerGenerator.GenerateHtml(scan);
        Assert.Contains(WebUtility.HtmlEncode(hostile), html);
        Assert.Contains("{{rows}}", html);
        Assert.DoesNotContain("<img", html);
        Assert.DoesNotContain("<script>alert", html);
        Assert.DoesNotContain("href=\"http://&quot;", html);
        Assert.HasCount(1, Regex.Matches(html, "</script>", RegexOptions.IgnoreCase));
    }

    [TestMethod]
    public void LabelsFiltersDiscoveredNavigationAndExplicitLinksKeepPublicMeaning()
    {
        var scan = Fixture();
        var html = I2pViewerGenerator.GenerateHtml(scan);
        foreach (var state in Enum.GetValues<I2pPublicSummaryState>())
        {
            Assert.Contains("<option value=\"" + state + "\">", html);
            Assert.IsFalse(Regex.IsMatch(I2pViewerGenerator.StateLabel(state),
                @"\b(reachable|online|offline|verified|authenticated|alive|dead)\b", RegexOptions.IgnoreCase));
        }
        foreach (var observation in Enum.GetValues<I2pRobotsObservation>())
            Assert.Contains("<option value=\"" + observation + "\">", html);
        foreach (var entry in scan.Entries)
            Assert.Contains("href=\"http://" + entry.DestinationHostname + "/\" target=\"_blank\" rel=\"noopener noreferrer\"", html);
        Assert.Contains("Open destination in I2P", html);
        Assert.Contains("depends on your I2P addressbook", html);
        Assert.Contains("Outside-seed references are discovery observations", html);
        Assert.Contains("href=\"http://outside.i2p/\"", html);
        foreach (var entry in scan.Entries.Where(e => e.Hostname == "plugins.i2p"))
            Assert.Contains("href=\"#" + I2pViewerGenerator.Id(entry) + "\"", html);
        Assert.DoesNotContain("/private-path", html);
        Assert.DoesNotContain("?private-query", html);
        Assert.Contains("Link extraction is incomplete", html);
        foreach (var resource in new[] { "<img", "<iframe", "<object", "<video", "<audio", "fetch(", "XMLHttpRequest", "WebSocket", "EventSource", "rel=\"prefetch", "rel=\"prerender", "rel=\"preconnect" })
            Assert.DoesNotContain(resource, html);
        Assert.Contains("default-src 'none'", html);
        Assert.Contains("href=\"data:,\"", html);
        Assert.Contains("@media (max-width: 600px)", html);
        Assert.Contains("role=\"table\"", html);
        Assert.Contains("headers=\"column-title row-i2p-service-", html);
    }

    [TestMethod]
    public async Task StagedWriteIsClosedAndCompleteBeforeReplacementAndFailuresPreservePreviousHtml()
    {
        using var files = new TemporaryFiles();
        var input = Write(files);
        var output = files.Write("viewer.html", "previous HTML");
        var observed = false;
        var generator = new I2pViewerGenerator(staged =>
        {
            Assert.AreEqual("previous HTML", File.ReadAllText(output));
            using var file = new FileStream(staged, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            using var reader = new StreamReader(file);
            Assert.Contains("</html>", reader.ReadToEnd());
            observed = true;
            throw new IOException("Injected replacement failure");
        });
        await Assert.ThrowsAsync<IOException>(() => generator.GenerateAsync(input, output));
        Assert.IsTrue(observed);
        Assert.AreEqual("previous HTML", await File.ReadAllTextAsync(output));
        Assert.IsEmpty(Directory.GetFiles(files.DirectoryPath, ".i2p-view-*.tmp"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => new I2pViewerGenerator().GenerateAsync(input, output, cancellation.Token));
        Assert.AreEqual("previous HTML", await File.ReadAllTextAsync(output));
    }

    [TestMethod]
    public async Task EmptyAndUnknownObservationsRemainUsable()
    {
        using var files = new TemporaryFiles();
        var scan = Fixture();
        var unknown = scan.Entries[^1] with { RobotsRequestOutcome = null, RobotsStatusCode = null,
            RobotsObservation = I2pRobotsObservation.NotObserved, RobotsProxyClassification = I2pProxyResponseClassification.Ambiguous,
            HomepageRequestAttempted = null, SummaryState = I2pPublicSummaryState.Ambiguous };
        var one = new I2pPublicScan(scan.ScanStartedAtUtc, scan.ScanCompletedAtUtc, [unknown]);
        var result = await I2pPublicScanReader.ReadAsync(files.Write("one.json", JsonSerializer.Serialize(one, JsonOptions)));
        Assert.IsNull(result.Entries[0].HomepageRequestAttempted);
        var empty = new I2pPublicScan(scan.ScanStartedAtUtc, scan.ScanCompletedAtUtc, []);
        var input = files.Write("empty.json", JsonSerializer.Serialize(empty, JsonOptions));
        await new I2pViewerGenerator().GenerateAsync(input, Path.Combine(files.DirectoryPath, "empty.html"));
        Assert.Contains("No public destination entries.", File.ReadAllText(Path.Combine(files.DirectoryPath, "empty.html")));
    }

    [TestMethod]
    [DataRow("usage", 2)][DataRow("missing", 1)][DataRow("directory", 1)][DataRow("same", 2)]
    [DataRow("invalid", 1)][DataRow("cancel", 130)][DataRow("success", 0)]
    public async Task CommandReportsUsefulErrorsAndPreservesPreviousHtml(string mode, int expected)
    {
        using var files = new TemporaryFiles();
        var input = Write(files);
        var output = files.Write("viewer.html", "previous HTML");
        using var cancel = new CancellationTokenSource();
        if (mode == "cancel") cancel.Cancel();
        if (mode == "invalid") File.WriteAllText(input, "PRIVATE_INVALID_SENTINEL");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var args = new[] { "i2p-view", mode == "missing" ? input + ".missing" : input,
            mode == "same" ? input : mode == "directory" ? Path.Combine(files.DirectoryPath, "absent", "view.html") : output };
        var code = await I2pViewCommand.ExecuteAsync(mode == "usage" ? ["i2p-view"] : args, stdout, stderr, cancel.Token);
        Assert.AreEqual(expected, code, stderr.ToString());
        Assert.DoesNotContain("PRIVATE_INVALID_SENTINEL", stderr.ToString());
        if (mode == "directory") { Assert.Contains("output directory does not exist", stderr.ToString()); Assert.DoesNotContain("Invalid public artifact", stderr.ToString()); }
        if (mode != "success") Assert.AreEqual("previous HTML", File.ReadAllText(output));
        else Assert.Contains("8 destination entries", stdout.ToString());
    }

    [TestMethod]
    public async Task CliDispatchRequiresOnlyPublicInputWithPrivateConfigurationUnavailable()
    {
        using var files = new TemporaryFiles();
        var input = Write(files);
        var output = Path.Combine(files.DirectoryPath, "viewer.html");
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = files.DirectoryPath, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in new[] { typeof(I2pViewCommand).Assembly.Location, "i2p-view", input, output }) start.ArgumentList.Add(arg);
        start.Environment["DN42ATLAS_RUNTIME_EXCLUSIONS_PATH"] = "DO-NOT-OPEN";
        start.Environment["DN42ATLAS_REGISTRY_PATH"] = "DO-NOT-OPEN";
        start.Environment["HTTP_PROXY"] = start.Environment["HTTPS_PROXY"] = start.Environment["ALL_PROXY"] = "http://invalid.invalid:1";
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.AreEqual(0, process.ExitCode, await stderr);
        Assert.Contains("I2PAtlas viewer installed", await stdout);
        Assert.IsTrue(File.Exists(output));
        Assert.IsFalse(Directory.Exists(Path.Combine(files.DirectoryPath, "config")));
    }

    private static string Write(TemporaryFiles files) => files.Write("public.json", JsonSerializer.Serialize(Fixture(), JsonOptions));
    private static JsonNode Node() => JsonSerializer.SerializeToNode(Fixture(), JsonOptions)!;
    internal static I2pPublicScan Fixture()
    {
        var entries = Enum.GetValues<I2pPublicSummaryState>().Select((state, i) =>
        {
            var host = i < 2 ? "i2p-javadocs.i2p" : i < 4 ? "plugins.i2p" : "state" + i + ".i2p";
            var outcome = state == I2pPublicSummaryState.RequestTimedOut ? I2pRequestOutcome.TimedOut :
                state == I2pPublicSummaryState.TransportFailure ? I2pRequestOutcome.ProxyUnavailable : I2pRequestOutcome.ProxyHttpResponse;
            var robots = i < 2 ? I2pRobotsObservation.AllowsRoot : i == 2 ? I2pRobotsObservation.DisallowsRoot :
                i == 6 ? I2pRobotsObservation.Redirect : i is 4 or 5 ? I2pRobotsObservation.NotObserved : I2pRobotsObservation.UnknownStatus;
            var classification = i < 3 ? I2pProxyResponseClassification.LikelyOriginResponse :
                i == 3 ? I2pProxyResponseClassification.LikelyProxyFailure : I2pProxyResponseClassification.Ambiguous;
            int? status = i < 3 ? 200 : i == 3 ? 504 : i is 4 or 5 ? null : i == 6 ? 302 : 403;
            return new I2pPublicEntry(host, new string((char)('a' + i), 51) + "a.b32.i2p", i < 4 ? 2 : 1, state,
                outcome, classification, robots, status, i < 2,
                i == 0 ? I2pRequestOutcome.ProxyHttpResponse : i == 1 ? I2pRequestOutcome.TimedOut : null,
                i == 0 ? I2pProxyResponseClassification.Ambiguous : null, i == 0 ? 200 : null,
                i == 0 ? "text/html" : null, i == 0 ? "Zulu title" : null,
                i == 0 ? [new string('a', 52) + ".b32.i2p", "outside.i2p", "plugins.i2p"] : [], i != 0);
        }).OrderBy(e => e.Hostname, StringComparer.Ordinal).ThenBy(e => e.DestinationHostname, StringComparer.Ordinal).ToImmutableArray();
        var started = DateTimeOffset.Parse("2026-10-16T00:00:00Z");
        return new(started, started.AddMinutes(2), entries);
    }
}
