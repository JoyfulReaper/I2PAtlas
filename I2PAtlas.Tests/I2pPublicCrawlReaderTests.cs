using System.Text.Json;
using System.Text.Json.Nodes;
using I2PAtlas.I2pCrawling;
using I2PAtlas.I2pProbing;
using I2PAtlas.I2pPublishing;
using static I2PAtlas.Tests.CrawlProjectionTestData;

namespace I2PAtlas.Tests;

[TestClass]
public sealed class I2pPublicCrawlReaderTests
{
    [TestMethod]
    [DataRow("schema")][DataRow("version")][DataRow("unknown-root")][DataRow("missing-root")]
    [DataRow("known-count")][DataRow("host-count")][DataRow("kind-count")][DataRow("too-many-known")][DataRow("too-many-hosts")]
    [DataRow("non-utc")][DataRow("offset")][DataRow("time-order")]
    [DataRow("kind")][DataRow("kind-case")][DataRow("integer-kind")][DataRow("missing-kind")][DataRow("missing-hostname")]
    [DataRow("known-depth")][DataRow("host-depth")][DataRow("known-hostform")][DataRow("known-referrer")]
    [DataRow("host-destination")][DataRow("base64")][DataRow("private-observation")][DataRow("private-timing")]
    [DataRow("known-case")][DataRow("host-dot")][DataRow("invalid-host")][DataRow("url-host")][DataRow("ip-host")]
    [DataRow("bad-b32")][DataRow("noncanonical-b32")][DataRow("hostform")][DataRow("integer-hostform")]
    [DataRow("classic-form")][DataRow("classic-bits")][DataRow("control-host")][DataRow("extended-b32")]
    [DataRow("referrer-pair")][DataRow("referrer-unknown")][DataRow("referrer-case")][DataRow("missing-referrer")]
    [DataRow("duplicate-known")][DataRow("duplicate-host")][DataRow("known-order")][DataRow("host-order")][DataRow("kinds-order")]
    [DataRow("known-as-discovered")][DataRow("discovered-as-known")][DataRow("known-host-collision")]
    [DataRow("title-bound")][DataRow("title-control")][DataRow("title-format")][DataRow("title-space")][DataRow("title-empty")]
    [DataRow("media-bound")][DataRow("media-parameters")][DataRow("media-case")][DataRow("media-invalid")]
    [DataRow("link-count")][DataRow("link-order")][DataRow("link-duplicate")][DataRow("link-host")][DataRow("link-url")]
    [DataRow("summary")][DataRow("enum-case")][DataRow("integer-enum")][DataRow("unknown-enum")][DataRow("null-enum")]
    [DataRow("outcome-status")][DataRow("attempt-null")][DataRow("attempt-false")][DataRow("gate")]
    [DataRow("robots-failure")][DataRow("robots-redirect")][DataRow("robots-200")][DataRow("homepage-class")]
    [DataRow("metadata-status")][DataRow("extraction-state")][DataRow("missing-observation")]
    public async Task RejectsMalformedOrNoncanonicalPublicCrawlWithoutRepairOrPrivateDiagnostics(string mutation)
    {
        using var files = new TemporaryFiles();
        var root = JsonNode.Parse(PublicJson())!;
        var entries = root["entries"]!.AsArray();
        var known = entries[0]!;
        var host = entries[2]!;
        var obs = known["observation"]!;
        switch (mutation)
        {
            case "schema": root["schema"] = "i2patlas.public-scan"; break;
            case "version": root["version"] = 2; break;
            case "unknown-root": root["snapshot"] = "PRIVATE_PROVENANCE"; break;
            case "missing-root": root.AsObject().Remove("knownSeedCount"); break;
            case "known-count": root["knownSeedCount"] = 1; break;
            case "host-count": root["discoveredHostCount"] = 1; break;
            case "kind-count": root["knownSeedCount"] = 3; root["discoveredHostCount"] = 1; break;
            case "too-many-known": root["knownSeedCount"] = 10001; break;
            case "too-many-hosts": root["discoveredHostCount"] = 1001; break;
            case "non-utc": root["crawlStartedAtUtc"] = "2026-10-16T02:00:00"; break;
            case "offset": root["crawlStartedAtUtc"] = "2026-10-16T02:00:00+01:00"; break;
            case "time-order": root["crawlCompletedAtUtc"] = "2020-01-01T00:00:00Z"; break;
            case "kind": known["kind"] = "Unknown"; break;
            case "kind-case": known["kind"] = "knownSeed"; break;
            case "integer-kind": known["kind"] = 0; break;
            case "missing-kind": known.AsObject().Remove("kind"); break;
            case "missing-hostname": known.AsObject().Remove("hostname"); break;
            case "known-depth": known["depth"] = 1; break;
            case "host-depth": host["depth"] = 2; break;
            case "known-hostform": known["hostForm"] = "Friendly"; break;
            case "known-referrer": known["referrerHostname"] = "a.i2p"; break;
            case "host-destination": host["destinationHostname"] = CrawlTestData.ClassicB32; break;
            case "base64": known["destination"] = "PRIVATE_DESTINATION"; break;
            case "private-observation": obs["errors"] = new JsonArray("PRIVATE_ERROR"); break;
            case "private-timing": obs["durationMilliseconds"] = 123; break;
            case "known-case": known["hostname"] = "A.I2P"; break;
            case "host-dot": host["hostname"] = "child.i2p."; break;
            case "invalid-host": host["hostname"] = "bad_name.i2p"; break;
            case "url-host": host["hostname"] = "http://child.i2p/path"; break;
            case "ip-host": host["hostname"] = "127.0.0.1"; break;
            case "bad-b32": known["destinationHostname"] = "abc.b32.i2p"; break;
            case "noncanonical-b32": known["destinationHostname"] = new string('a', 51) + "b.b32.i2p"; break;
            case "hostform": host["hostForm"] = "ClassicBase32"; break;
            case "integer-hostform": host["hostForm"] = 0; break;
            case "classic-form": entries[3]!["hostForm"] = "Friendly"; break;
            case "classic-bits": entries[3]!["hostname"] = new string('z', 52) + ".b32.i2p"; break;
            case "control-host": host["hostname"] = "sub.proxy.i2p"; break;
            case "extended-b32": host["hostname"] = new string('a', 56) + ".b32.i2p"; break;
            case "referrer-pair": host["referrerHostname"] = "b.i2p"; break;
            case "referrer-unknown": host["referrerHostname"] = "unselected.i2p"; break;
            case "referrer-case": host["referrerHostname"] = "A.I2P"; break;
            case "missing-referrer": host.AsObject().Remove("referrerDestinationHostname"); break;
            case "duplicate-known": entries[1] = known.DeepClone(); break;
            case "duplicate-host": entries[3] = host.DeepClone(); break;
            case "known-order": entries[0] = entries[1]!.DeepClone(); entries[1] = known.DeepClone(); break;
            case "host-order": entries[2] = entries[3]!.DeepClone(); entries[3] = host.DeepClone(); break;
            case "kinds-order": entries[0] = host.DeepClone(); entries[2] = known.DeepClone(); break;
            case "known-as-discovered": known["kind"] = "DiscoveredHost"; break;
            case "discovered-as-known": host["kind"] = "KnownSeed"; break;
            case "known-host-collision": host["hostname"] = "a.i2p"; break;
            case "title-bound": obs["title"] = new string('x', 161); break;
            case "title-control": obs["title"] = "title\0"; break;
            case "title-format": obs["title"] = "title\u202e"; break;
            case "title-space": obs["title"] = " title "; break;
            case "title-empty": obs["title"] = ""; break;
            case "media-bound": obs["homepageContentType"] = "text/" + new string('x', 76); break;
            case "media-parameters": obs["homepageContentType"] = "text/html;charset=utf-8"; break;
            case "media-case": obs["homepageContentType"] = "Text/HTML"; break;
            case "media-invalid": obs["homepageContentType"] = "garbage"; break;
            case "link-count": obs["discoveredI2pHostnames"] = new JsonArray(Enumerable.Range(0, 251).Select(i => JsonValue.Create($"h{i:D3}.i2p") as JsonNode).ToArray()); break;
            case "link-order": obs["discoveredI2pHostnames"] = new JsonArray("z.i2p", "a.i2p"); break;
            case "link-duplicate": obs["discoveredI2pHostnames"] = new JsonArray("a.i2p", "a.i2p"); break;
            case "link-host": obs["discoveredI2pHostnames"] = new JsonArray("example.com"); break;
            case "link-url": obs["discoveredI2pHostnames"] = new JsonArray("http://child.i2p/path?q=PRIVATE_QUERY"); break;
            case "summary": obs["summaryState"] = "Ambiguous"; break;
            case "enum-case": obs["robotsProxyClassification"] = "likelyOriginResponse"; break;
            case "integer-enum": obs["robotsRequestOutcome"] = 4; break;
            case "unknown-enum": obs["robotsObservation"] = "Online"; break;
            case "null-enum": obs["robotsObservation"] = null; break;
            case "outcome-status": obs["robotsRequestOutcome"] = "TimedOut"; break;
            case "attempt-null": obs["homepageRequestAttempted"] = null; break;
            case "attempt-false": obs["homepageRequestAttempted"] = false; break;
            case "gate": obs["robotsObservation"] = "DisallowsRoot"; break;
            case "robots-failure": obs["robotsStatusCode"] = 504; break;
            case "robots-redirect": obs["robotsStatusCode"] = 302; break;
            case "robots-200": obs["robotsObservation"] = "UnknownStatus"; break;
            case "homepage-class": obs["homepageProxyClassification"] = "LikelyOriginResponse"; break;
            case "metadata-status": obs["homepageStatusCode"] = 403; break;
            case "extraction-state": obs["homepageStatusCode"] = 504; obs["homepageProxyClassification"] = "LikelyProxyFailure";
                obs["title"] = null; obs["discoveredI2pHostnames"] = new JsonArray(); break;
            case "missing-observation": known.AsObject().Remove("observation"); break;
        }
        var json = root.ToJsonString();
        var path = files.Write("public.json", json);
        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => I2pPublicCrawlReader.ReadAsync(path));
        Assert.IsLessThan(256, error.Message.Length);
        Assert.DoesNotContain("PRIVATE_", error.Message);
        Assert.DoesNotContain(path, error.Message);
        Assert.AreEqual(json, await File.ReadAllTextAsync(path));
    }

    [TestMethod]
    [DataRow("root", "schema")][DataRow("known", "destinationHostname")][DataRow("host", "referrerHostname")]
    [DataRow("observation", "robotsObservation")]
    public async Task DuplicatePropertiesAreRejectedAtEachObjectBoundary(string scope, string key)
    {
        using var files = new TemporaryFiles();
        var json = PublicJson();
        var start = scope switch { "host" => json.IndexOf("\"hostForm\":", StringComparison.Ordinal),
            "observation" => json.IndexOf("\"observation\":", StringComparison.Ordinal), _ => 0 };
        var needle = $"\"{key}\":";
        var index = json.IndexOf(needle, start, StringComparison.Ordinal);
        var valueStart = index + needle.Length;
        var valueEnd = json.IndexOfAny([',', '\r', '\n'], valueStart);
        json = json.Insert(valueEnd, "," + needle + json[valueStart..valueEnd]);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => I2pPublicCrawlReader.ReadAsync(files.Write("public.json", json)));
    }

    [TestMethod]
    public async Task StrictEncodingDepthAndActualReadSizeBoundsApply()
    {
        using var files = new TemporaryFiles();
        var path = files.Write("public.json", "");
        foreach (var bytes in new[] { new byte[] { 0xff }, new byte[] { 0xef, 0xbb, 0xbf }.Concat(System.Text.Encoding.UTF8.GetBytes(PublicJson())).ToArray(),
            System.Text.Encoding.UTF8.GetBytes(new string('[', 9) + "0" + new string(']', 9)) })
        {
            await File.WriteAllBytesAsync(path, bytes);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => I2pPublicCrawlReader.ReadAsync(path));
        }
        using (var file = new FileStream(path, FileMode.Create)) file.SetLength((long)I2pPublicCrawlReader.MaxArtifactBytes + 1);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => I2pPublicCrawlReader.ReadAsync(path));
    }

    [TestMethod]
    [DataRow("unknown")][DataRow("timeout")][DataRow("unavailable")][DataRow("rejected")][DataRow("redirect")][DataRow("403")][DataRow("504")]
    [DataRow("too-large")][DataRow("malformed")][DataRow("unsupported")][DataRow("ambiguous-allow")][DataRow("disallow")]
    [DataRow("homepage-timeout")][DataRow("homepage-proxyfailure")][DataRow("homepage-redirect")][DataRow("homepage-no-metadata")]
    public async Task AllSupportedObservationStatesSurviveProjectionAndStrictPublicReading(string state)
    {
        using var files = new TemporaryFiles();
        var raw = CrawlTestData.Artifact();
        var prior = raw.KnownSeedResults[0];
        var evidence = I2pCrawlEvidence.FromObservation(Observed(state), prior.Evidence.StartedAtUtc,
            prior.Evidence.CompletedAtUtc, prior.Evidence.DurationMilliseconds);
        raw = Refresh(raw with { KnownSeedResults = [prior with { Evidence = evidence }], DiscoveredHostResults = [],
            SnapshotSeedCount = 1, Settings = new(), Discovery = new(0, 0, false) });
        raw = raw with { Metrics = raw.Metrics with { MaximumActiveProbes = 1 } };
        var input = await WriteRawAsync(files, raw);
        var output = Path.Combine(files.DirectoryPath, "public.json");
        await new I2pCrawlProjectionService().ProjectAsync(input, output);
        var e = (await I2pPublicCrawlReader.ReadAsync(output)).Entries.Single().Observation;
        Assert.AreEqual(evidence.HomepageRequestAttempted, e.HomepageRequestAttempted);
        Assert.AreEqual(evidence.RobotsRequestOutcome, e.RobotsRequestOutcome);
        Assert.AreEqual(evidence.HomepageResponse?.StatusCode, e.HomepageStatusCode);
    }

    private static I2pProbeObservation? Observed(string state)
    {
        if (state == "unknown") return null;
        if (state.StartsWith("homepage-", StringComparison.Ordinal))
        {
            var page = CrawlTestData.Observation();
            return state switch
            {
                "homepage-timeout" => page with { HomepageRequestOutcome = I2pRequestOutcome.TimedOut, Homepage = I2pHomepageDisposition.RequestFailed,
                    HomepageResponse = null, HomepageMetadata = null, HomepageProxyResponseClassification = null },
                "homepage-proxyfailure" => page with { HomepageResponse = new(504, "text/html", 20, null, false), HomepageMetadata = null,
                    HomepageProxyResponseClassification = I2pProxyResponseClassification.LikelyProxyFailure },
                "homepage-redirect" => page with { HomepageResponse = new(302, "text/html", 20, null, false), HomepageMetadata = null },
                _ => page with { HomepageMetadata = null }
            };
        }
        var outcome = state switch { "timeout" => I2pRequestOutcome.TimedOut, "unavailable" => I2pRequestOutcome.ProxyUnavailable,
            "rejected" => I2pRequestOutcome.RejectedBeforeNetworking, _ => I2pRequestOutcome.ProxyHttpResponse };
        var status = state switch { "redirect" => 302, "403" => 403, "504" => 504, _ => 200 };
        var classification = state == "504" ? I2pProxyResponseClassification.LikelyProxyFailure : state == "disallow"
            ? I2pProxyResponseClassification.LikelyOriginResponse : I2pProxyResponseClassification.Ambiguous;
        var robots = outcome != I2pRequestOutcome.ProxyHttpResponse ? I2pRobotsObservation.NotObserved : state switch
        {
            "redirect" => I2pRobotsObservation.Redirect, "403" or "504" => I2pRobotsObservation.UnknownStatus,
            "too-large" => I2pRobotsObservation.TooLarge, "unsupported" => I2pRobotsObservation.UnsupportedRepresentation,
            "ambiguous-allow" => I2pRobotsObservation.AllowsRoot, "disallow" => I2pRobotsObservation.DisallowsRoot, _ => I2pRobotsObservation.Malformed
        };
        var disposition = outcome != I2pRequestOutcome.ProxyHttpResponse
            ? state == "rejected" ? I2pHomepageDisposition.SkippedRequestRejected : I2pHomepageDisposition.SkippedRequestFailed
            : state == "504" ? I2pHomepageDisposition.SkippedProxyFailure : state == "disallow" ? I2pHomepageDisposition.SkippedObservedDisallow
            : state == "ambiguous-allow" ? I2pHomepageDisposition.SkippedAmbiguousResponse : I2pHomepageDisposition.SkippedRobotsUnknown;
        return new(outcome, outcome == I2pRequestOutcome.ProxyHttpResponse ? I2pOriginEvidence.Unproven : I2pOriginEvidence.NotObserved,
            classification, outcome == I2pRequestOutcome.ProxyHttpResponse ? new(status, "text/plain", state == "too-large" ? 65537 : 20, null, false) : null,
            robots, disposition);
    }
}
