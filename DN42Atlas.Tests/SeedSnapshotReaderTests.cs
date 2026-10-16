using System.Text.Json.Nodes;
using DN42Atlas.Seeds;

namespace DN42Atlas.Tests;

[TestClass]
public sealed class SeedSnapshotReaderTests
{
    [TestMethod]
    public async Task ImportedSnapshotRoundTripsWithPrivateProvenanceAndPreservedDestination()
    {
        using var files = new TestFiles();
        var destination = SeedSnapshotTestData.Destination(payloadLength: 4);
        var source = files.Write("private hosts.txt", $"Z.I2P.={destination}\na.i2p={SeedSnapshotTestData.Destination()}");
        var path = Path.Combine(files.DirectoryPath, "seeds.json");
        var imported = await new SeedSnapshotImporter().ImportAsync(source, path);
        File.Delete(source); // reader must not reopen source provenance
        var loaded = await SeedSnapshotReader.ReadAsync(path);
        Assert.AreEqual(imported.Schema, loaded.Schema);
        Assert.AreEqual(imported.Version, loaded.Version);
        Assert.AreEqual(imported.ImportedAtUtc, loaded.ImportedAtUtc);
        Assert.AreEqual(imported.Source.Path, loaded.Source.Path);
        Assert.AreEqual(imported.Source.Sha256, loaded.Source.Sha256);
        Assert.AreEqual(imported.Source.ByteLength, loaded.Source.ByteLength);
        CollectionAssert.AreEqual(new[] { "a.i2p", "z.i2p" }, loaded.Seeds.Select(s => s.Hostname).ToArray());
        Assert.AreEqual(destination, loaded.Seeds[1].Destination);
        Assert.IsTrue(destination.EndsWith("==", StringComparison.Ordinal));
        Assert.HasCount(2, loaded.Seeds);
        Assert.HasCount(1, loaded.Seeds.RemoveAt(0));
    }

    [TestMethod]
    [DataRow("wrong-schema")]
    [DataRow("wrong-schema-case")]
    [DataRow("version")]
    [DataRow("version-string")]
    [DataRow("missing-schema")]
    [DataRow("missing-timestamp")]
    [DataRow("timestamp-no-offset")]
    [DataRow("timestamp-non-utc")]
    [DataRow("null-source")]
    [DataRow("missing-source-path")]
    [DataRow("empty-source-path")]
    [DataRow("bad-hash")]
    [DataRow("negative-byte-length")]
    [DataRow("byte-length-string")]
    [DataRow("missing-hostname")]
    [DataRow("empty-hostname")]
    [DataRow("non-i2p-hostname")]
    [DataRow("missing-destination")]
    [DataRow("bad-destination")]
    [DataRow("null-destination")]
    [DataRow("seeds-not-array")]
    [DataRow("unknown-field")]
    public async Task InvalidSchemaRequiredFieldsTypesAndSeedsAreRejectedWithoutEchoingPrivateInput(string failure)
    {
        using var files = new TestFiles();
        var json = SeedSnapshotTestData.Snapshot();
        var entry = json["seeds"]![0]!.AsObject();
        var source = json["source"]!.AsObject();
        switch (failure)
        {
            case "wrong-schema": json["schema"] = "dn42atlas.seed-snapshot"; break;
            case "wrong-schema-case": json["schema"] = "I2PATLAS.seed-snapshot"; break;
            case "version": json["version"] = 2; break;
            case "version-string": json["version"] = "1"; break;
            case "missing-schema": json.Remove("schema"); break;
            case "missing-timestamp": json.Remove("importedAtUtc"); break;
            case "timestamp-no-offset": json["importedAtUtc"] = "2026-10-16T01:00:00"; break;
            case "timestamp-non-utc": json["importedAtUtc"] = "2026-10-16T01:00:00-04:00"; break;
            case "null-source": json["source"] = null; break;
            case "missing-source-path": source.Remove("path"); break;
            case "empty-source-path": source["path"] = ""; break;
            case "bad-hash": source["sha256"] = "not a sha256"; break;
            case "negative-byte-length": source["byteLength"] = -1; break;
            case "byte-length-string": source["byteLength"] = "100"; break;
            case "missing-hostname": entry.Remove("hostname"); break;
            case "empty-hostname": entry["hostname"] = ""; break;
            case "non-i2p-hostname": entry["hostname"] = "https://example.com/"; break;
            case "missing-destination": entry.Remove("destination"); break;
            case "bad-destination": entry["destination"] = "invalid secret destination"; break;
            case "null-destination": entry["destination"] = null; break;
            case "seeds-not-array": json["seeds"] = new JsonObject(); break;
            case "unknown-field": json["unrecognized"] = "secret"; break;
        }
        var path = files.Write("seeds.json", json.ToJsonString());
        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => SeedSnapshotReader.ReadAsync(path));
        Assert.DoesNotContain(SeedSnapshotTestData.Destination(), error.Message);
        Assert.DoesNotContain(SeedSnapshotTestData.SourcePath, error.Message);
        Assert.DoesNotContain("invalid secret destination", error.Message);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NormalizedPairsDeduplicateAndAlternateDestinationsSurvive(bool alternate)
    {
        using var files = new TestFiles();
        var json = SeedSnapshotTestData.Snapshot();
        json["seeds"]!.AsArray().Add(new JsonObject
        {
            ["hostname"] = " FRIENDLY.I2P. ",
            ["destination"] = "\t" + SeedSnapshotTestData.Destination(salt: alternate ? 1 : 0) + " "
        });
        json["seeds"]!.AsArray().Add(json["seeds"]![1]!.DeepClone());
        var path = files.Write("seeds.json", json.ToJsonString());
        var loaded = await SeedSnapshotReader.ReadAsync(path);
        Assert.HasCount(alternate ? 2 : 1, loaded.Seeds);
        Assert.IsTrue(loaded.Seeds.All(s => s.Hostname == "friendly.i2p"));
        Assert.Contains(SeedSnapshotTestData.Destination(), loaded.Seeds.Select(s => s.Destination));
        if (alternate) Assert.Contains(SeedSnapshotTestData.Destination(salt: 1), loaded.Seeds.Select(s => s.Destination));
    }

