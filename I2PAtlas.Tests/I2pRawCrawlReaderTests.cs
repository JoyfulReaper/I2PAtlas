using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using I2PAtlas.I2pCrawling;
using I2PAtlas.I2pProbing;
using I2PAtlas.I2pPublishing;
using I2PAtlas.I2pReporting;
using static I2PAtlas.Tests.CrawlTestData;

namespace I2PAtlas.Tests;

[TestClass]
public sealed class I2pRawCrawlReaderTests
{
    [TestMethod]
    public async Task WriterImmediatelyStrictReadsTheSamePrivateSemanticArtifact()
    {
        using var files = new TemporaryFiles();
        var output = Path.Combine(files.DirectoryPath, "crawl.json");
        var expected = Artifact();
        var sawStage = false;
        await I2pRawCrawlFile.WriteAsync(output, expected, default, staged =>
        {
            Assert.IsFalse(File.Exists(output));
            Assert.AreEqual(Json(expected), File.ReadAllText(staged));
            if (!OperatingSystem.IsWindows()) Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(staged));
            sawStage = true;
        });
        Assert.IsTrue(sawStage);
        Assert.AreEqual(Json(expected), Json(await I2pRawCrawlReader.ReadAsync(output)));
        var root = JsonNode.Parse(await File.ReadAllTextAsync(output))!;
        Assert.AreEqual("i2patlas.raw-crawl", root["schema"]!.GetValue<string>());
        Assert.AreEqual(1, root["version"]!.GetValue<int>());
        CollectionAssert.AreEquivalent(new[] { "hostname", "hostForm", "depth", "referrer", "evidence" },
            root["discoveredHostResults"]![0]!.AsObject().Select(p => p.Key).ToArray());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new I2pProjectionService().ProjectAsync(output, Path.Combine(files.DirectoryPath, "public.json")));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new I2pViewerGenerator().GenerateAsync(output, Path.Combine(files.DirectoryPath, "viewer.html")));
        Assert.IsFalse(File.Exists(Path.Combine(files.DirectoryPath, "public.json")));
        Assert.IsFalse(File.Exists(Path.Combine(files.DirectoryPath, "viewer.html")));
    }

    [TestMethod]
    [DataRow("schema")][DataRow("public-schema")][DataRow("version")][DataRow("integer-enum")][DataRow("enum-case")]
    [DataRow("unknown-root")][DataRow("unknown-evidence")][DataRow("fake-host-destination")][DataRow("fake-host-b32")]
    [DataRow("missing-field")][DataRow("non-utc")][DataRow("time-order")][DataRow("phase-barrier")]
    [DataRow("concurrency")][DataRow("timeout")][DataRow("max-seeds")][DataRow("max-hosts")]
    [DataRow("snapshot-hash")][DataRow("snapshot-schema")][DataRow("snapshot-version")][DataRow("snapshot-size")]
    [DataRow("proxy")][DataRow("proxy-port")][DataRow("host-case")][DataRow("host-form")][DataRow("host-form-enum")]
    [DataRow("control-host")][DataRow("extended-b32")][DataRow("referrer")][DataRow("unselected-referrer")]
    [DataRow("referrer-case")][DataRow("known-b32")][DataRow("invalid-destination")][DataRow("known-case")]
    [DataRow("duplicate-known-equivalent")][DataRow("duplicate-host")][DataRow("host-order")][DataRow("known-order")]
    [DataRow("known-depth")][DataRow("host-depth")][DataRow("selected-count")][DataRow("snapshot-count")][DataRow("seed-limit")]
    [DataRow("admitted-count")][DataRow("references")][DataRow("admission-flag")][DataRow("attempted-count")]
    [DataRow("response-count")][DataRow("unexpected-count")][DataRow("active-count")][DataRow("outcome-count")]
    [DataRow("homepage-gate")][DataRow("disposition")][DataRow("origin")][DataRow("outcome")][DataRow("homepage-class")]
    [DataRow("error-details")][DataRow("metadata-status")][DataRow("metadata-bytes")][DataRow("metadata-links")]
    [DataRow("helper-link")][DataRow("link-userinfo")][DataRow("link-kind")][DataRow("title-bound")][DataRow("truncation")]
    public async Task StrictBoundaryRejectsMalformedPrivateEvidenceWithoutRepairOrLeaks(string failure)
    {
        using var files = new TemporaryFiles();
        var root = JsonNode.Parse(Json(Artifact()))!;
        var known = root["knownSeedResults"]!.AsArray();
        var host = root["discoveredHostResults"]![0]!;
        var evidence = known[0]!["evidence"]!;
        switch (failure)
        {
            case "schema": root["schema"] = "i2patlas.raw-scan"; break;
            case "public-schema": root["schema"] = "i2patlas.public-crawl"; break;
            case "version": root["version"] = 2; break;
            case "integer-enum": evidence["robotsRequestOutcome"] = 4; break;
            case "enum-case": evidence["robotsRequestOutcome"] = "proxyHttpResponse"; break;
            case "unknown-root": root["private"] = true; break;
            case "unknown-evidence": evidence["body"] = "PRIVATE_BODY"; break;
            case "fake-host-destination": host["destination"] = "PRIVATE_DESTINATION"; break;
            case "fake-host-b32": host["destinationHostname"] = ClassicB32; break;
            case "missing-field": root.AsObject().Remove("metrics"); break;
            case "non-utc": root["crawlStartedAtUtc"] = "2026-10-16T02:00:00"; break;
            case "time-order": root["crawlCompletedAtUtc"] = "2020-01-01T00:00:00Z"; break;
            case "phase-barrier": host["evidence"]!["startedAtUtc"] = Started.AddSeconds(1); break;
            case "concurrency": root["settings"]!["concurrency"] = 5; break;
            case "timeout": root["settings"]!["requestTimeoutMilliseconds"] = 60001; break;
            case "max-seeds": root["settings"]!["maximumSeeds"] = 0; break;
            case "max-hosts": root["settings"]!["maximumDiscoveredHosts"] = 1001; break;
            case "snapshot-hash": root["snapshot"]!["fileSha256"] = "bad"; break;
            case "snapshot-schema": root["snapshot"]!["schema"] = "dn42"; break;
            case "snapshot-version": root["snapshot"]!["version"] = 2; break;
            case "snapshot-size": root["snapshot"]!["fileByteLength"] = 999999999; break;
            case "proxy": root["proxy"]!["address"] = "192.0.2.1"; break;
            case "proxy-port": root["proxy"]!["port"] = 0; break;
            case "host-case": host["hostname"] = "CHILD.I2P."; break;
            case "host-form": host["hostForm"] = "ClassicBase32"; break;
            case "host-form-enum": host["hostForm"] = "friendly"; break;
            case "control-host": host["hostname"] = "proxy.i2p"; break;
            case "extended-b32": host["hostname"] = new string('a', 56) + ".b32.i2p"; break;
            case "referrer": host["referrer"]!["destinationHostname"] = ClassicB32; break;
            case "unselected-referrer": host["referrer"]!["hostname"] = "c.i2p"; break;
            case "referrer-case": host["referrer"]!["hostname"] = "A.I2P."; break;
            case "known-b32": known[0]!["destinationHostname"] = ClassicB32; break;
            case "invalid-destination": known[0]!["destination"] = "PRIVATE_BAD_DESTINATION"; break;
            case "known-case": known[0]!["hostname"] = "A.I2P."; break;
            case "duplicate-known-equivalent":
                known[1] = known[0]!.DeepClone();
                known[1]!["destination"] = known[0]!["destination"]!.GetValue<string>().TrimEnd('='); break;
            case "duplicate-host": root["discoveredHostResults"]![1] = host.DeepClone(); break;
            case "host-order": root["discoveredHostResults"] = new JsonArray(root["discoveredHostResults"]!.AsArray().Reverse().Select(n => n!.DeepClone()).ToArray()); break;
            case "known-order": root["knownSeedResults"] = new JsonArray(known.Reverse().Select(n => n!.DeepClone()).ToArray()); break;
            case "known-depth": known[0]!["depth"] = 1; break;
            case "host-depth": host["depth"] = 2; break;
            case "selected-count": root["selectedSeedCount"] = 1; break;
            case "snapshot-count": root["snapshotSeedCount"] = 1; break;
            case "seed-limit": root["seedLimitApplied"] = false; break;
            case "admitted-count": root["discovery"]!["admittedHostCount"] = 0; break;
            case "references": root["discovery"]!["eligibleReferenceCount"] = 501; break;
            case "admission-flag": root["discovery"]!["admissionLimitApplied"] = true; root["settings"]!["maximumDiscoveredHosts"] = 3; break;
            case "attempted-count": root["metrics"]!["homepageAttemptedCount"] = 1; break;
            case "response-count": root["metrics"]!["homepageResponseCount"] = 1; break;
            case "unexpected-count": root["metrics"]!["unexpectedProbeFailureCount"] = 1; break;
            case "active-count": root["metrics"]!["maximumActiveProbes"] = 3; break;
            case "outcome-count": root["metrics"]!["robotsRequestOutcomes"]!["ProxyHttpResponse"] = 3; break;
            case "homepage-gate": evidence["robotsObservation"] = "DisallowsRoot"; break;
            case "disposition": evidence["homepageDisposition"] = "SkippedObservedDisallow"; break;
            case "origin": evidence["robotsOriginEvidence"] = "NotObserved"; break;
            case "outcome": evidence["robotsRequestOutcome"] = "TimedOut"; break;
            case "homepage-class": evidence["homepageProxyClassification"] = "LikelyOriginResponse"; break;
            case "error-details": evidence["errors"]!.AsArray().Add(new JsonObject { ["phase"] = "probe", ["category"] = "secret", ["detail"] = "PRIVATE_ERROR" }); break;
            case "metadata-status": evidence["homepageResponse"]!["statusCode"] = 403; break;
            case "metadata-bytes": evidence["homepageMetadata"]!["bodyBytesExamined"] = 10; break;
            case "metadata-links": evidence["homepageMetadata"]!["links"] = new JsonArray(Enumerable.Range(0, 251).Select(i => (JsonNode)new JsonObject { ["url"] = $"http://h{i}.i2p/", ["kind"] = "I2p" }).ToArray()); break;
            case "helper-link": evidence["homepageMetadata"]!["links"]![0]!["url"] = "http://child.i2p/?i2paddresshelper=secret"; break;
            case "link-userinfo": evidence["homepageMetadata"]!["links"]![0]!["url"] = "http://secret@child.i2p/"; break;
            case "link-kind": evidence["homepageMetadata"]!["links"]![0]!["kind"] = "Clearnet"; break;
            case "title-bound": evidence["homepageMetadata"]!["title"] = new string('x', 201); break;
            case "truncation": evidence["homepageMetadata"]!["bodyTruncated"] = true; break;
        }
        var input = files.Write("raw.json", root.ToJsonString());
        var original = await File.ReadAllBytesAsync(input);
        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => I2pRawCrawlReader.ReadAsync(input));
        Assert.IsLessThan(256, error.Message.Length);
        Assert.DoesNotContain("PRIVATE_", error.Message);
        Assert.DoesNotContain(Artifact().KnownSeedResults[0].Destination, error.Message);
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(input));
    }

    [TestMethod]
    [DataRow("root")][DataRow("evidence")][DataRow("settings")][DataRow("referrer")][DataRow("response")]
    public async Task DuplicatePropertiesAtEveryBoundaryAreRejected(string level)
    {
        using var files = new TemporaryFiles();
        var json = Json(Artifact());
        var key = level switch { "root" => "schema", "evidence" => "robotsRequestOutcome", "settings" => "concurrency", "referrer" => "hostname", _ => "statusCode" };
        // Repeating even an identical value is invalid; JSON deserialization must not choose a winner.
        var needle = $"\"{key}\":";
        var start = level == "referrer" ? json.IndexOf("\"referrer\":", StringComparison.Ordinal) : 0;
        var position = json.IndexOf(needle, start, StringComparison.Ordinal);
        var valueStart = position + needle.Length;
        var valueEnd = json.IndexOfAny([',', '\r', '\n'], valueStart);
        var value = json[valueStart..valueEnd];
        json = json.Insert(valueEnd, "," + needle + value);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => I2pRawCrawlReader.ReadAsync(files.Write("raw.json", json)));
    }

    [TestMethod]
    public async Task EncodingDepthAndReadSizeBoundsAreEnforced()
    {
        using var files = new TemporaryFiles();
        var path = files.Write("raw.json", "");
        File.WriteAllBytes(path, [0xff]);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => I2pRawCrawlReader.ReadAsync(path));
        File.WriteAllText(path, new string('[', 17) + "0" + new string(']', 17));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => I2pRawCrawlReader.ReadAsync(path));
        using (var file = new FileStream(path, FileMode.Create)) file.SetLength((long)I2pRawCrawlReader.MaxArtifactBytes + 1);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => I2pRawCrawlReader.ReadAsync(path));
    }

    [TestMethod]
    [DataRow("cancel")][DataRow("io")][DataRow("serialization-limit")][DataRow("invalid-artifact")]
    public async Task FailedStagingOrValidationNeverReplacesPreviousOutput(string failure)
    {
        using var files = new TemporaryFiles();
        var output = files.Write("raw.json", "previous");
        using var cancel = new CancellationTokenSource();
        var artifact = Artifact();
        if (failure == "invalid-artifact") artifact = artifact with { Discovery = new(0, 2, false) };
        try
        {
            await I2pRawCrawlFile.WriteAsync(output, artifact, cancel.Token, _ =>
            {
                if (failure == "cancel") cancel.Cancel();
                if (failure == "io") throw new IOException("I/O failure");
            }, failure == "serialization-limit" ? 100 : I2pRawCrawlFile.MaxArtifactBytes);
            Assert.Fail("An injected failure must abort installation.");
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidDataException) { }
        Assert.AreEqual("previous", await File.ReadAllTextAsync(output));
        Assert.IsEmpty(Directory.GetFiles(files.DirectoryPath, ".i2p-crawl-*.tmp"));
    }

    [TestMethod]
    public async Task ReaderAlsoEnforcesTheRealAggregateEvidenceBudget()
    {
        using var files = new TemporaryFiles();
        var observation = Observation(Enumerable.Range(0, 250).Select(i => ($"http://u{i:D3}.i2p/").PadRight(4096, 'x')).ToArray());
        var evidence = I2pCrawlEvidence.FromObservation(observation, Started.AddSeconds(1), Started.AddSeconds(2), 100);
        var known = Enumerable.Range(0, 34).Select(i =>
        {
            var seed = Seed($"p{i:D2}.i2p");
            return new I2pCrawlKnownResult(seed.Hostname, seed.Destination, B32(seed), 0, evidence);
        }).ToImmutableArray();
        var raw = Artifact() with { KnownSeedResults = known, DiscoveredHostResults = [], SnapshotSeedCount = 34,
            Settings = new(maximumSeeds: 34, maximumDiscoveredHosts: 0), Discovery = new(8500, 0, true),
            Metrics = I2pCrawlMetrics.Calculate(known.Select(r => r.Evidence), 5000, 1) };
        var input = Path.Combine(files.DirectoryPath, "large.json");
        await using (var stream = File.Create(input)) await JsonSerializer.SerializeAsync(stream, raw, I2pRawCrawlFile.JsonOptions);
        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => I2pRawCrawlReader.ReadAsync(input));
        Assert.Contains("retained evidence", error.Message);
    }
}
