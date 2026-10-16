# Private I2P seed snapshots

Create a seed input artifact from an explicitly selected local addressbook:

```text
dotnet run --project DN42Atlas -- import-seeds path/to/hosts.txt private/i2p-seeds.json
```

Both paths are required; quote paths containing spaces. The output directory must
already exist. Relative paths resolve against the current working directory.
URLs, UNC/device paths and identical normalized input/output paths are rejected.
Use ordinary local paths without aliases into the input file or a served directory.
Exit codes are 0 for success, 2 for wrong argument count/empty paths, and 1 for
invalid input, missing files, unsupported paths or output/I/O failures. Success
reports only the seed count and output path. Errors go to stderr and include the
parser's source/line diagnostics where applicable, without destination contents.

`SeedSnapshotImporter.ImportAsync` uses the existing
[AddressBookParser](../AddressBook/README.md) and retains all of its validation,
deduplication, conflict and bounded-input rules. The CLI dispatches before DN42
exclusion policy, operation locking, registry or scanner construction. Import
performs only local file transformation: no DNS, HTTP, sockets, proxy discovery,
router access or registry Git operations. The DN42 scanner does not consume this
artifact yet. This is an export boundary; loading/version-validation for a future
scanner belongs to a later slice.

## Version 1 format

The immutable `I2pSeedSnapshot` is serialized as UTF-8 JSON, without a BOM, with
camel-case field names, fixed property order and indented formatting:

```json
{
  "schema": "i2patlas.seed-snapshot",
  "version": 1,
  "importedAtUtc": "2026-10-16T01:00:00+00:00",
  "source": {
    "path": "/absolute/private/hosts.txt",
    "sha256": "<64 uppercase hexadecimal characters>",
    "byteLength": 1234
  },
  "seeds": [
    { "hostname": "example.i2p", "destination": "<preserved validated destination>" }
  ]
}
```

The import timestamp comes from `TimeProvider` after successful parsing and is
normalized to UTC. Tests can supply a fixed clock. Seeds are sorted by normalized
hostname with `StringComparer.Ordinal`, independent of input order or culture.
Destination case, I2P Base64 alphabet and padding remain unchanged except for the
existing parser's permitted surrounding spaces/tabs. Empty books have `seeds: []`.
No DN42 resolution status, IP address array or registry model is used.

The SHA-256 is calculated while parsing the same open stream, not by reopening a
path after parsing or hashing reserialized text. It includes the exact original
bytes, including UTF-8 BOM, comments, duplicates, whitespace and CR/LF choices.
`byteLength` records the bytes consumed. The fully qualified source path describes
where the file was opened; it is diagnostic provenance, not a stable file identity
or a promise the path still contains those bytes. File sharing denies ordinary
writers during the read on supporting platforms. Independently of sharing, hash
and parser always consume the same stream. Reordering or editing source bytes
changes the hash even if the normalized seeds are equivalent. With the same
source path/bytes and fixed clock, repeated artifact bytes are deterministic.

## Private state and replacement

**The entire snapshot is private input state.** In particular, destination strings,
hostname-to-destination mappings, local source paths, source hashes/byte lengths
and import timestamps must never be copied wholesale into public JSON or HTML.
These destinations are public-key identities, not private cryptographic keys,
but belong to private scan provenance here. A future public view must use a
separate explicit projection; a friendly hostname may be selected under that
view's publication/exclusion policy. Keep this artifact outside all served roots.

After a complete successful import, JSON is written to a unique private temporary
file in the output directory, flushed to disk, closed, then moved over the output.
On Unix, the shared `PrivateFile` helper creates it with owner read/write (0600);
Windows uses the directory's inherited ACLs. Parser, conflict, encoding, missing
input/directory, serialization, cancellation and replacement failures do not
truncate an existing output. Owned temporary files are removed on normal failure.
A process crash may leave a temporary file; individual same-filesystem rename is
the safety boundary, not a power-loss durability guarantee or multi-file transaction.
No output directory is created implicitly and no public artifact is generated.
