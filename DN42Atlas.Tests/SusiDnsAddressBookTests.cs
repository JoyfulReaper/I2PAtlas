using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DN42Atlas.AddressBook;
using DN42Atlas.Seeds;

namespace DN42Atlas.Tests;

[TestClass]
public sealed class SusiDnsAddressBookTests
{
    private static readonly string Destination = SeedSnapshotTestData.Destination(payloadLength: 4);

    [TestMethod]
    public void PlainEntriesStillHaveNoMetadata()
    {
        var parsed = Parse($"example.i2p={Destination}");
        Assert.AreEqual(Destination, parsed.Seeds.Single().Destination);
        Assert.IsEmpty(parsed.EntryMetadata);
    }

    [TestMethod]
    public void ExtendedEntrySeparatesDestinationAndRetainsStructuredCaseSensitiveMetadata()
    {
        var parsed = Parse($"# header\r\n EXAMPLE.I2P. = \t{Destination}\t #!date=1790878952#sig=AbC-~==#vendor.key=keep=this=value \t");
        Assert.AreEqual(new I2pSeed("example.i2p", Destination), parsed.Seeds.Single());
        Assert.AreEqual(Destination, parsed.Seeds[0].Destination);
        Assert.IsTrue(Destination.EndsWith("==", StringComparison.Ordinal));
        var metadata = parsed.EntryMetadata.Single();
        Assert.AreEqual(2, metadata.LineNumber);
        Assert.AreEqual("example.i2p", metadata.Hostname);
        Assert.AreEqual("1790878952", metadata.Properties["date"]);
        Assert.AreEqual("AbC-~==", metadata.Properties["sig"]);
        Assert.AreEqual("keep=this=value", metadata.Properties["vendor.key"]);
        var changed = metadata.Properties.SetItem("sig", "other");
        Assert.AreEqual("AbC-~==", metadata.Properties["sig"]);
        Assert.AreEqual("other", changed["sig"]);
        Assert.AreEqual(Destination, parsed.Seeds[0].Destination);
        Assert.ThrowsExactly<FormatException>(() => new I2pSeed("example.i2p", Destination + "#!date=1790878952"));
    }

    [TestMethod]
    public void FixtureMatchesObservedRouterExportShapeWithSyntheticKeysAndSignature()
    {
        using var stream = Fixture();
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        var parsed = AddressBookParser.Parse(reader, "SusiDNS regression fixture");
        Assert.AreEqual(new I2pSeed("8ightball.i2p", Destination), parsed.Seeds.Single());
        Assert.AreEqual("1790878952", parsed.EntryMetadata.Single().Properties["date"]);
        Assert.AreEqual(88, parsed.EntryMetadata.Single().Properties["sig"].Length);
        Assert.HasCount(2, parsed.EntryMetadata.Single().Properties);
    }

    [TestMethod]
    [DataRow("truncated")]
    [DataRow("bad-certificate")]
    [DataRow("bad-padding")]
    [DataRow("internal-whitespace")]
    [DataRow("empty")]
    public void MetadataNeverBypassesDeepDestinationValidation(string failure)
    {
        var destination = failure switch
        {
            "truncated" => Destination[..^4],
            "bad-certificate" => Convert.ToBase64String(new byte[391]).Replace('+', '-').Replace('/', '~'),
            "bad-padding" => Destination[..^3] + "B==",
            "internal-whitespace" => Destination[..100] + " " + Destination[100..],
            _ => ""
        };
        Assert.ThrowsExactly<FormatException>(() => new I2pSeed("example.i2p", destination));
        Assert.ThrowsExactly<AddressBookParseException>(() => Parse($"example.i2p={destination}#!date=1790878952#sig=opaque"));
    }

