using System.Diagnostics;
using System.Globalization;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DN42Atlas.AddressBook;
using DN42Atlas.Commands;
using DN42Atlas.Seeds;

namespace DN42Atlas.Tests;

[TestClass]
public sealed class SeedSnapshotImportTests
{
    private static readonly DateTimeOffset ImportedAt = new(2026, 10, 15, 21, 0, 0, TimeSpan.FromHours(-4));
    private static readonly string Destination = MakeDestination();
    private static readonly string PaddedDestination = MakeDestination(1, payloadLength: 4);

    [TestMethod]
    public async Task ValidImportUsesExplicitI2pSchemaPrivateProvenanceAndImmutableSeeds()
    {
        using var files = new TestFiles();
        var input = files.Write("hosts.txt", $"Z.I2P.={Destination}\n A.I2P = {PaddedDestination}\t\nZ.i2p={Destination}");
        var output = Path.Combine(files.DirectoryPath, "seeds.json");
        I2pSeedSnapshot snapshot = await Importer().ImportAsync(input, output);

        Assert.AreEqual("i2patlas.seed-snapshot", snapshot.Schema);
        Assert.AreEqual(1, snapshot.Version);
        Assert.AreEqual(ImportedAt.ToUniversalTime(), snapshot.ImportedAtUtc);
        Assert.AreEqual(TimeSpan.Zero, snapshot.ImportedAtUtc.Offset);
        Assert.AreEqual(Path.GetFullPath(input), snapshot.Source.Path);
        CollectionAssert.AreEqual(new[] { "a.i2p", "z.i2p" }, snapshot.Seeds.Select(s => s.Hostname).ToArray());
        Assert.AreEqual(PaddedDestination, snapshot.Seeds[0].Destination);
        Assert.AreEqual(Destination, snapshot.Seeds[1].Destination);
        var copy = snapshot.Seeds.RemoveAt(0);
        Assert.HasCount(2, snapshot.Seeds);
        Assert.HasCount(1, copy);

        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(output));
        var root = document.RootElement;
        // This boundary has an I2P contract, not DomainResolution/RegistryResolutionResult fields.
        CollectionAssert.AreEqual(new[] { "schema", "version", "importedAtUtc", "source", "seeds" },
            root.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.AreEqual(snapshot.Schema, root.GetProperty("schema").GetString());
        Assert.AreEqual(1, root.GetProperty("version").GetInt32());
        Assert.AreEqual(TimeSpan.Zero, root.GetProperty("importedAtUtc").GetDateTimeOffset().Offset);
        CollectionAssert.AreEqual(new[] { "path", "sha256", "byteLength" },
            root.GetProperty("source").EnumerateObject().Select(p => p.Name).ToArray());
        var seed = root.GetProperty("seeds")[0];
        CollectionAssert.AreEqual(new[] { "hostname", "destination" }, seed.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.AreEqual("a.i2p", seed.GetProperty("hostname").GetString());
        Assert.AreEqual(PaddedDestination, seed.GetProperty("destination").GetString());
        var bytes = await File.ReadAllBytesAsync(output);
        Assert.IsFalse(bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }));
        if (!OperatingSystem.IsWindows())
            Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(output));
        Assert.IsEmpty(Directory.GetFiles(files.DirectoryPath, ".i2p-seeds-*.tmp"));
    }

    [TestMethod]
    [DataRow(false, "\n")]
    [DataRow(true, "\r\n")]
    [DataRow(true, "\r")]
    public async Task SourceHashCoversExactBytesIncludingBomCommentsDuplicatesAndWhitespace(bool bom, string newline)
    {
        using var files = new TestFiles();
        var path = Path.Combine(files.DirectoryPath, "hosts.txt");
        var text = string.Join(newline, "# café", $" example.I2P. = \t{PaddedDestination} \t",
            $"EXAMPLE.i2p={PaddedDestination}", "", "; comment");
        File.WriteAllText(path, text, new UTF8Encoding(bom, true));
        var original = await File.ReadAllBytesAsync(path);
        var output = Path.Combine(files.DirectoryPath, "seeds.json");
        var snapshot = await Importer().ImportAsync(path, output);
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(original)), snapshot.Source.Sha256);
        Assert.AreEqual(original.LongLength, snapshot.Source.ByteLength);
        var serialized = JsonNode.Parse(await File.ReadAllTextAsync(output))!;
        Assert.AreEqual(snapshot.Source.Sha256, serialized["source"]!["sha256"]!.GetValue<string>());
        Assert.AreEqual(original.LongLength, serialized["source"]!["byteLength"]!.GetValue<long>());
        Assert.AreEqual(PaddedDestination, serialized["seeds"]![0]!["destination"]!.GetValue<string>());
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(path));
    }

    [TestMethod]
    public async Task FixedClockProducesIdenticalArtifactBytesAndReorderedInputsKeepOrdinalSeedOrder()
    {
        using var files = new TestFiles();
        var input = files.Write("hosts.txt", $"Z.I2P={Destination}\nI.I2P={PaddedDestination}\na.i2p={Destination}");
        var output = Path.Combine(files.DirectoryPath, "seeds.json");
        var first = await Importer().ImportAsync(input, output);
        var bytes = await File.ReadAllBytesAsync(output);
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            await Importer().ImportAsync(input, output);
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(output));
            File.WriteAllText(input, $"a.i2p={Destination}\nI.I2P={PaddedDestination}\nZ.I2P={Destination}");
            var reordered = await Importer().ImportAsync(input, output);
            CollectionAssert.AreEqual(first.Seeds.ToArray(), reordered.Seeds.ToArray());
            Assert.AreNotEqual(first.Source.Sha256, reordered.Source.Sha256);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [TestMethod]
    [DataRow("malformed")]
    [DataRow("invalid-metadata")]
    [DataRow("missing-destination")]
    [DataRow("invalid-utf8")]
    [DataRow("oversized-line")]
    public async Task FailedValidationPreservesPreviousValidOutputAndSource(string failure)
    {
        using var files = new TestFiles();
        var input = files.Write("hosts.txt", $"example.i2p={Destination}");
        var output = Path.Combine(files.DirectoryPath, "seeds.json");
        await Importer().ImportAsync(input, output);
        var previous = await File.ReadAllBytesAsync(output);
        if (failure == "invalid-utf8") File.WriteAllBytes(input, [0xff]);
        else File.WriteAllText(input, failure switch
        {
            "malformed" => "not an entry",
            "invalid-metadata" => $"example.i2p={Destination}\nEXAMPLE.I2P.={MakeDestination(2)}#!sig=",
            "missing-destination" => "example.i2p=",
            _ => "#" + new string('a', AddressBookParser.MaxLineCharacters)
        });
        var source = await File.ReadAllBytesAsync(input);
        if (failure == "invalid-utf8")
            await Assert.ThrowsExactlyAsync<DecoderFallbackException>(() => Importer().ImportAsync(input, output));
        else
            await Assert.ThrowsExactlyAsync<AddressBookParseException>(() => Importer().ImportAsync(input, output));
        CollectionAssert.AreEqual(previous, await File.ReadAllBytesAsync(output));
        CollectionAssert.AreEqual(source, await File.ReadAllBytesAsync(input));
        Assert.IsEmpty(Directory.GetFiles(files.DirectoryPath, ".i2p-seeds-*.tmp"));
    }

    [TestMethod]
    public async Task MissingInputDoesNotReplacePreviousOutputOrCreateDirectories()
    {
        using var files = new TestFiles();
        var output = files.Write("seeds.json", "previous output");
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() =>
            Importer().ImportAsync(Path.Combine(files.DirectoryPath, "missing.txt"), output));
        Assert.AreEqual("previous output", await File.ReadAllTextAsync(output));
        Assert.IsEmpty(Directory.GetFiles(files.DirectoryPath, ".i2p-seeds-*.tmp"));
    }

    [TestMethod]
    public async Task MissingOrFileShapedOutputDirectoryFailsWithoutCreatingIt()
    {
        using var files = new TestFiles();
        var input = files.Write("hosts.txt", $"example.i2p={Destination}");
        var missing = Path.Combine(files.DirectoryPath, "missing");
        await Assert.ThrowsAsync<IOException>(() => Importer().ImportAsync(input, Path.Combine(missing, "seeds.json")));
        Assert.IsFalse(Directory.Exists(missing));
        var obstruction = files.Write("not-a-directory", "preserve");
        await Assert.ThrowsAsync<IOException>(() => Importer().ImportAsync(input, Path.Combine(obstruction, "seeds.json")));
        Assert.AreEqual("preserve", await File.ReadAllTextAsync(obstruction));
        Assert.IsEmpty(Directory.GetFiles(files.DirectoryPath, ".i2p-seeds-*.tmp"));
    }

    [TestMethod]
    public async Task UnwritableOutputDirectoryPreservesAnExistingArtifact()
    {
        // Root bypasses Unix mode checks. Other accounts exercise the actual directory permission failure.
        if (!OperatingSystem.IsWindows() && Environment.UserName == "root") return;
        using var files = new TestFiles();
        var input = files.Write("hosts.txt", $"example.i2p={Destination}");
        var directory = new DirectoryInfo(Path.Combine(files.DirectoryPath, "private"));
        directory.Create();
        var output = Path.Combine(directory.FullName, "seeds.json");
        await Importer().ImportAsync(input, output);
        var previous = await File.ReadAllBytesAsync(output);
        if (OperatingSystem.IsWindows())
        {
            var original = directory.GetAccessControl();
            var restricted = directory.GetAccessControl();
            using var identity = WindowsIdentity.GetCurrent();
            restricted.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.CreateFiles, AccessControlType.Deny));
            try
            {
                directory.SetAccessControl(restricted);
                await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => Importer().ImportAsync(input, output));
            }
            finally { directory.SetAccessControl(original); }
        }
        else
        {
            var original = File.GetUnixFileMode(directory.FullName);
            try
            {
                File.SetUnixFileMode(directory.FullName, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => Importer().ImportAsync(input, output));
            }
            finally { File.SetUnixFileMode(directory.FullName, original); }
        }
        CollectionAssert.AreEqual(previous, await File.ReadAllBytesAsync(output));
        Assert.IsEmpty(Directory.GetFiles(directory.FullName, ".i2p-seeds-*.tmp"));
    }

    [TestMethod]
    public async Task FailedReplacementCleansStagingAndPreservesExistingDirectoryContents()
    {
        using var files = new TestFiles();
        var input = files.Write("hosts.txt", $"example.i2p={Destination}");
        var output = Path.Combine(files.DirectoryPath, "seeds.json");
        Directory.CreateDirectory(output);
        var sentinel = Path.Combine(output, "sentinel");
        File.WriteAllText(sentinel, "preserve");
        try
        {
            await Importer().ImportAsync(input, output);
            Assert.Fail("A file must not replace a directory.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Assert.AreEqual("preserve", await File.ReadAllTextAsync(sentinel));
        Assert.IsEmpty(Directory.GetFiles(files.DirectoryPath, ".i2p-seeds-*.tmp"));
    }

    [TestMethod]
    public async Task SameInputOutputAndPreCancelledImportNeverOverwriteInputOrOutput()
    {
        using var files = new TestFiles();
        var input = files.Write("hosts.txt", $"example.i2p={Destination}");
        var source = await File.ReadAllBytesAsync(input);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            Importer().ImportAsync(input, Path.Combine(files.DirectoryPath, ".", "hosts.txt")));
        var output = files.Write("seeds.json", "previous");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => Importer().ImportAsync(input, output, cancellation.Token));
        CollectionAssert.AreEqual(source, await File.ReadAllBytesAsync(input));
        Assert.AreEqual("previous", await File.ReadAllTextAsync(output));
    }

    [TestMethod]
    [DataRow("http://example.i2p/hosts.txt")]
    [DataRow("https://example.com/seeds.json")]
    [DataRow(@"\\server\share\hosts.txt")]
    [DataRow("//server/share/hosts.txt")]
    [DataRow(@"/\server\share\hosts.txt")]
    [DataRow(@"\/server/share/hosts.txt")]
    public async Task NetworkShapedPathsAreRejectedBeforeAnyFileAccess(string path)
    {
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => Importer().ImportAsync(path, "output.json"));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => Importer().ImportAsync("input.txt", path));
    }

    [TestMethod]
    public async Task EmptyBookProducesValidEmptyI2pSnapshot()
    {
        using var files = new TestFiles();
        var input = files.Write("hosts.txt", "");
        var output = Path.Combine(files.DirectoryPath, "seeds.json");
        var snapshot = await Importer().ImportAsync(input, output);
        Assert.IsEmpty(snapshot.Seeds);
        Assert.AreEqual(0L, snapshot.Source.ByteLength);
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData([])), snapshot.Source.Sha256);
        Assert.IsEmpty(JsonNode.Parse(await File.ReadAllTextAsync(output))!["seeds"]!.AsArray());
    }

    [TestMethod]
    public async Task CliImportsQuotedRelativePathsWithoutDn42PolicyStateOrRegistry()
    {
        using var files = new TestFiles();
        files.Write("local hosts.txt", $"EXAMPLE.I2P.={PaddedDestination}");
        var before = Directory.GetFiles(files.DirectoryPath).Order(StringComparer.Ordinal).ToArray();
        var result = await RunCli(files.DirectoryPath, "import-seeds", "local hosts.txt", "private seeds.json");
        Assert.AreEqual(0, result.ExitCode, result.Error);
        Assert.AreEqual("", result.Error);
        Assert.Contains("Imported 1 I2P seeds to:", result.Output);
        Assert.DoesNotContain(PaddedDestination, result.Output);
        Assert.DoesNotContain(PaddedDestination, result.Error);
        var artifact = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(files.DirectoryPath, "private seeds.json")))!;
        Assert.AreEqual("i2patlas.seed-snapshot", artifact["schema"]!.GetValue<string>());
        Assert.AreEqual("example.i2p", artifact["seeds"]![0]!["hostname"]!.GetValue<string>());
        Assert.IsFalse(File.Exists(Path.Combine(files.DirectoryPath, "domain-resolution.json")));
        Assert.IsFalse(Directory.Exists(Path.Combine(files.DirectoryPath, "config")));
        Assert.IsFalse(Directory.Exists(Path.Combine(files.DirectoryPath, "published")));
        CollectionAssert.AreEqual(before.Append(Path.Combine(files.DirectoryPath, "private seeds.json")).Order(StringComparer.Ordinal).ToArray(),
            Directory.GetFiles(files.DirectoryPath).Order(StringComparer.Ordinal).ToArray());
    }

    [TestMethod]
    [DataRow("missing-arguments", 2)]
    [DataRow("extra-arguments", 2)]
    [DataRow("empty-path", 2)]
    [DataRow("missing-input", 1)]
    [DataRow("malformed-input", 1)]
    [DataRow("invalid-metadata", 1)]
    [DataRow("missing-output-directory", 1)]
    public async Task CliUsageAndImportFailuresHaveClearExitCodesAndPreserveOutput(string failure, int code)
    {
        using var files = new TestFiles();
        files.Write("hosts.txt", failure switch
        {
            "malformed-input" => "broken",
            "invalid-metadata" => $"example.i2p={Destination}\nexample.i2p={MakeDestination(2)}#!sig=",
            _ => $"example.i2p={Destination}"
        });
        var output = files.Write("seeds.json", "previous output");
        var args = failure switch
        {
            "missing-arguments" => new[] { "import-seeds", "hosts.txt" },
            "extra-arguments" => ["import-seeds", "hosts.txt", "seeds.json", "extra"],
            "empty-path" => ["import-seeds", "", "seeds.json"],
            "missing-input" => ["import-seeds", "missing.txt", "seeds.json"],
            "missing-output-directory" => ["import-seeds", "hosts.txt", "missing/seeds.json"],
            _ => ["import-seeds", "hosts.txt", "seeds.json"]
        };
        var result = await RunCli(files.DirectoryPath, args);
        Assert.AreEqual(code, result.ExitCode, result.Error);
        if (code == 2)
        {
            Assert.Contains("Usage: dn42atlas import-seeds <addressbook-file> <snapshot-file>", result.Output);
            Assert.AreEqual("", result.Error);
        }
        else
        {
            Assert.AreEqual("", result.Output);
            Assert.IsFalse(string.IsNullOrWhiteSpace(result.Error));
        }
        if (failure == "invalid-metadata") Assert.Contains("Metadata requires nonempty", result.Error);
        Assert.DoesNotContain(Destination, result.Error);
        Assert.AreEqual("previous output", await File.ReadAllTextAsync(output));
        Assert.IsEmpty(Directory.GetFiles(files.DirectoryPath, ".i2p-seeds-*.tmp"));
        Assert.IsFalse(File.Exists(Path.Combine(files.DirectoryPath, "domain-resolution.json")));
    }

    private static SeedSnapshotImporter Importer() => new(new FixedClock());

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => ImportedAt;
    }

    private static string MakeDestination(int salt = 0, int payloadLength = 0)
    {
        var bytes = new byte[387 + payloadLength];
        for (var i = 0; i < 384; i++) bytes[i] = (byte)(i * 73 + salt);
        if (payloadLength > 0)
        {
            bytes[384] = 5;
            bytes[385] = (byte)(payloadLength >> 8);
            bytes[386] = (byte)payloadLength;
            bytes[388] = 7;
        }
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '~');
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunCli(string directory, params string[] args)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        start.ArgumentList.Add(typeof(ImportSeedsCommand).Assembly.Location);
        foreach (var argument in args) start.ArgumentList.Add(argument);
        // All of these would obstruct accidental DN42 dispatch. Import must ignore them.
        start.Environment["DN42ATLAS_RUNTIME_EXCLUSIONS_PATH"] = Path.Combine(directory, "missing-runtime.json");
        start.Environment["DN42ATLAS_REGISTRY_PATH"] = Path.Combine(directory, "missing-registry");
        start.Environment["DN42ATLAS_PUBLICATION_STATE_PATH"] = Path.Combine(directory, "missing-state", "state.json");
        start.Environment["HTTP_PROXY"] = start.Environment["HTTPS_PROXY"] = start.Environment["ALL_PROXY"] = "http://127.0.0.1:1";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        return (process.ExitCode, await output, await error);
    }
}
