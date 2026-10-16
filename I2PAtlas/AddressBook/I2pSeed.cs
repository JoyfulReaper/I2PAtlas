namespace I2PAtlas.AddressBook;

/// <summary>A validated local mapping with preserved private destination text; see <see cref="I2pSeedIdentity"/> for semantic identity.</summary>
public sealed record I2pSeed
{
    public string Hostname { get; }
    public string Destination { get; }

    public I2pSeed(string hostname, string destination)
    {
        Hostname = NormalizeHostname(hostname);
        Destination = ValidateDestination(destination);
    }

    /// <summary>Shared local hostname validation for seed construction and explicit seed selection.</summary>
    public static string NormalizeHostname(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var name = value.AsSpan().Trim(" \t");
        if (name.EndsWith(".")) name = name[..^1];
        if (name.Length is < 5 or > 253 ||
            !name.EndsWith(".i2p", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("Hostname must be a nonempty .i2p name of at most 253 characters.");

        // ASCII LDH labels only. This also rejects URLs, ports, userinfo and IP literals
        // without invoking a resolver, URI canonicalization or IDN conversion.
        foreach (var range in name.Split('.'))
        {
            var label = name[range];
            if (label.Length is < 1 or > 63 || label[0] == '-' || label[^1] == '-')
                throw new FormatException("Hostname contains an invalid label.");
            foreach (var c in label)
                if (!char.IsAsciiLetterOrDigit(c) && c != '-')
                    throw new FormatException("Hostname must contain only ASCII letters, digits, hyphens and dots.");
        }

        return name.ToString().ToLowerInvariant();
    }

    private static string ValidateDestination(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var encoded = value.AsSpan().Trim(" \t");
        if (encoded.Length is < 516 or > AddressBookParser.MaxDestinationCharacters)
            throw new FormatException($"Destination must contain 516 to {AddressBookParser.MaxDestinationCharacters} encoded characters.");

        var data = encoded.TrimEnd('=');
        var padding = encoded.Length - data.Length;
        var remainder = data.Length % 4;
        if (padding > 2 || remainder == 1 ||
            (padding != 0 && (encoded.Length % 4 != 0 || padding != 4 - remainder)))
            throw new FormatException("Destination has invalid Base64 padding or length.");
        foreach (var c in data)
            if (Sextet(c) < 0)
                throw new FormatException("Destination must use the I2P Base64 alphabet without internal whitespace.");
        if ((remainder == 2 && (Sextet(data[^1]) & 15) != 0) ||
            (remainder == 3 && (Sextet(data[^1]) & 3) != 0))
            throw new FormatException("Destination has nonzero unused Base64 bits.");

        var byteLength = data.Length * 6 / 8;
        if (byteLength < 387)
            throw new FormatException("Destination is shorter than its keys and certificate header.");

        // The first 384 bytes are key material/padding. Inspect only the following
        // four encoded characters: certificate type (8 bits) and length (16 bits).
        // Never decode/re-encode or canonicalize the stored destination string.
        var header = (Sextet(data[512]) << 18) | (Sextet(data[513]) << 12) |
            (Sextet(data[514]) << 6) | Sextet(data[515]);
        var certificateType = header >> 16;
        var certificateLength = header & 0xffff;
        if (byteLength != 387 + certificateLength ||
            (certificateType is 0 or 2 && certificateLength != 0) ||
            (certificateType == 5 && certificateLength < 4))
            throw new FormatException("Destination certificate length does not match its envelope.");

        return encoded.Length == value.Length ? value : encoded.ToString();
    }

    private static int Sextet(char c) => c switch
    {
        >= 'A' and <= 'Z' => c - 'A',
        >= 'a' and <= 'z' => c - 'a' + 26,
        >= '0' and <= '9' => c - '0' + 52,
        '-' => 62,
        '~' => 63,
        _ => -1
    };
}