    [TestMethod]
    [DataRow("#!")]
    [DataRow("#!date")]
    [DataRow("#!date=")]
    [DataRow("#!date= #sig=opaque")]
    [DataRow("#!=value")]
    [DataRow("#!date=1#sig")]
    [DataRow("#!date=1#sig=")]
    [DataRow("#!date=1#=opaque")]
    [DataRow("#!date=1#")]
    [DataRow("#!date=1##sig=opaque")]
    [DataRow("#!!date=1")]
    [DataRow("#!date=1#!sig=opaque")]
    [DataRow("#!date=1#sig=opaque#garbage")]
    [DataRow("#! bad=value")]
    [DataRow("#!bad key=value")]
    [DataRow("#!1bad=value")]
    [DataRow("#!note=bad\tvalue")]
    [DataRow("#!note=bad\0value")]
    [DataRow("#!note=café")]
    [DataRow("#!action=add")]
    public void MalformedMetadataFailsWithoutEchoingValues(string suffix)
    {
        var error = Assert.ThrowsExactly<AddressBookParseException>(() => Parse($"# source\nexample.i2p={Destination}{suffix}"));
        Assert.AreEqual(2, error.LineNumber);
        Assert.DoesNotContain(Destination, error.Message);
        Assert.DoesNotContain("opaque", error.Message);
    }

    [TestMethod]
    [DataRow("#date=1790878952")]
    [DataRow(" # comment")]
    [DataRow("# comment#!date=1")]
    [DataRow("# !date=1")]
    [DataRow("##!date=1")]
    [DataRow("!#date=1")]
    [DataRow("garbage#!date=1")]
    [DataRow("=garbage#!date=1")]
    public void LiteralHashMalformedDelimitersAndGarbagePrefixesAreNeverStripped(string suffix)
    {
        Assert.ThrowsExactly<AddressBookParseException>(() => Parse($"example.i2p={Destination}{suffix}"));
    }

    [TestMethod]
    [DataRow("date=1#date=1")]
    [DataRow("sig=first#sig=second")]
    [DataRow("unknown=x#unknown=y")]
    public void DuplicateMetadataKeysFailEvenWhenValuesAreIdentical(string suffix)
    {
        var error = Assert.ThrowsExactly<AddressBookParseException>(() => Parse($"example.i2p={Destination}#!{suffix}"));
        Assert.Contains("Duplicate metadata key", error.Message);
    }

    [TestMethod]
    public void UnknownPropertiesRemainOpaqueAndCannotOverrideIdentityOrActAsCommands()
    {
        var parsed = Parse($"example.i2p={Destination}#!date=opaque#Date=other#sig=unverified#hostname=other.i2p#dest=not-a-destination#vendor-note=hello world");
        Assert.AreEqual(new I2pSeed("example.i2p", Destination), parsed.Seeds.Single());
        var metadata = parsed.EntryMetadata.Single().Properties;
        Assert.AreEqual("opaque", metadata["date"]);
        Assert.AreEqual("other", metadata["Date"]);
        Assert.AreEqual("unverified", metadata["sig"]);
        Assert.AreEqual("other.i2p", metadata["hostname"]);
        Assert.AreEqual("not-a-destination", metadata["dest"]);
        Assert.AreEqual("hello world", metadata["vendor-note"]);
    }

    [TestMethod]
    public void MappingDuplicatesKeepTheirOriginalRulesAndMetadataRemainsPerLine()
    {
        var parsed = Parse($"example.i2p={Destination}#!date=1#sig=first\nEXAMPLE.I2P.={Destination}#!date=2#sig=second\nexample.i2p={Destination}");
        Assert.HasCount(1, parsed.Seeds);
        Assert.HasCount(2, parsed.EntryMetadata);
        CollectionAssert.AreEqual(new[] { 1, 2 }, parsed.EntryMetadata.Select(m => m.LineNumber).ToArray());
        Assert.AreEqual("first", parsed.EntryMetadata[0].Properties["sig"]);
        Assert.AreEqual("second", parsed.EntryMetadata[1].Properties["sig"]);
        Assert.ThrowsExactly<AddressBookParseException>(() => Parse(
            $"example.i2p={Destination}#!date=1\nEXAMPLE.I2P.={SeedSnapshotTestData.Destination(salt: 1)}#!date=1"));
        Assert.ThrowsExactly<AddressBookParseException>(() => Parse(
            $"example.i2p={Destination}#!date=1\nEXAMPLE.I2P.={Destination.TrimEnd('=')}#!date=1"));
        // An identical mapping cannot hide an invalid suffix on a later duplicate.
        Assert.ThrowsExactly<AddressBookParseException>(() => Parse($"example.i2p={Destination}\nexample.i2p={Destination}#!sig="));
    }

