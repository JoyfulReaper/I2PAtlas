using System.Globalization;
using System.Text;
using I2PAtlas.AddressBook;

namespace I2PAtlas.Tests;

[TestClass]
public sealed class AddressBookTests
{
    private static readonly string Destination = MakeDestination();

    [TestMethod]
    public void ValidEntryHasImmutableMappingAndSourceProvenance()
    {
        using var reader = new StringReader($"example.i2p={Destination}");
        var snapshot = AddressBookParser.Parse(reader, "manual import");
        Assert.AreEqual("manual import", snapshot.Source);
        Assert.HasCount(1, snapshot.Seeds);
        Assert.AreEqual(new I2pSeed("example.i2p", Destination), snapshot.Seeds[0]);
        var changedCopy = snapshot.Seeds.SetItem(0, new I2pSeed("other.i2p", Destination));
        Assert.AreEqual("example.i2p", snapshot.Seeds[0].Hostname);
        Assert.AreEqual("other.i2p", changedCopy[0].Hostname);
    }

    [TestMethod]
    [DataRow("EXAMPLE.I2P")]
    [DataRow("Example.i2p.")]
    [DataRow(" \tExAmPlE.I2p. \t")]
    public void HostnameNormalizationIsConsistentInModelAndParser(string hostname)
    {
        Assert.AreEqual("example.i2p", new I2pSeed(hostname, Destination).Hostname);
        Assert.AreEqual("example.i2p", Parse($"{hostname}={Destination}").Seeds[0].Hostname);
    }

    [TestMethod]
    public void NormalizationIsCultureIndependent()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            Assert.AreEqual("initial.i2p", new I2pSeed("INITIAL.I2P", Destination).Hostname);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [TestMethod]
    [DataRow("\n")]
    [DataRow("\r\n")]
    [DataRow("\r")]
    public void BlankCommentsWhitespaceAndLineEndingsAreSupported(string newline)
    {
        var text = string.Join(newline, "\uFEFF# book", "", " \t ", " \t; note", " # comment=x",
            $" \tExample.I2P. \t= \t{Destination}\t ", "");
        Assert.AreEqual(new I2pSeed("example.i2p", Destination), Parse(text).Seeds.Single());
        Assert.IsEmpty(Parse(" \t# comment\n; another\n\n").Seeds);
        Assert.IsEmpty(Parse("").Seeds);
    }

    [TestMethod]
    public void IdenticalDuplicatesAreDeduplicatedAndHostnameOrderIsDeterministic()
    {
        var snapshot = Parse($"z.i2p={Destination}\nZ.I2P. = {Destination}\n" +
            $"a.i2p={Destination}\nz.i2p={Destination}");
        CollectionAssert.AreEqual(new[] { "a.i2p", "z.i2p" }, snapshot.Seeds.Select(s => s.Hostname).ToArray());
        Assert.AreEqual(snapshot.Seeds[0].Destination, snapshot.Seeds[1].Destination);
    }

    [TestMethod]
    public void SameHostnameWithDistinctDestinationsKeepsBothPairsInStableOrder()
    {
        var other = MakeDestination(salt: 1);
        var snapshot = Parse($"# source\r\nexample.i2p={Destination}\r\nEXAMPLE.I2P.={other}\r\n");
        var reversed = Parse($"example.i2p={other}\nexample.i2p={Destination}");
        Assert.HasCount(2, snapshot.Seeds);
        Assert.IsTrue(snapshot.Seeds.All(s => s.Hostname == "example.i2p"));
        CollectionAssert.AreEqual(snapshot.Seeds.ToArray(), reversed.Seeds.ToArray());
        CollectionAssert.AreEqual(new[] { Destination, other }.Order(StringComparer.Ordinal).ToArray(),
            snapshot.Seeds.Select(s => s.Destination).ToArray());
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(" \t")]
    [DataRow("i2p")]
    [DataRow(".i2p")]
    [DataRow("example.com")]
    [DataRow("example.dn42")]
    [DataRow("example.i2p.com")]
    [DataRow("example.i2p.alt")]
    [DataRow("127.0.0.1")]
    [DataRow("::1")]
    [DataRow("[fd42::1]")]
    [DataRow("http://example.i2p")]
    [DataRow("https://example.i2p/")]
    [DataRow("//example.i2p")]
    [DataRow("example.i2p:80")]
    [DataRow("user@example.i2p")]
    [DataRow("example.i2p/path")]
    [DataRow("example.i2p?query")]
    [DataRow("example.i2p#fragment")]
    [DataRow("example.i2p\\path")]
    [DataRow("bad_name.i2p")]
    [DataRow("bad name.i2p")]
    [DataRow("bad\tname.i2p")]
    [DataRow("bad\0name.i2p")]
    [DataRow("*.example.i2p")]
    [DataRow("-bad.i2p")]
    [DataRow("bad-.i2p")]
    [DataRow("bad..i2p")]
    [DataRow(".bad.i2p")]
    [DataRow("bad.i2p..")]
    [DataRow("café.i2p")]
    [DataRow("bad。i2p")]
    [DataRow("bad.i2p\u00a0")]
    public void InvalidHostnamesAreRejectedThroughBothEntryPoints(string hostname)
    {
        Assert.ThrowsExactly<FormatException>(() => new I2pSeed(hostname, Destination));
        Assert.ThrowsExactly<AddressBookParseException>(() => Parse($"{hostname}={Destination}"));
    }

