using System.Collections.Immutable;
using System.Text;

namespace I2PAtlas.AddressBook;

/// <summary>Bounded, all-or-nothing import of local hostname=destination addressbooks.</summary>
public static class AddressBookParser
{
    public const int MaxInputCharacters = 16 * 1024 * 1024;
    public const int MaxLineCharacters = 16 * 1024;
    public const int MaxDestinationCharacters = 8 * 1024;
    public const int MaxSeeds = 10_000;
    public const int MaxSourceCharacters = 1024;
    public const int MaxMetadataCharacters = 4096; // suffix after #!, including separators
    public const int MaxMetadataProperties = 16;
    public const int MaxMetadataKeyCharacters = 64;
    public const int MaxMetadataValueCharacters = 2048;

    public static AddressBookSnapshot ParseFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var source = Path.GetFullPath(path);
        ValidateSource(source);
        using var reader = new StreamReader(source, new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: false);
        return Parse(reader, source);
    }

    /// <summary>Leaves the caller-owned reader open. No partial snapshot escapes on failure.</summary>
    public static AddressBookSnapshot Parse(TextReader reader, string source)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ValidateSource(source);
        var seeds = ImmutableArray.CreateBuilder<I2pSeed>();
        var entryMetadata = ImmutableArray.CreateBuilder<AddressBookEntryMetadata>();
        var uniqueSeeds = new HashSet<(string Hostname, string DestinationHostname)>();
        var buffer = new char[MaxLineCharacters];
        var length = 0;
        var total = 0;
        var lineNumber = 1;
        var previousCr = false;

        int next;
        while ((next = reader.Read()) != -1)
        {
            if (++total > MaxInputCharacters)
                throw Error($"Input exceeds {MaxInputCharacters} characters.");
            var c = (char)next;
            if (total == 1 && c == '\uFEFF') continue;
            if (c == '\n' && previousCr)
            {
                previousCr = false;
                continue;
            }
            previousCr = c == '\r';
            if (c is '\r' or '\n')
            {
                AddLine();
                length = 0;
                lineNumber++;
                continue;
            }
            if (length == MaxLineCharacters)
                throw Error($"Line exceeds {MaxLineCharacters} characters.");
            buffer[length++] = c;
        }
        if (length > 0) AddLine();
        return new AddressBookSnapshot(source, seeds.OrderBy(seed => seed.Hostname, StringComparer.Ordinal)
            .ThenBy(seed => seed.Destination, StringComparer.Ordinal).ToImmutableArray(), entryMetadata.ToImmutable());

        AddressBookParseException Error(string reason) => new(source, lineNumber, reason);

        void AddLine()
        {
            var line = buffer.AsSpan(0, length).Trim(" \t");
            if (line.IsEmpty || line[0] is '#' or ';') return;
            var equals = line.IndexOf('=');
            if (equals < 0) throw Error("Expected hostname=destination.");

            var destination = line[(equals + 1)..];
            var delimiter = destination.IndexOf("#!", StringComparison.Ordinal);
            var metadata = ImmutableSortedDictionary<string, string>.Empty;
            if (delimiter >= 0)
            {
                // The first exact delimiter is authoritative: never skip a malformed
                // block to find a later one, and never strip arbitrary # comments.
                metadata = ParseMetadata(destination[(delimiter + 2)..]);
                destination = destination[..delimiter];
            }

            I2pSeed seed;
            try { seed = new I2pSeed(line[..equals].ToString(), destination.ToString()); }
            catch (FormatException ex) { throw Error(ex.Message); }

            if (!metadata.IsEmpty)
                entryMetadata.Add(new(lineNumber, seed.Hostname, seed.Destination, metadata));

            // Effective addressbooks can contain alternate destinations for one name.
            // Equivalent Base64 spellings share identity; retain the first source spelling.
            var identity = I2pSeedIdentity.FromSeed(seed);
            if (uniqueSeeds.Contains(identity)) return;
            if (seeds.Count == MaxSeeds) throw Error($"Addressbook exceeds {MaxSeeds} unique seeds.");
            uniqueSeeds.Add(identity);
            seeds.Add(seed);
        }

        ImmutableSortedDictionary<string, string> ParseMetadata(ReadOnlySpan<char> suffix)
        {
            if (suffix.IsEmpty || suffix.Length > MaxMetadataCharacters)
                throw Error($"Metadata must contain 1 to {MaxMetadataCharacters} characters after #!.");
            var properties = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            foreach (var range in suffix.Split('#'))
            {
                if (properties.Count == MaxMetadataProperties)
                    throw Error($"Metadata exceeds {MaxMetadataProperties} properties.");
                var pair = suffix[range];
                var equals = pair.IndexOf('=');
                if (equals <= 0 || equals == pair.Length - 1)
                    throw Error("Metadata requires nonempty key=value pairs separated by #.");
                var key = pair[..equals];
                var value = pair[(equals + 1)..];
                if (key.Length > MaxMetadataKeyCharacters || !char.IsAsciiLetter(key[0]))
                    throw Error("Metadata key must start with an ASCII letter and fit the key limit.");
                foreach (var c in key)
                    if (!char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-' and not '.')
                        throw Error("Metadata key contains an invalid character.");
                if (value.Length > MaxMetadataValueCharacters || value.IsWhiteSpace())
                    throw Error($"Metadata value must be nonblank and at most {MaxMetadataValueCharacters} characters.");
                foreach (var c in value)
                    if (c is < ' ' or > '~')
                        throw Error("Metadata values must contain printable ASCII characters.");
                // All properties, including action/olddest, are inert export provenance.
                if (!properties.TryAdd(key.ToString(), value.ToString()))
                    throw Error("Duplicate metadata key.");
            }
            return properties.ToImmutable();
        }
    }

    private static void ValidateSource(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (source.Length > MaxSourceCharacters || source.Any(char.IsControl))
            throw new ArgumentException($"Source must have at most {MaxSourceCharacters} characters and no controls.", nameof(source));
    }
}