    [TestMethod]
    public void MetadataLimitsAreInclusiveAndApplyBeforeDeduplication()
    {
        var maximumKey = "k" + new string('x', AddressBookParser.MaxMetadataKeyCharacters - 1);
        var maximumValue = new string('v', AddressBookParser.MaxMetadataValueCharacters);
        Assert.AreEqual(maximumValue, Parse($"example.i2p={Destination}#!{maximumKey}={maximumValue}")
            .EntryMetadata.Single().Properties[maximumKey]);
        Assert.ThrowsExactly<AddressBookParseException>(() => Parse($"example.i2p={Destination}#!{maximumKey}x=v"));
        Assert.ThrowsExactly<AddressBookParseException>(() => Parse($"example.i2p={Destination}#!k={maximumValue}v"));
        var properties = string.Join('#', Enumerable.Range(0, AddressBookParser.MaxMetadataProperties).Select(i => $"k{i}=v"));
        Assert.HasCount(AddressBookParser.MaxMetadataProperties, Parse($"example.i2p={Destination}#!{properties}").EntryMetadata.Single().Properties);
        Assert.ThrowsExactly<AddressBookParseException>(() => Parse($"example.i2p={Destination}#!{properties}#extra=v"));
        // 2 key=value pairs and one separator consume 5 framing characters.
        var exact = "a=" + maximumValue + "#b=" + new string('v', AddressBookParser.MaxMetadataCharacters - 5 - maximumValue.Length);
        Assert.AreEqual(AddressBookParser.MaxMetadataCharacters, exact.Length);
        Assert.HasCount(2, Parse($"example.i2p={Destination}#!{exact}").EntryMetadata.Single().Properties);
        Assert.ThrowsExactly<AddressBookParseException>(() => Parse($"example.i2p={Destination}\nexample.i2p={Destination}#!{exact}v"));
    }

    [TestMethod]
    public async Task ImportFixtureHashesAllOriginalBytesAndKeepsMetadataOutOfSeedJson()
    {
        using var files = new TestFiles();
        var input = Path.Combine(files.DirectoryPath, "router-export.txt");
        using var fixture = Fixture();
        using var memory = new MemoryStream();
        await fixture.CopyToAsync(memory);
        var original = memory.ToArray();
        await File.WriteAllBytesAsync(input, original);
        var output = Path.Combine(files.DirectoryPath, "seeds.json");
        var importer = new SeedSnapshotImporter();
        var imported = await importer.ImportAsync(input, output);
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(original)), imported.Source.Sha256);
        Assert.AreEqual(original.LongLength, imported.Source.ByteLength);
        Assert.AreEqual(new I2pSeed("8ightball.i2p", Destination), imported.Seeds.Single());
        var json = JsonNode.Parse(await File.ReadAllTextAsync(output))!;
        CollectionAssert.AreEqual(new[] { "hostname", "destination" }, json["seeds"]![0]!.AsObject().Select(p => p.Key).ToArray());
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(input));
        var changed = Encoding.UTF8.GetString(original).Replace("1790878952", "1790878953", StringComparison.Ordinal);
        await File.WriteAllTextAsync(input, changed, new UTF8Encoding(false, true));
        var reimported = await importer.ImportAsync(input, output);
        Assert.AreEqual(imported.Seeds.Single(), reimported.Seeds.Single());
        Assert.AreNotEqual(imported.Source.Sha256, reimported.Source.Sha256);
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(input))), reimported.Source.Sha256);
        var validOutput = await File.ReadAllBytesAsync(output);
        await File.WriteAllTextAsync(input, $"example.i2p={Destination}#!date=1#sig=");
        await Assert.ThrowsExactlyAsync<AddressBookParseException>(() => importer.ImportAsync(input, output));
        CollectionAssert.AreEqual(validOutput, await File.ReadAllBytesAsync(output));
    }

    private static Stream Fixture() => typeof(SusiDnsAddressBookTests).Assembly.GetManifestResourceStream(
        "DN42Atlas.Tests.Fixtures.susidns-export.hosts.txt") ?? throw new AssertFailedException("Missing SusiDNS fixture.");

    private static AddressBookSnapshot Parse(string text)
    {
        using var reader = new StringReader(text);
        return AddressBookParser.Parse(reader, "SusiDNS test import");
    }
}