    [TestMethod]
    public void HostnameLabelAndTotalLengthBoundariesAreEnforced()
    {
        var longest = string.Join('.', new string('a', 63), new string('b', 63), new string('c', 63), new string('d', 57), "i2p");
        Assert.AreEqual(253, longest.Length);
        Assert.AreEqual(longest, new I2pSeed(longest.ToUpperInvariant() + ".", Destination).Hostname);
        Assert.ThrowsExactly<FormatException>(() => new I2pSeed("x" + longest, Destination));
        Assert.ThrowsExactly<FormatException>(() => new I2pSeed(new string('a', 64) + ".i2p", Destination));
        Assert.AreEqual("xn--bcher-kva.i2p", new I2pSeed("XN--BCHER-KVA.I2P", Destination).Hostname);
        Assert.AreEqual("sub.example.i2p", new I2pSeed("sub.example.i2p", Destination).Hostname);
    }

    [TestMethod]
    [DataRow("missing separator")]
    [DataRow("example.i2p")]
    [DataRow("=value")]
    [DataRow("example.i2p=")]
    [DataRow("example.i2p= \t")]
    [DataRow("example.i2p: value")]
    [DataRow("[addressbook]")]
    public void MalformedLinesAndMissingFieldsFailExplicitly(string line)
    {
        var error = Assert.ThrowsExactly<AddressBookParseException>(() => Parse($"# comment\n{line}"));
        Assert.AreEqual(2, error.LineNumber);
    }

    [TestMethod]
    public void InlineCommentsAndExtraSeparatorsAreRejected()
    {
        foreach (var suffix in new[] { " # comment", " ; comment", "=extra", "\\" })
            Assert.ThrowsExactly<AddressBookParseException>(() => Parse($"example.i2p={Destination}{suffix}"));
        Assert.ThrowsExactly<AddressBookParseException>(() => Parse($"\n\uFEFFexample.i2p={Destination}"));
        Assert.ThrowsExactly<FormatException>(() => new I2pSeed("example.i2p", "\n" + Destination));
    }

    [TestMethod]
    [DataRow(0, 0)]
    [DataRow(5, 4)]
    [DataRow(1, 1)]
    [DataRow(1, 2)]
    [DataRow(1, 300)]
    public void VariableLengthDestinationsKeepExactCaseAlphabetAndPadding(int type, int payloadLength)
    {
        var destination = MakeDestination((byte)type, payloadLength);
        Assert.Contains("-", destination);
        Assert.Contains("~", destination);
        Assert.AreNotEqual(destination.ToLowerInvariant(), destination);
        Assert.AreEqual(destination, Parse($"example.i2p= \t{destination}\t ").Seeds[0].Destination);
        Assert.AreSame(destination, new I2pSeed("example.i2p", destination).Destination);
        var unpadded = destination.TrimEnd('=');
        Assert.AreEqual(unpadded, Parse($"example.i2p={unpadded}").Seeds[0].Destination);
    }

