using System.Collections.Immutable;

namespace DN42Atlas.AddressBook;

/// <summary>A complete successful import; source is a local path or caller-supplied import label.</summary>
public sealed class AddressBookSnapshot
{
    public string Source { get; }
    public ImmutableArray<I2pSeed> Seeds { get; }

    internal AddressBookSnapshot(string source, ImmutableArray<I2pSeed> seeds)
    {
        Source = source;
        Seeds = seeds;
    }
}
