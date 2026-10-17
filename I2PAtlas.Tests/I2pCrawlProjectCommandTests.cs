using I2PAtlas.Commands;
using I2PAtlas.I2pPublishing;
using static I2PAtlas.Tests.CrawlProjectionTestData;

namespace I2PAtlas.Tests;

[TestClass]
public sealed class I2pCrawlProjectCommandTests
{
    [TestMethod]
    public async Task CompiledCommandIsEntirelyLocalAndPrintsOnlyBoundedCounts()
    {
        using var files = new TemporaryFiles();
        var input = await WriteRawAsync(files);
        var output = Path.Combine(files.DirectoryPath, "public.json");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        using var network = new I2pCrawlProjectionTests.NetworkObserver();
        var oldOutput = Console.Out;
        var oldError = Console.Error;
        var oldProxy = Environment.GetEnvironmentVariable("HTTP_PROXY");
        try
        {
            Environment.SetEnvironmentVariable("HTTP_PROXY", "http://PRIVATE_AMBIENT_PROXY.invalid:1");
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var result = typeof(I2pCrawlProjectCommand).Assembly.EntryPoint!.Invoke(null,
                [new[] { "i2p-crawl-project", input, output }]);
            Assert.AreEqual(0, result is Task<int> task ? await task : (int)result!);
        }
        finally
        {
            Console.SetOut(oldOutput);
            Console.SetError(oldError);
            Environment.SetEnvironmentVariable("HTTP_PROXY", oldProxy);
        }
        Assert.AreEqual("", stderr.ToString());
        Assert.Contains("2 known seeds, 2 discovered hosts", stdout.ToString());
        Assert.Contains("unauthenticated", stdout.ToString());
        Assert.IsLessThan(256, stdout.ToString().Length);
        foreach (var secret in new[] { input, output, "a.i2p", "child.i2p", "PRIVATE_", "127.0.0.2", "43891" })
            Assert.DoesNotContain(secret, stdout.ToString());
        Assert.IsEmpty(network.Activity);
        Assert.AreEqual(2, (await I2pPublicCrawlReader.ReadAsync(output)).DiscoveredHostCount);
    }

    [TestMethod]
    [DataRow("usage", 2)][DataRow("extra-argument", 2)][DataRow("empty-path", 2)][DataRow("same-path", 2)]
    [DataRow("url", 2)][DataRow("unc", 2)][DataRow("device", 2)]
    [DataRow("missing-input", 1)][DataRow("missing-directory", 1)][DataRow("directory-output", 1)]
    [DataRow("invalid-raw", 1)][DataRow("cancel", 130)]
    public async Task ErrorsHaveExpectedCodesAndDoNotDisclosePrivateValuesOrReplaceOutput(string failure, int expected)
    {
        using var files = new TemporaryFiles();
        var input = await WriteRawAsync(files);
        var original = await File.ReadAllBytesAsync(input);
        var output = files.Write("public.json", "previous public artifact");
        if (failure == "invalid-raw") File.WriteAllText(input, "PRIVATE_INVALID_ARTIFACT");
        var args = new[] { "i2p-crawl-project", failure == "missing-input" ? input + "missing" : input, failure switch
        {
            "same-path" => input, "url" => "https://PRIVATE_HOST.invalid/output", "unc" => @"\\PRIVATE_HOST\share\output",
            "device" => @"\\?\C:\PRIVATE_OUTPUT", "empty-path" => " ", "directory-output" => files.DirectoryPath,
            "missing-directory" => Path.Combine(files.DirectoryPath, "absent", "output.json"), _ => output
        } };
        if (failure == "usage") args = ["i2p-crawl-project"];
        if (failure == "extra-argument") args = [.. args, "--unknown"];
        using var token = new CancellationTokenSource();
        if (failure == "cancel") token.Cancel();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        using var network = new I2pCrawlProjectionTests.NetworkObserver();
        Assert.AreEqual(expected, await I2pCrawlProjectCommand.ExecuteAsync(args, stdout, stderr, token.Token));
        Assert.DoesNotContain("PRIVATE_", stdout.ToString() + stderr.ToString());
        Assert.DoesNotContain(input, stdout.ToString() + stderr.ToString());
        Assert.AreEqual("previous public artifact", await File.ReadAllTextAsync(output));
        if (failure != "invalid-raw") CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(input));
        Assert.IsEmpty(network.Activity);
        Assert.IsEmpty(Directory.GetFiles(files.DirectoryPath, ".i2p-public-crawl-*.tmp"));
    }
}