    [TestMethod]
    public void DestinationTextComparisonDoesNotCanonicalizePaddingOrCase()
    {
        var padded = MakeDestination(5, 4);
        Assert.IsTrue(padded.EndsWith("==", StringComparison.Ordinal));
        var spellings = Parse($"example.i2p={padded}\nexample.i2p={padded.TrimEnd('=')}");
        CollectionAssert.AreEquivalent(new[] { padded, padded.TrimEnd('=') }, spellings.Seeds.Select(s => s.Destination).ToArray());
        var caseChanged = (Destination[0] == 'a' ? "A" : "a") + Destination[1..];
        Assert.AreEqual(caseChanged, new I2pSeed("example.i2p", caseChanged).Destination);
        var distinct = Parse($"example.i2p={Destination}\nexample.i2p={caseChanged}");
        CollectionAssert.AreEquivalent(new[] { Destination, caseChanged }, distinct.Seeds.Select(s => s.Destination).ToArray());
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(" \t")]
    [DataRow("destination")]
    [DataRow("127.0.0.1")]
    [DataRow("fd42::1")]
    [DataRow("example.b32.i2p")]
    [DataRow("https://example.i2p/")]
    public void NonDestinationsAreRejected(string destination)
    {
        Assert.ThrowsExactly<FormatException>(() => new I2pSeed("example.i2p", destination));
        Assert.ThrowsExactly<AddressBookParseException>(() => Parse($"example.i2p={destination}"));
    }

    [TestMethod]
    public void InvalidAlphabetWhitespacePaddingAndTruncatedEnvelopesAreRejected()
    {
        var padded = MakeDestination(5, 4);
        var badPadBits = padded[..^3] + "B==";
        foreach (var destination in new[] { "+" + Destination[1..], "/" + Destination[1..],
            "_" + Destination[1..], "é" + Destination[1..], "A=" + Destination[2..],
            Destination[..100] + " " + Destination[100..], Destination[..100] + "\t" + Destination[100..],
            Destination + "=", Destination + "===", Destination + "A", Destination[..^1],
            padded[..^1], badPadBits, padded[..^4] + "AAAA", MakeDestination(5, 3), MakeDestination(0, 1) })
        {
            Assert.ThrowsExactly<FormatException>(() => new I2pSeed("example.i2p", destination));
            Assert.ThrowsExactly<AddressBookParseException>(() => Parse($"example.i2p={destination}"));
        }
    }

    [TestMethod]
    public void DestinationLimitIsInclusiveAndDoesNotAssumeAnEncodedLength()
    {
        var maximum = MakeDestination(1, AddressBookParser.MaxDestinationCharacters / 4 * 3 - 387);
        Assert.AreEqual(AddressBookParser.MaxDestinationCharacters, maximum.Length);
        Assert.AreEqual(maximum, Parse($"example.i2p= {maximum} ").Seeds[0].Destination);
        Assert.ThrowsExactly<FormatException>(() => new I2pSeed("example.i2p", maximum + "AAAA"));
        var error = Assert.ThrowsExactly<AddressBookParseException>(() => Parse($"example.i2p={maximum}AAAA"));
        Assert.Contains("Destination", error.Message);
    }

    [TestMethod]
    public void LineLimitAppliesBeforeReadingWholeLinesEvenToCommentsAndWhitespace()
    {
        Assert.IsEmpty(Parse("#" + new string('x', AddressBookParser.MaxLineCharacters - 1)).Seeds);
        foreach (var pattern in new[] { "#", " " })
        {
            using var reader = new RepeatingReader(pattern, long.MaxValue);
            var error = Assert.ThrowsExactly<AddressBookParseException>(() => AddressBookParser.Parse(reader, "large line"));
            Assert.Contains("Line exceeds", error.Message);
            Assert.AreEqual(AddressBookParser.MaxLineCharacters + 1L, reader.CharactersRead);
        }
        var validLine = $"example.i2p={Destination}";
        Assert.HasCount(1, Parse(validLine.PadRight(AddressBookParser.MaxLineCharacters)).Seeds);
        Assert.ThrowsExactly<AddressBookParseException>(() => Parse(validLine.PadRight(AddressBookParser.MaxLineCharacters + 1)));
    }

    [TestMethod]
    public void TotalInputLimitIsInclusiveAndIncludesCommentsBlankLinesAndDuplicates()
    {
        using var exact = new RepeatingReader("#\n", AddressBookParser.MaxInputCharacters);
        Assert.IsEmpty(AddressBookParser.Parse(exact, "exact").Seeds);
        using var oversized = new RepeatingReader("\n", long.MaxValue);
        var error = Assert.ThrowsExactly<AddressBookParseException>(() => AddressBookParser.Parse(oversized, "oversized"));
        Assert.Contains("Input exceeds", error.Message);
        Assert.AreEqual(AddressBookParser.MaxInputCharacters + 1L, oversized.CharactersRead);
        using var duplicates = new RepeatingReader($"example.i2p={Destination}\n", long.MaxValue);
        Assert.ThrowsExactly<AddressBookParseException>(() => AddressBookParser.Parse(duplicates, "duplicates"));
        Assert.AreEqual(AddressBookParser.MaxInputCharacters + 1L, duplicates.CharactersRead);
    }

