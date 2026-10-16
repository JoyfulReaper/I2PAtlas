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
pair deduplication and bounded-input rules. The CLI dispatches before DN42
exclusion policy, operation locking, registry or scanner construction. Import
performs only local file transformation: no DNS, HTTP, sockets, proxy discovery,
router access or registry Git operations. The DN42 scanner does not consume this
artifact yet. `SeedSnapshotReader.ReadAsync(path, cancellationToken)` provides the
local validated loading boundary for the isolated diagnostic and later orchestration.

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
hostname then preserved destination text with `StringComparer.Ordinal`, independent
of input order or culture. Version 1's seed array permits multiple mappings for
one hostname; identity is the hostname/destination pair. Each pair becomes an
independent scan seed. Exact pairs deduplicate. Existing single-destination files
retain their meaning; the JSON shape and schema version are unchanged.
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
Windows uses the directory's inherited ACLs. Parser, encoding, missing
input/directory, serialization, cancellation and replacement failures do not
truncate an existing output. Owned temporary files are removed on normal failure.
A process crash may leave a temporary file; individual same-filesystem rename is
the safety boundary, not a power-loss durability guarantee or multi-file transaction.
No output directory is created implicitly and no public artifact is generated.

## Validated local loading

The reader accepts only explicit local file paths, rejecting URLs, UNC and device
paths before opening. JSON input is capped at 32 MiB, enough for the importer's
16 MiB addressbook limit plus JSON framing, with maximum nesting depth 8. The
bound is checked against both the initial file length and bytes actually read.
It requires the exact, case-sensitive schema and integer version 1, the five
version-1 root fields, all three source fields and both fields for every seed.
Duplicate JSON properties, unknown fields, wrong types and missing/null fields
are rejected. Timestamps must explicitly use UTC (`Z` or `+00:00`); source paths
must be nonempty, at most 1024 characters and free of controls; hashes must be 64
hexadecimal characters; source byte lengths must be nonnegative 64-bit integers.

Every seed is reconstructed through `I2pSeed`, preserving its hostname and
destination validation/normalization rules. Identical normalized hostname/destination
pairs are deduplicated; alternate destinations for one hostname are retained. The array is
capped at 10000 entries before deduplication, and accepted seeds are sorted
by hostname then destination text ordinally in an immutable snapshot. Empty seed arrays are valid. Validation
errors identify fields or entry indices without echoing private input values.

Source provenance is validated as metadata; the reader does not reopen the
recorded source path, rehash its contents, or authenticate a user-edited snapshot.
No network access, DNS, proxy discovery, router or registry operation occurs in
the reader. The [single-seed diagnostic](../I2pProbing/README.md#local-proxy-diagnostic)
uses this reader and requires an explicit existing seed and loopback proxy.

`ReadWithIdentityAsync` returns the same validated snapshot plus SHA-256 and byte
length of its exact loaded JSON bytes. It hashes the already validated buffer,
without reopening a path. The [private I2P scan](../I2pScanning/README.md) uses this
identity; it differs from `source.sha256`, which fingerprints the earlier
addressbook input. Neither hash authenticates a user-edited file.
