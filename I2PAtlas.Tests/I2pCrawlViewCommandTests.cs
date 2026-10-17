using I2PAtlas.Commands;
using I2PAtlas.I2pReporting;

namespace I2PAtlas.Tests;

[TestClass]
public sealed class I2pCrawlViewCommandTests
{
    [TestMethod]
    public async Task CompiledCommandReadsOnlyPublicCrawlAndProducesOfflineHtmlWithGenericSummary()
    {
        using var files = new TemporaryFiles();
        var input = I2pCrawlViewerTests.Write(files, I2pCrawlViewerTests.Fixture());
        var output = Path.Combine(files.DirectoryPath, "crawl.html");
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
            var result = typeof(I2pCrawlViewCommand).Assembly.EntryPoint!.Invoke(null, [new[] { "i2p-crawl-view", input, output }]);
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
        foreach (var secret in new[] { input, output, "a.i2p", "child.i2p", "PRIVATE_" }) Assert.DoesNotContain(secret, stdout.ToString());
        Assert.IsLessThan(256, stdout.ToString().Length);
        Assert.IsEmpty(network.Activity);
        Assert.Contains("<title>I2PAtlas crawl inspection</title>", await File.ReadAllTextAsync(output));
        CollectionAssert.AreEquivalent(new[] { input, output }, Directory.GetFiles(files.DirectoryPath));
    }

    [TestMethod]
    [DataRow("usage", 2)][DataRow("extra", 2)][DataRow("empty", 2)][DataRow("same", 2)]
    [DataRow("url-input", 2)][DataRow("url-output", 2)][DataRow("unc-input", 2)][DataRow("unc-output", 2)][DataRow("device", 2)]
    [DataRow("invalid", 1)][DataRow("missing", 1)][DataRow("missing-directory", 1)][DataRow("directory-output", 1)]
    [DataRow("wrong-schema", 1)][DataRow("cancel", 130)]
    public async Task BadInputsPathsAndCancellationReturnExpectedCodesAndPreserveHtmlWithoutLeaks(string failure, int expected)
    {
        using var files = new TemporaryFiles();
        var input = I2pCrawlViewerTests.Write(files, I2pCrawlViewerTests.Fixture());
        if (failure == "invalid") File.WriteAllText(input, "PRIVATE_INVALID_JSON");
        if (failure == "wrong-schema") File.WriteAllText(input, File.ReadAllText(input).Replace("i2patlas.public-crawl", "i2patlas.public-scan", StringComparison.Ordinal));
        var original = await File.ReadAllBytesAsync(input);
        var output = files.Write("crawl.html", "previous HTML");
        var args = new[] { "i2p-crawl-view", failure switch
        {
            "url-input" => "https://PRIVATE_HOST.invalid/public.json", "unc-input" => @"\\PRIVATE_HOST\share\public.json",
            "missing" => input + "missing", _ => input
        }, failure switch
        {
            "url-output" => "https://PRIVATE_HOST.invalid/crawl.html", "unc-output" => @"\\PRIVATE_HOST\share\crawl.html",
            "device" => @"\\?\C:\PRIVATE_OUTPUT", "same" => input, "empty" => " ", "directory-output" => files.DirectoryPath,
            "missing-directory" => Path.Combine(files.DirectoryPath, "absent", "crawl.html"), _ => output
        } };
        if (failure == "usage") args = ["i2p-crawl-view"];
        if (failure == "extra") args = [.. args, "--unknown"];
        using var cancel = new CancellationTokenSource();
        if (failure == "cancel") cancel.Cancel();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        using var network = new I2pCrawlProjectionTests.NetworkObserver();
        Assert.AreEqual(expected, await I2pCrawlViewCommand.ExecuteAsync(args, stdout, stderr, cancel.Token));
        Assert.DoesNotContain(input, stdout.ToString() + stderr.ToString());
        Assert.DoesNotContain("PRIVATE_", stdout.ToString() + stderr.ToString());
        Assert.AreEqual("previous HTML", await File.ReadAllTextAsync(output));
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(input));
        Assert.IsEmpty(network.Activity);
        Assert.IsEmpty(Directory.GetFiles(files.DirectoryPath, ".i2p-crawl-view-*.tmp"));
    }
}