    [TestMethod]
    public void SeedLimitCountsUniquePairsAndRejectsOneMoreEvenForAnExistingHostname()
    {
        var lines = string.Join('\n', Enumerable.Range(0, AddressBookParser.MaxSeeds)
            .Select(i => $"host{i}.i2p={Destination}"));
        Assert.HasCount(AddressBookParser.MaxSeeds, Parse(lines + $"\nHOST0.I2P.={Destination}").Seeds);
        var error = Assert.ThrowsExactly<AddressBookParseException>(() => Parse(lines + $"\nhost0.i2p={MakeDestination(salt: 1)}"));
        Assert.AreEqual(AddressBookParser.MaxSeeds + 1, error.LineNumber);
        Assert.Contains("unique seeds", error.Message);
    }

    [TestMethod]
    public void LocalFileUsesStrictUtf8AndAbsolutePathProvenance()
    {
        using var files = new TemporaryFiles();
        var path = files.Write("hosts.txt", $"example.i2p={Destination}\n");
        Assert.AreEqual(Path.GetFullPath(path), AddressBookParser.ParseFile(path).Source);
        File.WriteAllText(path, $"example.i2p={Destination}", new UTF8Encoding(true));
        Assert.HasCount(1, AddressBookParser.ParseFile(path).Seeds);
        File.WriteAllBytes(path, [0xff, 0xfe, 0xfd]);
        Assert.ThrowsExactly<DecoderFallbackException>(() => AddressBookParser.ParseFile(path));
        File.Delete(path); // File handles are closed on success and failure.
        Assert.ThrowsExactly<FileNotFoundException>(() => AddressBookParser.ParseFile(path));
    }

    [TestMethod]
    public void ReaderOwnershipAndIoFailuresArePreserved()
    {
        using var reader = new StringReader($"example.i2p={Destination}");
        AddressBookParser.Parse(reader, "source");
        Assert.AreEqual(-1, reader.Read());
        using var invalid = new StringReader("bad line\nremaining");
        Assert.ThrowsExactly<AddressBookParseException>(() => AddressBookParser.Parse(invalid, "source"));
        Assert.AreEqual('r', (char)invalid.Read());
        using var broken = new BrokenReader();
        Assert.ThrowsExactly<IOException>(() => AddressBookParser.Parse(broken, "source"));
    }

    [TestMethod]
    public void ApiArgumentsAndSourceLabelsAreValidated()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new I2pSeed(null!, Destination));
        Assert.ThrowsExactly<ArgumentNullException>(() => new I2pSeed("example.i2p", null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => AddressBookParser.Parse(null!, "source"));
        using var reader = new StringReader("");
        foreach (var source in new[] { "", " ", "bad\nsource", new string('x', AddressBookParser.MaxSourceCharacters + 1) })
            Assert.ThrowsExactly<ArgumentException>(() => AddressBookParser.Parse(reader, source));
        Assert.AreEqual(AddressBookParser.MaxSourceCharacters,
            AddressBookParser.Parse(reader, new string('x', AddressBookParser.MaxSourceCharacters)).Source.Length);
    }

    private static AddressBookSnapshot Parse(string text)
    {
        using var reader = new StringReader(text);
        return AddressBookParser.Parse(reader, "test addressbook");
    }

    // Deterministic, structurally valid synthetic destinations; no router or private keys.
    private static string MakeDestination(byte type = 0, int payloadLength = 0, int salt = 0)
    {
        var bytes = new byte[387 + payloadLength];
        for (var i = 0; i < 384; i++) bytes[i] = (byte)(i * 73 + salt);
        bytes[384] = type;
        bytes[385] = (byte)(payloadLength >> 8);
        bytes[386] = (byte)payloadLength;
        if (type == 5 && payloadLength >= 4) bytes[388] = 7; // Ed25519 signing, ElGamal crypto.
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '~');
    }

    private sealed class RepeatingReader(string pattern, long characterCount) : TextReader
    {
        public long CharactersRead { get; private set; }
        public override int Read() => CharactersRead < characterCount
            ? pattern[(int)(CharactersRead++ % pattern.Length)] : -1;
        public override string? ReadLine() => throw new AssertFailedException("Must not allocate unbounded lines.");
    }

    private sealed class BrokenReader : TextReader
    {
        public override int Read() => throw new IOException("read failed");
    }
}
