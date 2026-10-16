using System.Security.Cryptography;
using System.Text;
using DN42Atlas.AddressBook;

namespace DN42Atlas.I2pTransport;

/// <summary>A GET target bound to one seed destination, with the friendly name retained only as metadata.</summary>
public sealed class I2pHttpRequestTarget
{
    public const int MaxUrlCharacters = 4096;
    public I2pSeed Seed { get; }
    public string FriendlyHostname => Seed.Hostname;
    public string DestinationHostname { get; }
    public Uri RequestUri { get; }

    public I2pHttpRequestTarget(I2pSeed seed, string absoluteUrl)
    {
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(absoluteUrl);
        // No URI repair, backslash authority interpretation, address-helper queries or fragments.
        if (absoluteUrl.Length > MaxUrlCharacters ||
            absoluteUrl.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '\\' or '#' or '?') ||
            !absoluteUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            !Uri.TryCreate(absoluteUrl, UriKind.Absolute, out var requested) ||
            requested.Scheme != "http" || requested.Port != 80 || requested.UserInfo.Length != 0)
            throw Invalid();

        var end = absoluteUrl.IndexOf('/', 7);
        var authority = end < 0 ? absoluteUrl[7..] : absoluteUrl[7..end];
        var host = authority.EndsWith(":80", StringComparison.Ordinal) ? authority[..^3] : authority;
        string normalizedHost;
        try { normalizedHost = new I2pSeed(host, seed.Destination).Hostname; }
        catch (FormatException) { throw Invalid(); }

        DestinationHostname = DeriveHostname(seed.Destination);
        if (normalizedHost != seed.Hostname && normalizedHost != DestinationHostname)
            throw Invalid();

        Seed = seed;
        RequestUri = new Uri($"http://{DestinationHostname}{requested.AbsolutePath}");
    }

    private static string DeriveHostname(string destination)
    {
        // Decode a temporary copy solely to hash the binary destination. The validated
        // seed text (including its original case, alphabet and optional padding) is untouched.
        var encoded = destination.Replace('-', '+').Replace('~', '/');
        encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
        var hash = SHA256.HashData(Convert.FromBase64String(encoded));
        const string alphabet = "abcdefghijklmnopqrstuvwxyz234567";
        var result = new StringBuilder(52);
        var bits = 0;
        var accumulator = 0;
        foreach (var b in hash)
        {
            accumulator = (accumulator << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                result.Append(alphabet[(accumulator >> bits) & 31]);
            }
        }
        if (bits > 0) result.Append(alphabet[(accumulator << (5 - bits)) & 31]);
        return result + ".b32.i2p";
    }

    private static ArgumentException Invalid() => new(
        "Request must be an absolute HTTP URL for this seed's .i2p hostname or destination-derived Base32 hostname, " +
        "using port 80, without userinfo, query or fragment.");
}
