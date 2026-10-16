using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using I2PAtlas.AddressBook;
using I2PAtlas.Commands;
using I2PAtlas.I2pProbing;
using I2PAtlas.I2pPublishing;
using I2PAtlas.I2pReporting;
using I2PAtlas.I2pScanning;
using I2PAtlas.Seeds;
using I2PAtlas.I2pTransport;

namespace I2PAtlas.Tests;

[TestClass]
public sealed class I2pProjectionTests
{
    private static readonly DateTimeOffset Started = DateTimeOffset.Parse("2026-10-16T00:00:00+00:00");
    private const string RedirectSentinel = "http://redirect-secret.i2p/private-redirect?secret=REDIRECT_PRIVATE";
    private const string ErrorSentinel = "PRIVATE_ERROR_DETAIL_SENTINEL";
    private const string CategorySentinel = "PRIVATE_ERROR_CATEGORY_SENTINEL";
    private const string PathSentinel = "PRIVATE_LINK_PATH_SENTINEL";
    private const string QuerySentinel = "PRIVATE_QUERY_SENTINEL";

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ImportScanProjectViewPreservesDistinctDestinationsAndDeduplicatesEquivalentSpellings(bool equivalentSpelling)
    {
        using var files = new TemporaryFiles();
        var first = SeedSnapshotTestData.Destination(30, payloadLength: 4);
        var second = equivalentSpelling ? first.TrimEnd('=') : SeedSnapshotTestData.Destination(31, payloadLength: 4);
        var expectedCount = equivalentSpelling ? 1 : 2;
        var export = files.Write("hosts.txt", $"plugins.i2p={first}\nplugins.i2p={second}");
        var snapshot = Path.Combine(files.DirectoryPath, "seeds.json");
        var imported = await new SeedSnapshotImporter().ImportAsync(export, snapshot);
        Assert.HasCount(expectedCount, imported.Seeds);
        Assert.AreEqual(first, imported.Seeds.Single(s => s.Destination == first).Destination);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serving = Task.Run(async () =>
        {
            for (var request = 0; request < expectedCount * 2; request++)
            {
                using var client = await listener.AcceptTcpClientAsync(stop.Token);
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var requestLine = await reader.ReadLineAsync(stop.Token);
                Assert.IsNotNull(requestLine);
                while (await reader.ReadLineAsync(stop.Token) is { Length: > 0 }) { }
                var robots = requestLine.Contains("/robots.txt", StringComparison.Ordinal);
                var body = robots ? "User-agent: *\nAllow: /" : "<title>Real scan shape</title><a href='http://target.i2p/path?query=secret'>inert</a>";
                var response = Encoding.UTF8.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: {(robots ? "text/plain" : "text/html")}\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}");
                await stream.WriteAsync(response, stop.Token);
            }
        }, stop.Token);
        var rawPath = Path.Combine(files.DirectoryPath, "private-scan.json");
        var raw = await new I2pScanService().ScanAsync(snapshot, rawPath, $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}", cancellationToken: stop.Token);
        await serving;
        Assert.AreEqual(expectedCount, raw.TotalSeeds);
        Assert.AreEqual(expectedCount, raw.Metrics.HomepageResponseCount);
        // Projection only reads the completed raw artifact, with its input/router no longer available.
        listener.Stop();
        File.Delete(snapshot);
        File.Delete(export);
        var publicPath = Path.Combine(files.DirectoryPath, "public.json");
        var projected = await new I2pProjectionService().ProjectAsync(rawPath, publicPath);
        Assert.AreEqual(expectedCount, projected.SeedCount);
        Assert.AreEqual(1, projected.UniqueHostnameCount);
        Assert.IsTrue(projected.Entries.All(e => e.Hostname == "plugins.i2p" && e.DestinationsForHostname == expectedCount));
        Assert.IsTrue(projected.Entries.All(e => e.Title == "Real scan shape" && !e.LinkExtractionIncomplete));
        Assert.IsTrue(projected.Entries.All(e => e.DiscoveredI2pHostnames.SequenceEqual(["target.i2p"])));
        CollectionAssert.AreEquivalent(raw.Results.Select(r => r.DestinationHostname).ToArray(), projected.Entries.Select(e => e.DestinationHostname).ToArray());
        var htmlPath = Path.Combine(files.DirectoryPath, "index.html");
        var viewed = await new I2pViewerGenerator().GenerateAsync(publicPath, htmlPath);
        Assert.AreEqual("i2patlas.public-scan", viewed.Schema);
        Assert.AreEqual(1, viewed.Version);
        Assert.AreEqual(expectedCount, viewed.SeedCount);
        var html = await File.ReadAllTextAsync(htmlPath);
        Assert.Contains("Real scan shape", html);
        foreach (var entry in projected.Entries)
        {
            Assert.Contains(entry.DestinationHostname, html);
            Assert.Contains(I2pViewerGenerator.Id(entry), html);
        }
        var publicJson = await File.ReadAllTextAsync(publicPath);
        foreach (var destination in new[] { first, second })
        {
            Assert.DoesNotContain(destination, html);
            Assert.DoesNotContain(destination, publicJson);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DuplicateSemanticRawIdentitiesAreRejectedAndProjectorNeverMergesObservations(bool differentSpelling)
    {
        using var files = new TemporaryFiles();
        var padded = SeedSnapshotTestData.Destination(42, payloadLength: 4);
        var b32 = I2pSeedIdentity.FromSeed(new I2pSeed("duplicate.i2p", padded)).DestinationHostname;
        var first = RobotResult("duplicate.i2p", 42, 403) with { Destination = padded, DestinationHostname = b32 };
        var second = RobotResult("duplicate.i2p", 42, 504) with
        {
            Destination = differentSpelling ? padded.TrimEnd('=') : padded,
            DestinationHostname = b32
        };
        // Each observation is valid v1 evidence on its own; only their shared identity is invalid.
        foreach (var observation in new[] { first, second })
            Assert.AreEqual(1, (await I2pRawScanReader.ReadAsync(Write(files, Artifact([observation])))).TotalSeeds);
        var raw = Artifact([first, second]);
        var input = Write(files, raw);
        var original = await File.ReadAllBytesAsync(input);
        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => I2pRawScanReader.ReadAsync(input));
        Assert.Contains("Duplicate semantic seed identity", error.Message);
        Assert.DoesNotContain(padded.TrimEnd('='), error.Message);
        var projectorError = Assert.ThrowsExactly<InvalidDataException>(() => I2pPublicProjector.Project(raw, CancellationToken.None));
        Assert.Contains("cannot be merged", projectorError.Message);
        var output = files.Write("public.json", "previous public artifact");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new I2pProjectionService().ProjectAsync(input, output));
        Assert.AreEqual("previous public artifact", await File.ReadAllTextAsync(output));
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(input));
    }

    [TestMethod]
    public async Task FullRawProjectionIsAnExplicitPublicAllowlistAndPreservesAlternateDestinations()
    {
        using var files = new TemporaryFiles();
        var raw = Artifact();
        var input = Write(files, raw);
        var original = await File.ReadAllBytesAsync(input);
        var directory = Directory.CreateDirectory(Path.Combine(files.DirectoryPath, "published")).FullName;
        var output = Path.Combine(directory, "i2p-latest.json");
        var projected = await new I2pProjectionService().ProjectAsync(input, output);
        Assert.AreEqual("i2patlas.public-scan", projected.Schema);
        Assert.AreEqual(1, projected.Version);
        Assert.AreEqual(8, projected.SeedCount);
        Assert.AreEqual(7, projected.UniqueHostnameCount);
        Assert.AreEqual(Started, projected.ScanStartedAtUtc);
        var shared = projected.Entries.Where(e => e.Hostname == "i2p-javadocs.i2p").ToArray();
        Assert.HasCount(2, shared);
        Assert.IsTrue(shared.All(e => e.DestinationsForHostname == 2));
        Assert.AreNotEqual(shared[0].DestinationHostname, shared[1].DestinationHostname);
        CollectionAssert.AreEqual(raw.Results.Where(r => r.Hostname == "i2p-javadocs.i2p").Select(r => r.DestinationHostname).Order(StringComparer.Ordinal).ToArray(),
            shared.Select(e => e.DestinationHostname).ToArray());
        Assert.IsTrue(projected.Entries.Where(e => e.Hostname != "i2p-javadocs.i2p").All(e => e.DestinationsForHostname == 1));
        CollectionAssert.AreEqual(projected.Entries.OrderBy(e => e.Hostname, StringComparer.Ordinal).ThenBy(e => e.DestinationHostname, StringComparer.Ordinal).ToArray(),
            projected.Entries.ToArray());
        var json = await File.ReadAllTextAsync(output);
        var node = JsonNode.Parse(json)!;
        CollectionAssert.AreEqual(new[] { "schema", "version", "scanStartedAtUtc", "scanCompletedAtUtc", "seedCount", "uniqueHostnameCount", "entries" },
            node.AsObject().Select(p => p.Key).ToArray());
        var allowed = new[] { "hostname", "destinationHostname", "destinationsForHostname", "summaryState", "robotsRequestOutcome",
            "robotsProxyClassification", "robotsObservation", "robotsStatusCode", "homepageRequestAttempted", "homepageRequestOutcome",
            "homepageProxyClassification", "homepageStatusCode", "homepageContentType", "title", "discoveredI2pHostnames", "linkExtractionIncomplete" };
        foreach (var entry in node["entries"]!.AsArray()) CollectionAssert.AreEqual(allowed, entry!.AsObject().Select(p => p.Key).ToArray());
        foreach (var privateValue in raw.Results.Select(r => r.Destination).Concat(new[] {
            raw.SeedInput.FileSha256, raw.SeedInput.AddressBookSha256, raw.Proxy.Address, raw.Proxy.Port.ToString(),
            raw.Settings.RequestTimeoutMilliseconds.ToString(), raw.Results[0].DurationMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            RedirectSentinel, ErrorSentinel, CategorySentinel, PathSentinel, QuerySentinel, "PRIVATE_USER", "PRIVATE_FRAGMENT",
            "clearnet-private.example", "i2paddresshelper", SeedSnapshotTestData.SourcePath, input, "PRIVATE_CHARSET_SENTINEL", "PRIVATE_RAW_ANNOTATION" }))
            Assert.DoesNotContain(privateValue, json);
        foreach (var name in new[] { "destination", "seedInput", "source", "sha256", "byteLength", "proxy", "settings", "concurrency",
            "durationMilliseconds", "errors", "redirectLocation", "links", "metadata", "requestTimeoutMilliseconds" })
            Assert.DoesNotContain('"' + name + '"', json);
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(input));
        Assert.IsEmpty(Directory.GetFiles(directory, ".i2p-public-*.tmp"));
        var firstBytes = await File.ReadAllBytesAsync(output);
        await new I2pProjectionService().ProjectAsync(input, output);
        CollectionAssert.AreEqual(firstBytes, await File.ReadAllBytesAsync(output));
    }

    [TestMethod]
    public async Task PublicLinksAreOnlyNormalizedUniqueSortedI2pHostnamesAndTitlesRemainText()
    {
        using var files = new TemporaryFiles();
        var result = await new I2pProjectionService().ProjectAsync(Write(files, Artifact()), Path.Combine(files.DirectoryPath, "public.json"));
        var page = result.Entries.Single(e => e.Hostname == "page.i2p");
        CollectionAssert.AreEqual(new[] { "abc.b32.i2p", "other.i2p", "target.i2p" }, page.DiscoveredI2pHostnames.ToArray());
        Assert.AreEqual("text/html", page.HomepageContentType);
        Assert.IsTrue(page.LinkExtractionIncomplete);
        Assert.AreEqual("<b>literal</b>" + new string('x', 146), page.Title);
        Assert.AreEqual(I2pPublicProjector.MaxTitleCharacters, page.Title!.Length);
        Assert.IsFalse(page.Title.Any(char.IsControl));
        Assert.DoesNotContain("\u202e", page.Title);
        Assert.DoesNotContain("\u2028", page.Title);
        Assert.AreEqual(200, page.HomepageStatusCode);
        Assert.AreEqual(I2pProxyResponseClassification.Ambiguous, page.HomepageProxyClassification);
        var json = await File.ReadAllTextAsync(Path.Combine(files.DirectoryPath, "public.json"));
        Assert.DoesNotContain("<b>", json); // Default JSON encoder escapes markup; viewer must still HTML-escape parsed strings.
    }

    [TestMethod]
    public async Task SummaryStatesRetainIndependentUnderlyingObservations()
    {
        using var files = new TemporaryFiles();
        var result = await new I2pProjectionService().ProjectAsync(Write(files, Artifact()), Path.Combine(files.DirectoryPath, "public.json"));
        var entries = result.Entries.ToDictionary(e => (e.Hostname, e.SummaryState));
        Assert.IsTrue(entries.ContainsKey(("page.i2p", I2pPublicSummaryState.HomepageResponseObserved)));
        Assert.IsTrue(entries.ContainsKey(("i2p-javadocs.i2p", I2pPublicSummaryState.HomepageAttemptFailed)));
        Assert.IsTrue(entries.ContainsKey(("disallow.i2p", I2pPublicSummaryState.RobotsDisallowed)));
        Assert.IsTrue(entries.ContainsKey(("i2p-javadocs.i2p", I2pPublicSummaryState.ProxyFailureObserved)));
        Assert.IsTrue(entries.ContainsKey(("timeout.i2p", I2pPublicSummaryState.RequestTimedOut)));
        Assert.IsTrue(entries.ContainsKey(("transport.i2p", I2pPublicSummaryState.TransportFailure)));
        Assert.IsTrue(entries.ContainsKey(("redirect.i2p", I2pPublicSummaryState.RobotsRedirect)));
        Assert.IsTrue(entries.ContainsKey(("ambiguous.i2p", I2pPublicSummaryState.Ambiguous)));
        var failed = result.Entries.Single(e => e.SummaryState == I2pPublicSummaryState.HomepageAttemptFailed);
        Assert.IsTrue(failed.HomepageRequestAttempted.GetValueOrDefault());
        Assert.AreEqual(I2pRequestOutcome.TimedOut, failed.HomepageRequestOutcome);
        Assert.AreEqual(I2pRobotsObservation.AllowsRoot, failed.RobotsObservation);
        foreach (var forbidden in new[] { "Reachable", "Online", "Offline", "Verified", "Authenticated" })
            Assert.DoesNotContain(forbidden, Enum.GetNames<I2pPublicSummaryState>());
    }

    [TestMethod]
    [DataRow("schema")]
    [DataRow("version")]
    [DataRow("version-string")]
    [DataRow("unknown-root")]
    [DataRow("unknown-nested")]
    [DataRow("missing-field")]
    [DataRow("wrong-type")]
    [DataRow("enum-number")]
    [DataRow("enum-case")]
    [DataRow("enum-unknown")]
    [DataRow("utc")]
    [DataRow("non-utc")]
    [DataRow("timestamp-order")]
    [DataRow("result-timestamp")]
    [DataRow("total-count")]
    [DataRow("snapshot-count")]
    [DataRow("seed-limit")]
    [DataRow("metrics")]
    [DataRow("metric-key")]
    [DataRow("metric-total")]
    [DataRow("concurrency")]
    [DataRow("timeout")]
    [DataRow("maximum-seeds")]
    [DataRow("active-count")]
    [DataRow("hash")]
    [DataRow("byte-length")]
    [DataRow("proxy")]
    [DataRow("hostname")]
    [DataRow("not-normalized")]
    [DataRow("destination")]
    [DataRow("destination-whitespace")]
    [DataRow("b32")]
    [DataRow("duplicate-seed")]
    [DataRow("unsorted-seeds")]
    [DataRow("title-bound")]
    [DataRow("body-bound")]
    [DataRow("status")]
    [DataRow("url-kind")]
    [DataRow("url-scheme")]
    [DataRow("link-bound")]
    [DataRow("url-bound")]
    [DataRow("header-bound")]
    [DataRow("attempt-gate")]
    [DataRow("homepage-response")]
    [DataRow("origin-evidence")]
    [DataRow("classification")]
    [DataRow("metadata-status")]
    [DataRow("error-bound")]
    [DataRow("duration")]
    public async Task InvalidRawEvidenceFailsExplicitlyAndPreservesExistingPublicFile(string failure)
    {
        using var files = new TemporaryFiles();
        var node = Node();
        var results = node["results"]!.AsArray();
        var entry = results.Single(r => r!["hostname"]!.GetValue<string>() == "page.i2p")!;
        var metadata = entry["homepageMetadata"]!;
        switch (failure)
        {
            case "schema": node["schema"] = "dn42atlas.raw-scan"; break;
            case "version": node["version"] = 2; break;
            case "version-string": node["version"] = "1"; break;
            case "unknown-root": node["PRIVATE_RAW_ANNOTATION"] = "private"; break;
            case "unknown-nested": metadata["PRIVATE_RAW_ANNOTATION"] = "private"; break;
            case "missing-field": entry.AsObject().Remove("homepageRequestOutcome"); break;
            case "wrong-type": entry["homepageRequestAttempted"] = "true"; break;
            case "enum-number": entry["robotsRequestOutcome"] = 4; break;
            case "enum-case": entry["robotsRequestOutcome"] = "proxyhttpresponse"; break;
            case "enum-unknown": entry["robotsRequestOutcome"] = "Authenticated"; break;
            case "utc": node["startedAtUtc"] = "2026-10-16T00:00:00"; break;
            case "non-utc": node["startedAtUtc"] = "2026-10-16T00:00:00-04:00"; break;
            case "timestamp-order": node["startedAtUtc"] = "2026-10-17T00:00:00Z"; break;
            case "result-timestamp": entry["completedAtUtc"] = "2026-10-17T00:00:00Z"; break;
            case "total-count": node["totalSeeds"] = 9; break;
            case "snapshot-count": node["snapshotSeedCount"] = 9; break;
            case "seed-limit": node["seedLimitApplied"] = true; break;
            case "metrics": node["metrics"]!["homepageAttemptedCount"] = 0; break;
            case "metric-key": node["metrics"]!["robotsRequestOutcomes"]!["Unknown"] = 1; break;
            case "metric-total": node["metrics"]!["robotsRequestOutcomes"]!["ProxyHttpResponse"] = 100; break;
            case "concurrency": node["settings"]!["concurrency"] = 5; break;
            case "timeout": node["settings"]!["requestTimeoutMilliseconds"] = 60001; break;
            case "maximum-seeds": node["settings"]!["maximumSeeds"] = 0; break;
            case "active-count": node["metrics"]!["maximumActiveSeeds"] = 3; break;
            case "hash": node["seedInput"]!["fileSha256"] = "not-a-hash"; break;
            case "byte-length": node["seedInput"]!["fileByteLength"] = -1; break;
            case "proxy": node["proxy"]!["address"] = "example.com"; break;
            case "hostname": entry["hostname"] = "http://page.i2p/"; break;
            case "not-normalized": entry["hostname"] = "PAGE.I2P."; break;
            case "destination": entry["destination"] = SeedSnapshotTestData.Destination()[..^4]; break;
            case "destination-whitespace": entry["destination"] = " " + entry["destination"]!.GetValue<string>(); break;
            case "b32": entry["destinationHostname"] = new string('a', 52) + ".b32.i2p"; break;
            case "duplicate-seed": results[1] = results[0]!.DeepClone(); break;
            case "unsorted-seeds": var first = results[0]!.DeepClone(); results[0] = results[1]!.DeepClone(); results[1] = first; break;
            case "title-bound": metadata["title"] = new string('x', I2pPageMetadata.MaxTitleCharacters + 1); break;
            case "body-bound": entry["homepageResponse"]!["bodyBytes"] = I2pHttpTransport.MaxResponseBytes + 1; break;
            case "status": entry["homepageResponse"]!["statusCode"] = 42; break;
            case "url-kind": metadata["links"]![0]!["kind"] = "Clearnet"; break;
            case "url-scheme": metadata["links"]![0]!["url"] = "file:///private"; break;
            case "link-bound": metadata["links"] = new JsonArray(Enumerable.Range(0, I2pPageMetadata.MaxLinks + 1).Select(_ => metadata["links"]![0]!.DeepClone()).ToArray()); break;
            case "url-bound": metadata["links"]![0]!["url"] = "http://abc.b32.i2p/" + new string('x', I2pHttpRequestTarget.MaxUrlCharacters); break;
            case "header-bound": entry["homepageResponse"]!["contentType"] = new string('x', 257); break;
            case "attempt-gate": entry["robotsProxyClassification"] = "Ambiguous"; break;
            case "homepage-response": entry["homepageResponse"] = null; break;
            case "origin-evidence": entry["homepageOriginEvidence"] = "NotObserved"; break;
            case "classification": entry["homepageProxyClassification"] = "LikelyOriginResponse"; break;
            case "metadata-status": entry["homepageResponse"]!["statusCode"] = 403; break;
            case "error-bound": entry["errors"] = new JsonArray(new JsonObject { ["phase"] = "homepage", ["category"] = "private", ["detail"] = new string('x', 257) }); break;
            case "duration": entry["durationMilliseconds"] = -1; break;
        }
        var input = files.Write("raw.json", node.ToJsonString());
        var output = files.Write("public.json", "previous public artifact");
        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new I2pProjectionService().ProjectAsync(input, output));
        Assert.AreEqual("previous public artifact", await File.ReadAllTextAsync(output));
        Assert.DoesNotContain(SeedSnapshotTestData.Destination(), error.Message);
        Assert.DoesNotContain(ErrorSentinel, error.Message);
        Assert.IsEmpty(Directory.GetFiles(files.DirectoryPath, ".i2p-public-*.tmp"));
    }

    [TestMethod]
    [DataRow("malformed")]
    [DataRow("duplicate-root")]
    [DataRow("duplicate-result")]
    [DataRow("duplicate-response")]
    [DataRow("duplicate-metadata")]
    [DataRow("duplicate-link")]
    [DataRow("duplicate-metric")]
    [DataRow("duplicate-settings")]
    [DataRow("duplicate-provenance")]
    [DataRow("invalid-utf8")]
    [DataRow("invalid-unicode")]
    public async Task MalformedEncodingAndDuplicatePropertiesAreRejectedAtEveryLevel(string failure)
    {
        using var files = new TemporaryFiles();
        var json = Node().ToJsonString();
        json = failure switch
        {
            "malformed" => "{ PRIVATE invalid",
            "duplicate-root" => json.Replace("\"schema\":", "\"schema\":\"i2patlas.raw-scan\",\"schema\":"),
            "duplicate-result" => json.Replace("\"hostname\":", "\"hostname\":\"page.i2p\",\"hostname\":"),
            "duplicate-response" => json.Replace("\"statusCode\":", "\"statusCode\":200,\"statusCode\":"),
            "duplicate-metadata" => json.Replace("\"bodyTruncated\":", "\"bodyTruncated\":false,\"bodyTruncated\":"),
            "duplicate-link" => json.Replace("\"kind\":", "\"kind\":\"I2p\",\"kind\":"),
            "duplicate-metric" => json.Replace("\"ProxyHttpResponse\":", "\"ProxyHttpResponse\":1,\"ProxyHttpResponse\":"),
            "duplicate-settings" => json.Replace("\"concurrency\":", "\"concurrency\":2,\"concurrency\":"),
            "duplicate-provenance" => json.Replace("\"fileByteLength\":", "\"fileByteLength\":12345,\"fileByteLength\":"),
            "invalid-unicode" => json.Replace("<b>literal</b>", "\\uD800"),
            _ => json
        };
        if (failure == "invalid-unicode") json = json.Replace("\\u003Cb\\u003Eliteral\\u003C/b\\u003E", "\\uD800");
        var path = files.Write("raw.json", json);
        if (failure == "invalid-utf8") await File.WriteAllBytesAsync(path, [0xff, 0xfe]);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => I2pRawScanReader.ReadAsync(path));
    }

    [TestMethod]
    public async Task FileLimitIsCheckedBeforeParsing()
    {
        using var files = new TemporaryFiles();
        var path = files.Write("huge.json", "{}");
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Write)) file.SetLength(I2pRawScanReader.MaxArtifactBytes + 1L);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => I2pRawScanReader.ReadAsync(path));
    }

    [TestMethod]
    [DataRow("fail-replace")]
    [DataRow("cancel-replace")]
    [DataRow("output-is-directory")]
    public async Task CompleteClosedStagingAndReplacementFailuresPreservePreviousArtifact(string failure)
    {
        using var files = new TemporaryFiles();
        var input = Write(files, Artifact());
        var output = files.Write("public.json", "previous public artifact");
        using var cancellation = new CancellationTokenSource();
        var observed = false;
        var service = new I2pProjectionService(staged =>
        {
            observed = true;
            Assert.AreEqual("previous public artifact", File.ReadAllText(output));
            using (var stream = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.None))
                Assert.AreEqual("i2patlas.public-scan", JsonNode.Parse(stream)!["schema"]!.GetValue<string>());
            if (failure == "cancel-replace") cancellation.Cancel();
            else if (failure == "fail-replace") throw new IOException("Injected replacement failure.");
        });
        if (failure == "output-is-directory")
            await Assert.ThrowsExactlyAsync<IOException>(() => service.ProjectAsync(input, files.DirectoryPath));
        else if (failure == "cancel-replace")
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => service.ProjectAsync(input, output, cancellation.Token));
        else await Assert.ThrowsExactlyAsync<IOException>(() => service.ProjectAsync(input, output));
        Assert.AreEqual(failure != "output-is-directory", observed);
        Assert.AreEqual("previous public artifact", await File.ReadAllTextAsync(output));
        Assert.IsEmpty(Directory.GetFiles(files.DirectoryPath, ".i2p-public-*.tmp"));
    }

    [TestMethod]
    public async Task EmptyUnknownProbeAndLimitedScansAreSupportedWithoutInventingObservations()
    {
        using var files = new TemporaryFiles();
        var output = Path.Combine(files.DirectoryPath, "public.json");
        var empty = Artifact([]);
        var result = await new I2pProjectionService().ProjectAsync(Write(files, empty), output);
        Assert.AreEqual(0, result.SeedCount);
        Assert.AreEqual(0, result.UniqueHostnameCount);
        Assert.IsEmpty(result.Entries);
        var normal = RobotResult("unknown.i2p", 99, null, I2pRequestOutcome.TransportFailure);
        var unknown = normal with { RobotsRequestOutcome = null, HomepageRequestAttempted = null, HomepageDisposition = null,
            Errors = [new("probe", "UnexpectedProbeFailure", ErrorSentinel)] };
        var limited = Artifact([unknown], snapshotCount: 3, maximumSeeds: 1);
        result = await new I2pProjectionService().ProjectAsync(Write(files, limited), output);
        Assert.AreEqual(I2pPublicSummaryState.Ambiguous, result.Entries.Single().SummaryState);
        Assert.IsNull(result.Entries.Single().HomepageRequestAttempted);
        Assert.IsTrue(result.Entries.Single().LinkExtractionIncomplete);
    }

    [TestMethod]
    public async Task CommandDispatchIsLocalOnlyAndDoesNotConstructDn42Runtime()
    {
        using var files = new TemporaryFiles();
        var input = Write(files, Artifact());
        var output = Path.Combine(Directory.CreateDirectory(Path.Combine(files.DirectoryPath, "published")).FullName, "i2p.json");
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = files.DirectoryPath, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in new[] { typeof(I2pProjectCommand).Assembly.Location, "i2p-project", input, output }) start.ArgumentList.Add(argument);
        start.Environment["DN42ATLAS_RUNTIME_EXCLUSIONS_PATH"] = Path.Combine(files.DirectoryPath, "DO-NOT-OPEN.json");
        start.Environment["DN42ATLAS_REGISTRY_PATH"] = Path.Combine(files.DirectoryPath, "DO-NOT-OPEN-REGISTRY");
        start.Environment["HTTP_PROXY"] = start.Environment["HTTPS_PROXY"] = start.Environment["ALL_PROXY"] = "http://invalid-proxy.invalid:1";
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        Assert.AreEqual(0, process.ExitCode, await stderr);
        Assert.Contains("8 entries", await stdout);
        Assert.IsTrue(File.Exists(output));
        Assert.IsFalse(File.Exists(Path.Combine(files.DirectoryPath, "domain-resolution.json")));
    }

    [TestMethod]
    [DataRow("usage", 2)]
    [DataRow("missing-input", 1)]
    [DataRow("missing-directory", 1)]
    [DataRow("same-path", 2)]
    [DataRow("invalid", 1)]
    [DataRow("cancel", 130)]
    public async Task CommandFailuresHaveClearExitCodesWithoutPrivateDiagnosticLeaks(string failure, int expected)
    {
        using var files = new TemporaryFiles();
        var input = failure == "invalid" ? files.Write("private-raw.json", "PRIVATE_INVALID_INPUT") : Write(files, Artifact());
        var output = files.Write("public.json", "previous public artifact");
        var args = new[] { "i2p-project", failure == "missing-input" ? input + "missing" : input,
            failure == "same-path" ? input : failure == "missing-directory" ? Path.Combine(files.DirectoryPath, "absent", "public.json") : output };
        using var cancellation = new CancellationTokenSource();
        if (failure == "cancel") cancellation.Cancel();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        Assert.AreEqual(expected, await I2pProjectCommand.ExecuteAsync(failure == "usage" ? ["i2p-project"] : args, stdout, stderr, cancellation.Token));
        Assert.DoesNotContain(input, stderr.ToString());
        Assert.DoesNotContain("PRIVATE_INVALID_INPUT", stderr.ToString());
        Assert.AreEqual("previous public artifact", await File.ReadAllTextAsync(output));
    }

    private static string Write(TemporaryFiles files, I2pRawScanArtifact raw) => files.Write("raw.json", JsonSerializer.Serialize(raw, I2pRawScanFile.JsonOptions));
    private static JsonNode Node() => JsonNode.Parse(JsonSerializer.Serialize(Artifact(), I2pRawScanFile.JsonOptions))!;
    private static I2pRawScanArtifact Artifact(I2pRawSeedResult[]? custom = null, int? snapshotCount = null, int maximumSeeds = 10000)
    {
        var results = (custom ?? [
            RobotResult("ambiguous.i2p", 1, 403), RobotResult("disallow.i2p", 2, 200),
            RobotResult("i2p-javadocs.i2p", 3, 504), Homepage("i2p-javadocs.i2p", 4, true),
            Homepage("page.i2p", 5, false), RobotResult("redirect.i2p", 6, 302),
            RobotResult("timeout.i2p", 7, null, I2pRequestOutcome.TimedOut),
            RobotResult("transport.i2p", 8, null, I2pRequestOutcome.ProxyUnavailable)
        ]).OrderBy(r => r.Hostname, StringComparer.Ordinal).ThenBy(r => r.Destination, StringComparer.Ordinal).ToImmutableArray();
        var metrics = new I2pScanMetrics(9123.789,
            Counts(results.Select(r => r.RobotsRequestOutcome?.ToString() ?? "NotObserved")),
            Counts(results.Select(r => r.RobotsProxyClassification.ToString())),
            Counts(results.Select(r => r.HomepageRequestOutcome?.ToString() ?? (r.HomepageRequestAttempted == false ? "NotAttempted" : "NotObserved"))),
            Counts(results.Select(r => r.HomepageProxyClassification?.ToString() ?? "NotObserved")),
            results.Count(r => r.HomepageRequestAttempted == true), results.Count(r => r.HomepageResponse != null),
            results.Count(r => r.RobotsRequestOutcome == null), results.Length == 0 ? 0 : 1);
        return new(I2pRawScanArtifact.CurrentSchema, 1, Started, Started.AddSeconds(10),
            new(I2pSeedSnapshotSchema, 1, new string('B', 64), 12345, Started.AddDays(-1), new string('C', 64), 67890),
            new("127.0.0.1", 43891), new(2, 53791, maximumSeeds), snapshotCount ?? results.Length, results, metrics);
    }
    private const string I2pSeedSnapshotSchema = "i2patlas.seed-snapshot";
    private static ImmutableSortedDictionary<string, int> Counts(IEnumerable<string> values) => values.GroupBy(v => v, StringComparer.Ordinal)
        .ToImmutableSortedDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
    private static I2pRawSeedResult RobotResult(string hostname, int salt, int? status, I2pRequestOutcome? failure = null)
    {
        var seed = new I2pSeed(hostname, SeedSnapshotTestData.Destination(salt));
        var response = status.HasValue ? new I2pResponseObservation(status.Value, status == 200 ? "text/plain" : "text/html", 20, RedirectSentinel, false) : null;
        var classification = status == 504 ? I2pProxyResponseClassification.LikelyProxyFailure : status == 200
            ? I2pProxyResponseClassification.LikelyOriginResponse : I2pProxyResponseClassification.Ambiguous;
        var robots = status == 200 ? I2pRobotsObservation.DisallowsRoot : status == 302 ? I2pRobotsObservation.Redirect
            : status == null ? I2pRobotsObservation.NotObserved : I2pRobotsObservation.UnknownStatus;
        var disposition = status == null ? I2pHomepageDisposition.SkippedRequestFailed : status == 200 ? I2pHomepageDisposition.SkippedObservedDisallow
            : status == 504 ? I2pHomepageDisposition.SkippedProxyFailure : I2pHomepageDisposition.SkippedRobotsUnknown;
        return new(hostname, seed.Destination, new I2pHttpRequestTarget(seed, $"http://{hostname}/robots.txt").DestinationHostname,
            Started.AddSeconds(1), Started.AddSeconds(2), 123.456, failure ?? I2pRequestOutcome.ProxyHttpResponse,
            status == null ? I2pOriginEvidence.NotObserved : I2pOriginEvidence.Unproven, classification, robots, response,
            false, disposition, null, I2pOriginEvidence.NotObserved, null, null, null, [new("robots", CategorySentinel, ErrorSentinel)]);
    }
    private static I2pRawSeedResult Homepage(string hostname, int salt, bool timeout)
    {
        var result = RobotResult(hostname, salt, 200);
        var response = new I2pResponseObservation(200, "Text/HTML; charset=PRIVATE_CHARSET_SENTINEL", 1000, RedirectSentinel, false);
        var urls = new[] { "http://abc.b32.i2p/path", $"http://target.i2p/{PathSentinel}?secret={QuerySentinel}#PRIVATE_FRAGMENT",
            $"http://target.i2p/other?i2paddresshelper={SeedSnapshotTestData.Destination()}", "http://PRIVATE_USER:password@OTHER.I2P./path",
            "https://clearnet-private.example/path" };
        var links = urls.Order(StringComparer.Ordinal).Select(url => new I2pDiscoveredLink(url,
            url.Contains("clearnet-private", StringComparison.Ordinal) ? I2pDiscoveredLinkKind.Clearnet : I2pDiscoveredLinkKind.I2p)).ToImmutableArray();
        return result with { RobotsObservation = I2pRobotsObservation.AllowsRoot, HomepageRequestAttempted = true,
            HomepageDisposition = timeout ? I2pHomepageDisposition.RequestFailed : I2pHomepageDisposition.ResponseObserved,
            HomepageRequestOutcome = timeout ? I2pRequestOutcome.TimedOut : I2pRequestOutcome.ProxyHttpResponse,
            HomepageOriginEvidence = timeout ? I2pOriginEvidence.NotObserved : I2pOriginEvidence.Unproven,
            HomepageProxyClassification = timeout ? null : I2pProxyResponseClassification.Ambiguous,
            HomepageResponse = timeout ? null : response,
            HomepageMetadata = timeout ? null : new(response.ContentType, 1000, false,
                "\0\t\u202e<b>literal</b>\u2028" + new string('x', 180), links, true) };
    }
}
