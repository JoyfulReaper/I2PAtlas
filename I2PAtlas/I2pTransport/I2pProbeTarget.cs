using I2PAtlas.AddressBook;

namespace I2PAtlas.I2pTransport;

internal enum I2pDiscoveredHostForm { Friendly, ClassicBase32 }

// The private constructor closes the hierarchy to these two nested variants.
// Network targets carry no crawl depth, referring URL or import provenance.
internal abstract class I2pProbeTarget
{
    private I2pProbeTarget() { }
    internal abstract string RequestHostname { get; }

    internal sealed class KnownSeed : I2pProbeTarget
    {
        internal I2pSeed Seed { get; }
        internal string DestinationHostname { get; }
        internal override string RequestHostname => DestinationHostname;

        internal KnownSeed(I2pSeed seed)
        {
            ArgumentNullException.ThrowIfNull(seed);
            Seed = seed;
            DestinationHostname = I2pSeedIdentity.FromSeed(seed).DestinationHostname;
        }
    }

    // A hostname-only routing reference, never an inferred destination identity.
    internal sealed class DiscoveredHost : I2pProbeTarget
    {
        internal string Hostname { get; }
        internal I2pDiscoveredHostForm HostForm { get; }
        internal override string RequestHostname => Hostname;

        internal DiscoveredHost(string hostname)
        {
            var normalized = I2pSeed.NormalizeHostname(hostname);
            foreach (var reserved in new[] { "proxy.i2p", "router.i2p", "console.i2p" })
                if (normalized == reserved || normalized.EndsWith("." + reserved, StringComparison.Ordinal))
                    throw new FormatException("Proxy/router-control hostnames are not probe targets.");

            if (normalized == "b32.i2p")
                throw new FormatException("A classic Base32 target requires a destination-hash label.");
            HostForm = I2pDiscoveredHostForm.Friendly;
            if (normalized.EndsWith(".b32.i2p", StringComparison.Ordinal))
            {
                var label = normalized[..^8];
                if (!label.Contains('.') && label.Length >= 56)
                    throw new FormatException("Extended/encrypted-lease-set Base32 targets are unsupported.");
                if (label.Length != 52 || !label.All(c => c is >= 'a' and <= 'z' or >= '2' and <= '7') ||
                    label[51] is not ('a' or 'q'))
                    throw new FormatException("Classic Base32 targets require 52 canonical Base32 characters.");
                HostForm = I2pDiscoveredHostForm.ClassicBase32;
            }
            Hostname = normalized;
        }
    }
}
