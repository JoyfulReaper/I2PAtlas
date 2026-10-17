using I2PAtlas.Commands;
using I2PAtlas.I2pCrawling;
using static I2PAtlas.Tests.CrawlTestData;

namespace I2PAtlas.Tests;

[TestClass]
public sealed class I2pCrawlCommandTests
{
    [TestMethod]
    [DataRow("--concurrency", "0")][DataRow("--concurrency", "5")]
    [DataRow("--timeout-ms", "0")][DataRow("--timeout-ms", "60001")]
    [DataRow("--max-seeds", "0")][DataRow("--max-seeds", "10001")]
    [DataRow("--max-discovered-hosts", "-1")][DataRow("--max-discovered-hosts", "1001")]
    [DataRow("--max-depth", "0")][DataRow("--max-depth", "4")][DataRow("--unknown", "1")]
    [DataRow("--concurrency", "two")][DataRow("--concurrency", "+1")]
    public async Task InvalidOptionsAreUsageErrorsWithoutReadingInputOrMutatingOutput(string option, string value)
    {
        using var files = new TemporaryFiles();
        var outputPath = files.Write("crawl.json", "previous");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        Assert.AreEqual(2, await I2pCrawlCommand.ExecuteAsync(["i2p-crawl", "missing.json", outputPath,
            "http://127.0.0.1:4444", option, value], stdout, stderr));
        Assert.IsTrue(stdout.ToString().StartsWith("Usage: i2patlas i2p-crawl ", StringComparison.Ordinal));
        Assert.AreEqual("", stderr.ToString());
        Assert.AreEqual("previous", await File.ReadAllTextAsync(outputPath));
    }

    [TestMethod]
    [DataRow("http://localhost:4444")][DataRow("http://192.0.2.1:4444")][DataRow("http://user:secret@127.0.0.1:4444")]
    [DataRow("http://127.0.0.1")][DataRow("")]
    public async Task InvalidExplicitProxyIsRejectedBeforeAnyInputOrOutputAccess(string proxy)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        Assert.AreEqual(2, await I2pCrawlCommand.ExecuteAsync(["i2p-crawl", "nonexistent.json", "nonexistent-directory/out.json", proxy], stdout, stderr));
        Assert.AreEqual("", stderr.ToString());
        Assert.DoesNotContain("secret", stdout.ToString());
    }

    [TestMethod]
    public async Task MissingExtraAndDuplicateArgumentsAreUsageErrors()
    {
        foreach (var args in new[] {
            new[] { "i2p-crawl" },
            new[] { "i2p-crawl", "a", "b" },
            new[] { "i2p-crawl", "a", "b", "http://127.0.0.1:4444", "--concurrency" },
            new[] { "i2p-crawl", "a", "b", "http://127.0.0.1:4444", "--concurrency", "1", "--concurrency", "2" }
        })
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            Assert.AreEqual(2, await I2pCrawlCommand.ExecuteAsync(args, output, error));
        }
    }

    [TestMethod]
    public void SettingsHaveFiveStrictlyBoundedControlsWithCompatibleDepthDefault()
    {
        var defaults = new I2pCrawlSettings();
        Assert.AreEqual(2, defaults.Concurrency);
        Assert.AreEqual(10000, defaults.RequestTimeoutMilliseconds);
        Assert.AreEqual(10000, defaults.MaximumSeeds);
        Assert.AreEqual(100, defaults.MaximumDiscoveredHosts);
        Assert.AreEqual(1, defaults.MaximumDepth);
        CollectionAssert.AreEquivalent(new[] { "Concurrency", "RequestTimeoutMilliseconds", "MaximumSeeds", "MaximumDiscoveredHosts", "MaximumDepth" },
            typeof(I2pCrawlSettings).GetProperties().Select(p => p.Name).ToArray());
        _ = new I2pCrawlSettings(1, 1, 1, 0);
        _ = new I2pCrawlSettings(4, 60000, 10000, 1000);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new I2pCrawlSettings(5));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new I2pCrawlSettings(requestTimeoutMilliseconds: 60001));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new I2pCrawlSettings(maximumSeeds: 10001));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new I2pCrawlSettings(maximumDiscoveredHosts: 1001));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new I2pCrawlSettings(maximumDepth: 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new I2pCrawlSettings(maximumDepth: 4));
    }

    [TestMethod]
    public async Task TargetFailuresStillInstallPrivateArtifactWithBoundedSanitizedSummary()
    {
        using var files = new TemporaryFiles();
        var seed = Seed("PRIVATE-HOSTNAME.i2p");
        var input = Snapshot(files, seed);
        var outputPath = Path.Combine(files.DirectoryPath, "crawl.json");
        await using var proxy = new CrawlProxy((_, _) => Task.FromResult<byte[]?>(Response(504, "PRIVATE_ERROR_BODY")));
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.AreEqual(0, await I2pCrawlCommand.ExecuteAsync(["i2p-crawl", input, outputPath, proxy.Uri,
            "--max-discovered-hosts", "0", "--max-seeds", "1", "--concurrency", "1", "--timeout-ms", "1000"], output, error));
        Assert.Contains("Depth 0 complete: 1 probed", error.ToString());
        foreach (var secret in new[] { seed.Hostname, seed.Destination, B32(seed), proxy.Uri, "PRIVATE_ERROR_BODY" })
            Assert.DoesNotContain(secret, error.ToString());
        Assert.Contains("Responses remain unauthenticated", output.ToString());
        Assert.IsLessThan(512, output.ToString().Length);
        Assert.DoesNotContain(seed.Destination, output.ToString());
        Assert.DoesNotContain(seed.Hostname, output.ToString());
        Assert.DoesNotContain(proxy.Uri, output.ToString());
        Assert.DoesNotContain("PRIVATE_ERROR_BODY", output.ToString());
        Assert.AreEqual(1, (await I2pRawCrawlReader.ReadAsync(outputPath)).SelectedSeedCount);
    }

    [TestMethod]
    public async Task CancellationAndValidationIoFailuresHaveDistinctExitCodesAndPreserveOutput()
    {
        using var files = new TemporaryFiles();
        var input = Snapshot(files, Seed("a.i2p"));
        var output = files.Write("crawl.json", "previous");
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var args = new[] { "i2p-crawl", input, output, "http://127.0.0.1:4444" };
        Assert.AreEqual(130, await I2pCrawlCommand.ExecuteAsync(args, stdout, stderr, stop.Token));
        File.WriteAllText(input, "PRIVATE_INVALID_JSON");
        Assert.AreEqual(1, await I2pCrawlCommand.ExecuteAsync(args, stdout, stderr));
        args[1] = input + "missing";
        Assert.AreEqual(1, await I2pCrawlCommand.ExecuteAsync(args, stdout, stderr));
        Assert.DoesNotContain("PRIVATE_INVALID_JSON", stderr.ToString());
        Assert.DoesNotContain(input, stderr.ToString());
        Assert.AreEqual("previous", await File.ReadAllTextAsync(output));
    }

    [TestMethod]
    [DataRow("public")][DataRow("published")][DataRow("site")][DataRow("wwwroot")]
    public async Task PublicOutputPathsFailBeforeAnyRequest(string directory)
    {
        using var files = new TemporaryFiles();
        var input = Snapshot(files, Seed("a.i2p"));
        var folder = Directory.CreateDirectory(Path.Combine(files.DirectoryPath, directory)).FullName;
        var output = Path.Combine(folder, "crawl.json");
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => new I2pCrawlService().CrawlAsync(input, output, "http://127.0.0.1:4444"));
        Assert.IsFalse(File.Exists(output));
    }
}
