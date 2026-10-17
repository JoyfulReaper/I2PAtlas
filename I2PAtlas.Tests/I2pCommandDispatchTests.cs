using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using I2PAtlas.Commands;

namespace I2PAtlas.Tests;

[TestClass]
public sealed class I2pCommandDispatchTests
{
    [TestMethod]
    [DataRow("")]
    [DataRow("help")]
    [DataRow("--help")]
    [DataRow("-h")]
    public async Task NoArgumentsAndHelpAreInertAndListOnlyI2pCommands(string command)
    {
        var result = await InvokeAsync(command.Length == 0 ? [] : [command]);
        Assert.AreEqual(0, result.ExitCode);
        Assert.IsTrue(result.Output.StartsWith("Usage: i2patlas [command]", StringComparison.Ordinal));
        var listed = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).SkipLast(1)
            .Select(line => line.Trim().Split(' ')[0]).ToArray();
        CollectionAssert.AreEquivalent(new[] { "import-seeds", "i2p-probe-test", "i2p-scan", "i2p-crawl", "i2p-project", "i2p-crawl-project", "i2p-view", "i2p-crawl-view" }, listed);
    }

    [TestMethod]
    [DataRow("unknown-command")]
    [DataRow("resolve")]
    [DataRow("web-scan")]
    [DataRow("report")]
    [DataRow("probe-test")]
    [DataRow("run")]
    [DataRow("registry-update")]
    [DataRow("republish")]
    [DataRow("publish-existing")]
    public async Task UnknownAndRemovedCommandsAreInertUsageErrors(string command)
    {
        var result = await InvokeAsync([command]);
        Assert.AreEqual(2, result.ExitCode);
        Assert.IsTrue(result.Output.StartsWith("Usage: i2patlas [command]", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("import-seeds")]
    [DataRow("i2p-probe-test")]
    [DataRow("i2p-scan")]
    [DataRow("i2p-crawl")]
    [DataRow("i2p-crawl-project")]
    [DataRow("i2p-project")]
    [DataRow("i2p-view")]
    [DataRow("i2p-crawl-view")]
    public async Task EveryI2pCommandDispatchesToItsOwnArgumentValidation(string command)
    {
        var result = await InvokeAsync([command]);
        Assert.AreEqual(2, result.ExitCode);
        Assert.IsTrue(result.Output.StartsWith($"Usage: i2patlas {command} ", StringComparison.Ordinal));
    }

    private static async Task<(int ExitCode, string Output)> InvokeAsync(string[] arguments)
    {
        using var files = new TemporaryFiles();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        using var network = new NetworkActivityListener();
        var previousDirectory = Environment.CurrentDirectory;
        var previousOutput = Console.Out;
        var previousError = Console.Error;
        try
        {
            Environment.CurrentDirectory = files.DirectoryPath;
            Console.SetOut(stdout);
            Console.SetError(stderr);
            // Exercise the compiled executable entry point, including its default and rejection paths.
            var result = typeof(ImportSeedsCommand).Assembly.EntryPoint!.Invoke(null, [arguments]);
            var exitCode = result is Task<int> task ? await task : (int)result!;
            Assert.IsEmpty(network.Activity, "CLI validation must not start HTTP, socket connections or DNS resolution.");
            Assert.IsEmpty(Directory.GetFileSystemEntries(files.DirectoryPath), "CLI validation must not mutate the working directory.");
            Assert.AreEqual("", stderr.ToString());
            return (exitCode, stdout.ToString());
        }
        finally
        {
            Console.SetOut(previousOutput);
            Console.SetError(previousError);
            Environment.CurrentDirectory = previousDirectory;
        }
    }

    private sealed class NetworkActivityListener : EventListener
    {
        public ConcurrentQueue<string> Activity { get; } = new();

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name is "System.Net.Http" or "System.Net.Sockets" or "System.Net.NameResolution")
                EnableEvents(eventSource, EventLevel.Informational);
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventName is "RequestStart" or "ConnectStart" or "ResolutionStart")
                Activity.Enqueue(eventData.EventSource.Name + ":" + eventData.EventName);
        }
    }
}