    [TestMethod]
    [DataRow("schema")]
    [DataRow("path")]
    [DataRow("hostname")]
    public async Task DuplicateJsonPropertiesAreRejected(string property)
    {
        using var files = new TestFiles();
        var json = SeedSnapshotTestData.Snapshot().ToJsonString();
        var value = property switch
        {
            "schema" => "i2patlas.seed-snapshot",
            "path" => SeedSnapshotTestData.SourcePath,
            _ => "friendly.i2p"
        };
        var existing = $"\"{property}\":{System.Text.Json.JsonSerializer.Serialize(value)}";
        var path = files.Write("seeds.json", json.Replace(existing, existing + "," + existing, StringComparison.Ordinal));
        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => SeedSnapshotReader.ReadAsync(path));
        Assert.Contains("duplicate property", error.Message);
    }

    [TestMethod]
    public async Task EmptySnapshotsRemainValidAndEntriesAreBoundedBeforeDeduplication()
    {
        using var files = new TestFiles();
        var json = SeedSnapshotTestData.Snapshot();
        json["seeds"] = new JsonArray();
        var path = files.Write("seeds.json", json.ToJsonString());
        Assert.IsEmpty((await SeedSnapshotReader.ReadAsync(path)).Seeds);
        var entries = new JsonArray();
        for (var i = 0; i < 10001; i++) entries.Add((JsonNode?)null);
        json["seeds"] = entries;
        File.WriteAllText(path, json.ToJsonString());
        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => SeedSnapshotReader.ReadAsync(path));
        Assert.Contains("at most 10000", error.Message);
    }

    [TestMethod]
    public async Task OversizedSnapshotFailsBeforeReadingPayload()
    {
        using var files = new TestFiles();
        var path = Path.Combine(files.DirectoryPath, "oversized.json");
        using (var stream = File.Create(path)) stream.SetLength(SeedSnapshotReader.MaxSnapshotBytes + 1L);
        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => SeedSnapshotReader.ReadAsync(path));
        Assert.Contains("32 MiB", error.Message);
    }

    [TestMethod]
    [DataRow("{")]
    [DataRow("[]")]
    [DataRow("{\"nested\":[[[[[[[[[0]]]]]]]]]}")]
    public async Task MalformedOrDeepJsonCannotEscapeValidation(string json)
    {
        using var files = new TestFiles();
        var path = files.Write("bad.json", json);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => SeedSnapshotReader.ReadAsync(path));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InvalidUtf8OrUnpairedUnicodeEscapesAreRejected(bool escaped)
    {
        using var files = new TestFiles();
        var json = SeedSnapshotTestData.Snapshot().ToJsonString()
            .Replace("friendly.i2p", escaped ? "\\uD800.i2p" : "\u00FF.i2p", StringComparison.Ordinal);
        var path = Path.Combine(files.DirectoryPath, "bad-encoding.json");
        await File.WriteAllBytesAsync(path, System.Text.Encoding.Latin1.GetBytes(json));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => SeedSnapshotReader.ReadAsync(path));
    }

    [TestMethod]
    [DataRow("http://example.i2p/seeds.json")]
    [DataRow("https://example.com/seeds.json")]
    [DataRow(@"\\server\share\seeds.json")]
    [DataRow("//server/share/seeds.json")]
    [DataRow(@"\\?\C:\seeds.json")]
    public async Task NetworkAndDevicePathsAreRejectedBeforeOpening(string path)
    {
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => SeedSnapshotReader.ReadAsync(path));
    }

    [TestMethod]
    public async Task MissingFileAndCallerCancellationRemainDistinguishable()
    {
        using var files = new TestFiles();
        var path = Path.Combine(files.DirectoryPath, "absent.json");
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => SeedSnapshotReader.ReadAsync(path));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => SeedSnapshotReader.ReadAsync(path, cancellation.Token));
    }
}

internal static class SeedSnapshotTestData
{
    public const string SourcePath = "/private/DO-NOT-PRINT-hosts.txt";

    public static JsonObject Snapshot() => new()
    {
        ["schema"] = "i2patlas.seed-snapshot",
        ["version"] = 1,
        ["importedAtUtc"] = "2026-10-16T01:00:00+00:00",
        ["source"] = new JsonObject { ["path"] = SourcePath, ["sha256"] = new string('A', 64), ["byteLength"] = 1000 },
        ["seeds"] = new JsonArray(new JsonObject { ["hostname"] = "friendly.i2p", ["destination"] = Destination() })
    };

    public static string Destination(int salt = 0, int payloadLength = 0)
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
}
