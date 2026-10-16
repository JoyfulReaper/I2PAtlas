using System.Collections.Immutable;
using System.Text;

namespace DN42Atlas.AddressBook;

/// <summary>Bounded, all-or-nothing import of local hostname=destination addressbooks.</summary>
public static class AddressBookParser
{
    public const int MaxInputCharacters = 16 * 1024 * 1024;
    public const int MaxLineCharacters = 16 * 1024;
    public const int MaxDestinationCharacters = 8 * 1024;
    public const int MaxSeeds = 10_000;
    public const int MaxSourceCharacters = 1024;

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
        var byHost = new Dictionary<string, I2pSeed>(StringComparer.Ordinal);
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
        return new AddressBookSnapshot(source, seeds.ToImmutable());

        AddressBookParseException Error(string reason) => new(source, lineNumber, reason);

        void AddLine()
        {
            var line = buffer.AsSpan(0, length).Trim(" \t");
            if (line.IsEmpty || line[0] is '#' or ';') return;
            var equals = line.IndexOf('=');
            if (equals < 0) throw Error("Expected hostname=destination.");

            I2pSeed seed;
            try { seed = new I2pSeed(line[..equals].ToString(), line[(equals + 1)..].ToString()); }
            catch (FormatException ex) { throw Error(ex.Message); }

            if (byHost.TryGetValue(seed.Hostname, out var previous))
            {
                if (!string.Equals(previous.Destination, seed.Destination, StringComparison.Ordinal))
                    throw Error($"Conflicting destinations for {seed.Hostname}.");
                return;
            }
            if (seeds.Count == MaxSeeds) throw Error($"Addressbook exceeds {MaxSeeds} unique seeds.");
            byHost.Add(seed.Hostname, seed);
            seeds.Add(seed);
        }
    }

    private static void ValidateSource(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (source.Length > MaxSourceCharacters || source.Any(char.IsControl))
            throw new ArgumentException($"Source must have at most {MaxSourceCharacters} characters and no controls.", nameof(source));
    }
}
