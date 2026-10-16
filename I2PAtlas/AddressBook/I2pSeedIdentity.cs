using System.Security.Cryptography;
using System.Text;

namespace I2PAtlas.AddressBook;

/// <summary>Semantic identity, independent of a validated seed's private Base64 spelling.</summary>
public static class I2pSeedIdentity
{
    public static (string Hostname, string DestinationHostname) FromSeed(I2pSeed seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        // Decode a temporary copy solely to hash the binary destination. The validated
        // seed text (including its original case, alphabet and optional padding) is untouched.
        var encoded = seed.Destination.Replace('-', '+').Replace('~', '/');
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
        return (seed.Hostname, result + ".b32.i2p");
    }
}
