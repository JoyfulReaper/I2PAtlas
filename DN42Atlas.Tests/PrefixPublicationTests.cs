using System.Text;
using System.Text.Json.Nodes;
using DN42Atlas.Commands;
using DN42Atlas.Policy;
using DN42Atlas.Publishing;
using DN42Atlas.Reporting;

namespace DN42Atlas.Tests;

[TestClass]
public sealed class PrefixPublicationTests
{
    [TestMethod]
    [DataRow("172.20.1.0/24", "172.20.1.1", "blocked.dn42")]
    [DataRow("fd42:1234::/48", "fd42:1234::1", " BLOCKED.DN42. ")]
    public async Task PrefixRemovedHostReferencesAreFilteredFromBothPublicModels(string prefix, string address, string domain)
    {
        using var files = new TestFiles();
        var scan = new JsonObject
        {
            ["GeneratedAt"] = "2026-10-03T12:50:26Z", ["ExcludedByHostname"] = 0, ["ExcludedByPrefix"] = 0,
            ["SkippedExternalOrMixedDomains"] = new JsonArray("BLOCKED.DN42.", "unrelated.dn42"),
            ["Results"] = new JsonArray(
                new JsonObject { ["Domain"] = domain, ["ProbeAddresses"] = new JsonArray(address) },
                new JsonObject
                {
                    ["Domain"] = "safe.dn42", ["Scheme"] = "http", ["Port"] = 80,
                    ["ProbeAddresses"] = new JsonArray("fd42:5678::1"),
                    ["Title"] = "See BLOCKED.DN42. and safe.dn42",
                    ["Error"] = "http://blocked.dn42/path failed",
                    ["DiscoveredLinks"] = new JsonArray("http://blocked.dn42/", "https://BLOCKED.DN42./path", "http://safe.dn42/", "http://child.blocked.dn42/"),
                    ["Dn42Mentions"] = new JsonArray(" blocked.dn42. ", "safe.dn42", "child.blocked.dn42")
                })
        };
        var raw = files.Write("scan.json", scan.ToJsonString());
        var original = File.ReadAllBytes(raw);
        Assert.Contains("ProbeAddresses", Encoding.UTF8.GetString(original));
        var published = Path.Combine(files.DirectoryPath, "published");
        var state = Path.Combine(files.DirectoryPath, "state.json");
        await ArtifactPublisher.PublishAsync(raw, published, files.LoadPolicy(), state);
        File.WriteAllText(files.PrefixesPath, prefix);
        await new RepublishCommand(files.LoadPolicy(), published, state).ExecuteAsync();
        var json = File.ReadAllText(Path.Combine(published, "latest.json"));
        var html = File.ReadAllText(Path.Combine(published, "index.html"));
        var model = JsonNode.Parse(json)!;
        Assert.HasCount(1, model["Results"]!.AsArray());
        var safe = model["Results"]![0]!;
        Assert.AreEqual("safe.dn42", safe["Domain"]!.GetValue<string>());
        Assert.AreEqual("See [excluded] and safe.dn42", safe["Title"]!.GetValue<string>());
        Assert.AreEqual("http://[excluded]/path failed", safe["Error"]!.GetValue<string>());
        CollectionAssert.AreEqual(new[] { "http://safe.dn42/", "http://child.blocked.dn42/" },
            safe["DiscoveredLinks"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
        CollectionAssert.AreEqual(new[] { "safe.dn42", "child.blocked.dn42" },
            safe["Dn42Mentions"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
        CollectionAssert.AreEqual(new[] { "unrelated.dn42" },
            model["SkippedExternalOrMixedDomains"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
        Assert.AreEqual(1, model["ExcludedByPrefix"]!.GetValue<int>());
        Assert.DoesNotContain("ProbeAddresses", json);
        Assert.DoesNotContain("ProbeAddresses", html);
        Assert.IsTrue(JsonNode.DeepEquals(model, Embedded(html)));
        CollectionAssert.AreEqual(original, File.ReadAllBytes(raw));
    }

    [TestMethod]
    [DataRow("172.20.220.48/28", "172.20.220.49", true)]
    [DataRow("172.20.0.0/16", "172.20.220.49", true)]
    [DataRow("fdf0:e12c:5528::/48", "fdf0:e12c:5528::123", true)]
    [DataRow("172.20.220.48/28", "172.20.220.64", false)]
    [DataRow("fdf0:e12c:5528::/48", "fdf0:e12c:5529::1", false)]
    [DataRow("172.20.220.48/28", "fdf0:e12c:5528::1", false)]
    [DataRow("fdf0:e12c:5528::/48", "172.20.220.49", false)]
    [DataRow("172.20.220.48/28", "::ffff:172.20.220.49", true)]
    [DataRow("fdf0:e12c:5528::/48", "::ffff:172.20.220.49", false)]
    public void EntireRowIsRemovedOnlyForMatchingRecordedAddress(string prefix, string address, bool removed)
    {
        using var files = new TestFiles(prefixes: prefix);
        var scan = Scan(new JsonArray(address));
        var raw = Encoding.UTF8.GetBytes(scan.ToJsonString());
        var artifacts = PublicArtifactGenerator.Generate(raw, files.LoadPolicy());
        var model = JsonNode.Parse(artifacts.Json)!;
        Assert.HasCount(removed ? 0 : 1, model["Results"]!.AsArray());
        Assert.AreEqual(removed ? 1 : 0, model["ExcludedByPrefix"]!.GetValue<int>());
        Assert.AreEqual(0, model["ExcludedByHostname"]!.GetValue<int>());
        Assert.IsTrue(JsonNode.DeepEquals(model, Embedded(artifacts.Html)));
        if (removed)
        {
            Assert.DoesNotContain("target.dn42", artifacts.Json);
            Assert.DoesNotContain("target.dn42", artifacts.Html);
        }
    }

    [TestMethod]
    [DataRow("172.20.220.48/28")]
    [DataRow("fdf0:e12c:5528::/48")]
    public void OneMatchingAddressInMixedSetRemovesWholeResult(string prefix)
    {
        using var files = new TestFiles(prefixes: prefix);
        var scan = Scan(new JsonArray("172.20.220.49", "fdf0:e12c:5528::1", "fd42::1"));
        new PublicScanPolicy(files.LoadPolicy()).Apply(scan);
        Assert.IsEmpty(scan["Results"]!.AsArray());
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("target.dn42")]
    public async Task LegacyScanWorksWithNoPrefixesIncludingHostnameOnlyPolicy(string host)
    {
        using var files = new TestFiles(host);
        var raw = files.Write("legacy.json", Scan(null).ToJsonString());
        var bytes = File.ReadAllBytes(raw);
        var published = Path.Combine(files.DirectoryPath, "published");
        await ArtifactPublisher.PublishAsync(raw, published, files.LoadPolicy(), Path.Combine(files.DirectoryPath, "state.json"));
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(published, "latest.json")))!;
        Assert.HasCount(host.Length == 0 ? 1 : 0, json["Results"]!.AsArray());
        Assert.IsTrue(JsonNode.DeepEquals(json, Embedded(File.ReadAllText(Path.Combine(published, "index.html")))));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(raw));
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("null")]
    [DataRow("empty")]
    [DataRow("malformed")]
    [DataRow("non-string")]
    [DataRow("not-array")]
    [DataRow("mixed-invalid")]
    [DataRow("partial-legacy")]
    public async Task InsufficientProvenanceWithdrawsListingAndPreservesRawStateAndSupportFiles(string evidence)
    {
        using var files = new TestFiles();
        var scan = Scan(null);
        var row = scan["Results"]![0]!;
        if (evidence != "missing") row["ProbeAddresses"] = evidence switch
        {
            "null" => null, "empty" => new JsonArray(), "malformed" => new JsonArray("invalid"),
            "non-string" => new JsonArray(123), "not-array" => JsonValue.Create("172.20.220.49"),
            "mixed-invalid" => new JsonArray("172.20.220.49", "invalid"),
            _ => new JsonArray("172.20.220.49")
        };
        if (evidence == "partial-legacy") scan["Results"]!.AsArray().Add(new JsonObject { ["Domain"] = "legacy.dn42" });
        var raw = files.Write("scan.json", scan.ToJsonString());
        var original = File.ReadAllBytes(raw);
        var published = Path.Combine(files.DirectoryPath, "published");
        var state = Path.Combine(files.DirectoryPath, "state.json");
        await ArtifactPublisher.PublishAsync(raw, published, files.LoadPolicy(), state);
        var originalState = File.ReadAllBytes(state);
        var support = new[] { "about.html", "opt-out.html", "robots.txt" }.Select(name => Path.Combine(published, name)).ToArray();
        var supportBytes = support.Select(File.ReadAllBytes).ToArray();
        File.WriteAllText(files.PrefixesPath, "172.20.220.48/28");
        await Assert.ThrowsExactlyAsync<MissingProbeAddressProvenanceException>(() =>
            new RepublishCommand(files.LoadPolicy(), published, state).ExecuteAsync());
        Assert.IsFalse(File.Exists(Path.Combine(published, "index.html")));
        Assert.IsFalse(File.Exists(Path.Combine(published, "latest.json")));
        CollectionAssert.AreEqual(original, File.ReadAllBytes(raw));
        CollectionAssert.AreEqual(originalState, File.ReadAllBytes(state));
        for (var i = 0; i < support.Length; i++) CollectionAssert.AreEqual(supportBytes[i], File.ReadAllBytes(support[i]));
    }

    [TestMethod]
    public async Task NewPublicationWithInsufficientProvenanceEmitsNoArtifacts()
    {
        using var files = new TestFiles(prefixes: "172.20.220.48/28");
        var raw = files.Write("legacy.json", Scan(null).ToJsonString());
        var published = Path.Combine(files.DirectoryPath, "published");
        var state = Path.Combine(files.DirectoryPath, "state.json");
        var bytes = File.ReadAllBytes(raw);
        await Assert.ThrowsExactlyAsync<MissingProbeAddressProvenanceException>(() =>
            ArtifactPublisher.PublishAsync(raw, published, files.LoadPolicy(), state));
        Assert.IsFalse(Directory.Exists(published));
        Assert.IsFalse(File.Exists(state));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(raw));
    }

    [TestMethod]
    public async Task PrefixRepublishRemovesMatchingRowsAndPreservesTimestampAndRawBytes()
    {
        using var files = new TestFiles();
        var scan = Scan(new JsonArray("172.20.220.49", "fdf0:e12c:5528::1"));
        scan["Results"]!.AsArray().Add(new JsonObject { ["Domain"] = "keep.dn42", ["Scheme"] = "https", ["Port"] = 443,
            ["ProbeAddresses"] = new JsonArray("fd42::1") });
        var raw = files.Write("scan.json", scan.ToJsonString());
        var original = File.ReadAllBytes(raw);
        foreach (var row in JsonNode.Parse(original)!["Results"]!.AsArray())
            Assert.IsTrue(row!.AsObject().ContainsKey("ProbeAddresses"));
        var published = Path.Combine(files.DirectoryPath, "published");
        var state = Path.Combine(files.DirectoryPath, "state.json");
        await ArtifactPublisher.PublishAsync(raw, published, files.LoadPolicy(), state);
        Assert.DoesNotContain("ProbeAddresses", File.ReadAllText(Path.Combine(published, "latest.json")));
        Assert.DoesNotContain("ProbeAddresses", File.ReadAllText(Path.Combine(published, "index.html")));
        File.WriteAllText(files.PrefixesPath, "172.20.220.48/28\nfdf0:e12c:5528::/48");
        Assert.AreEqual(0, await new RepublishCommand(files.LoadPolicy(), published, state).ExecuteAsync());
        var model = JsonNode.Parse(File.ReadAllText(Path.Combine(published, "latest.json")))!;
        Assert.HasCount(1, model["Results"]!.AsArray());
        Assert.AreEqual("keep.dn42", model["Results"]![0]!["Domain"]!.GetValue<string>());
        Assert.IsFalse(model["Results"]![0]!.AsObject().ContainsKey("ProbeAddresses"));
        Assert.DoesNotContain("ProbeAddresses", model.ToJsonString());
        Assert.DoesNotContain("ProbeAddresses", File.ReadAllText(Path.Combine(published, "index.html")));
        Assert.AreEqual("2026-10-03T12:50:26Z", model["GeneratedAt"]!.GetValue<string>());
        Assert.IsTrue(JsonNode.DeepEquals(model, Embedded(File.ReadAllText(Path.Combine(published, "index.html")))));
        CollectionAssert.AreEqual(original, File.ReadAllBytes(raw));
        var report = files.Write("report.html", "old report");
        await AtlasReportGenerator.GenerateAsync(raw, report, files.LoadPolicy());
        Assert.IsTrue(JsonNode.DeepEquals(model, Embedded(File.ReadAllText(report))));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("other.dn42")]
    public async Task PublicArtifactsStripProvenanceWithoutPrefixPolicy(string excludedHost)
    {
        using var files = new TestFiles(excludedHost);
        var raw = files.Write("scan.json", Scan(new JsonArray("172.20.220.49", "fdf0:e12c:5528::1")).ToJsonString());
        var original = File.ReadAllBytes(raw);
        Assert.Contains("ProbeAddresses", Encoding.UTF8.GetString(original));
        var published = Path.Combine(files.DirectoryPath, "published");
        await ArtifactPublisher.PublishAsync(raw, published, files.LoadPolicy(), Path.Combine(files.DirectoryPath, "state.json"));
        var json = File.ReadAllText(Path.Combine(published, "latest.json"));
        var html = File.ReadAllText(Path.Combine(published, "index.html"));
        var model = JsonNode.Parse(json)!;
        Assert.HasCount(1, model["Results"]!.AsArray());
        Assert.DoesNotContain("ProbeAddresses", json);
        Assert.DoesNotContain("ProbeAddresses", html);
        Assert.IsTrue(JsonNode.DeepEquals(model, Embedded(html)));
        CollectionAssert.AreEqual(original, File.ReadAllBytes(raw));
    }


    [TestMethod]
    public async Task UnsafeLegacyReportFailsWithoutLeavingOldHtmlOrChangingRaw()
    {
        using var files = new TestFiles(prefixes: "172.20.220.48/28");
        var raw = files.Write("legacy.json", Scan(null).ToJsonString());
        var bytes = File.ReadAllBytes(raw);
        var report = files.Write("legacy.html", "old report");
        await Assert.ThrowsExactlyAsync<MissingProbeAddressProvenanceException>(() =>
            AtlasReportGenerator.GenerateAsync(raw, report, files.LoadPolicy()));
        Assert.IsFalse(File.Exists(report));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(raw));
    }

    private static JsonObject Scan(JsonArray? addresses)
    {
        var row = new JsonObject { ["Domain"] = "target.dn42", ["Scheme"] = "http", ["Port"] = 80 };
        if (addresses != null) row["ProbeAddresses"] = addresses;
        return new JsonObject { ["GeneratedAt"] = "2026-10-03T12:50:26Z", ["ExcludedByHostname"] = 0,
            ["ExcludedByPrefix"] = 0, ["Results"] = new JsonArray(row) };
    }

    private static JsonNode Embedded(string html)
    {
        var start = html.IndexOf("const scan = ", StringComparison.Ordinal) + "const scan = ".Length;
        return JsonNode.Parse(html[start..html.IndexOf(';', start)])!;
    }
}
