using System.Collections.Immutable;

namespace DN42Atlas.AddressBook;

/// <summary>A complete successful import; source is a local path or caller-supplied import label.</summary>
public sealed class AddressBookSnapshot
{
    public string Source { get; }
    public ImmutableArray<I2pSeed> Seeds { get; }
    public ImmutableArray<AddressBookEntryMetadata> EntryMetadata { get; }

    internal AddressBookSnapshot(string source, ImmutableArray<I2pSeed> seeds,
        ImmutableArray<AddressBookEntryMetadata> entryMetadata)
    {
        Source = source;
        Seeds = seeds;
        EntryMetadata = entryMetadata;
    }
}

/// <summary>Unverified, private import annotations per physical entry, never part of seed identity.</summary>
public sealed record AddressBookEntryMetadata(int LineNumber, string Hostname,
    ImmutableSortedDictionary<string, string> Properties);
