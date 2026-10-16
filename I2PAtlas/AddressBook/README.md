# Local I2P addressbook imports

`AddressBookParser.ParseFile(path)` reads a local UTF-8 file (optional UTF-8 BOM).
`Parse(reader, source)` reads a caller-owned `TextReader` and leaves it open.
Neither API performs DNS, router lookups, HTTP requests or proxy configuration.
This parser supplies validated mappings to the local seed snapshot importer.
For the local CLI import and versioned private artifact boundary, see
[private I2P seed snapshots](../Seeds/README.md).

Each non-comment line must be `hostname.i2p=destination`, optionally followed by
structured SusiDNS export metadata as described below. LF, CRLF and CR line
endings are accepted, including a final line without a newline. Blank lines and
lines whose first non-space/tab character is `#` or `;` are ignored. Only ASCII
spaces and tabs are removed around fields. Split at the first `=`: trailing `=`
characters belong to Base64 padding. Arbitrary inline comments,
continuations and escaped/property-file syntax are not supported and fail
validation when appended to an entry. A leading BOM is accepted only at the
start of input. Invalid UTF-8 fails explicitly.

## Extended SusiDNS export entries

SusiDNS's [export method](https://github.com/i2p/i2p.i2p/blob/master/apps/susidns/src/java/src/i2p/susi/dns/NamingServiceBean.java)
delegates to the naming service. I2P's [HostTxtEntry writer/parser](https://github.com/i2p/i2p.i2p/blob/master/core/java/src/net/i2p/client/naming/HostTxtEntry.java)
uses `name=destination#!key=value#key=value`, splitting each property at its first
`=` and rejecting duplicate case-sensitive keys. The [subscription format](https://www.i2p.net/en/docs/specs/subscription/)
also defines command actions. This importer consumes an effective addressbook export,
not a subscription update stream, and never executes those actions.

After removing ASCII spaces/tabs at the physical line's edges, the supported
entry grammar is:

```text
entry      = hostname "=" destination [ "#!" properties ]
properties = property *( "#" property )
property   = key "=" value
key        = ASCII-letter *( ASCII-letter / ASCII-digit / "_" / "-" / "." )
value      = nonblank printable-ASCII text, excluding "#"
```

The first `=` separates the mapping; the first exact `#!` on its right separates
the destination and metadata. Only the destination prefix is passed to the
unchanged I2pSeed validator, with its existing surrounding-space/tab handling.
There is no recovery to a later `#!` if the first block is malformed. A bare `#`,
`# !`, `##!`, garbage before `#!`, empty block/pair/key/value, trailing separator,
repeated block delimiter, duplicate key or invalid character fails explicitly.
`#` is a property separator only inside a properly introduced metadata block,
never an inline comment delimiter. Additional `=` characters belong to values,
including signature padding. No property escape/quoting syntax is interpreted.

Keys are case-sensitive and limited to 64 characters. Values are limited to
2048 characters from U+0020 through U+007E, excluding `#`, and cannot consist
only of spaces. Spaces inside values are retained; tabs, controls and non-ASCII
metadata values are rejected by this bounded local-import profile. Whole-line
edge spaces/tabs remain padding and are removed before parsing. Each block has
at most 16 properties and at most 4096 characters after `#!`, including all keys,
values and separators. Limits are inclusive and do not replace line/input limits.

`date` and `sig` are retained as opaque strings: dates are not interpreted or
used for expiry, and signatures are not cryptographically verified or treated as
proof. Unknown keys follow the same bounded generic-retention rules. `action`,
`olddest` and `oldsig` follow those same rules: `adddest`, `changedest`, `remove`,
and other syntactically valid action values are inert provenance on an ordinary
hostname/destination mapping. They never add, replace or remove other entries.
Standalone `#!...` records remain ignored whole-line comments, never mutation
commands. A mapping with a missing hostname or destination still fails validation.
Metadata called `hostname`, `dest` or similar never overrides the actual mapping.

`AddressBookSnapshot.EntryMetadata` is a private, immutable array of per-line
annotations (line number, normalized hostname, preserved destination, ordinally sorted immutable property
map). Identical mapping duplicates still deduplicate as before; their annotations
remain separate physical-line records rather than arbitrarily choosing metadata.
Every duplicate's suffix is validated before deduplication. Different annotation
values never change mapping identity. Total input size also bounds
retained annotations, including duplicate entries.

I2pSeed and the version-1 imported seed JSON contain no metadata fields. There is
no current scan need to persist these annotations. The importer still hashes the
exact original export bytes, including metadata, signature text, comments, BOM and
line endings; it does not hash a reconstructed metadata-free addressbook. The
regression fixture uses the observed `8ightball.i2p=...#!date=1790878952#sig=...`
shape with synthetic keys/signature rather than a live router dependency. The
alternate-destination fixture adds the observed `action=adddest`, `olddest`, `date`,
`sig` and `oldsig` shape with two mappings for `example.i2p`.

Hostnames are lowercased invariantly and one optional final dot is removed.
The result must have at least one label before `.i2p`, at most 253 characters
overall, and labels of 1–63 ASCII letters/digits/hyphens without leading/trailing
hyphens. Empty labels, further trailing dots, Unicode, underscores, wildcards,
IP literals, URLs, ports and userinfo are rejected. Already encoded `xn--` labels
are accepted without IDN conversion. These are local hostname syntax rules;
subscription registrars' reserved-name and registration policies are not applied.

Destinations use the I2P Base64 alphabet `A-Z a-z 0-9 - ~`, with optional correct
terminal padding and zero unused bits. Internal whitespace, standard Base64
`+`/`/`, URLs, IPs, short tokens and Base32 aliases are rejected. Validation checks
the variable-length keys/certificate envelope: 384 key/padding bytes, a three-byte
certificate header, and exactly the declared certificate payload. Null/hidden
certificates must have empty payloads; key certificates need at least four bytes.
Other certificate types are accepted if their declared envelope length matches.
This checks encoding and framing, not key algorithms, signatures, ownership or
reachability. It does not decode/re-encode the destination: the supplied string,
case, alphabet and padding are preserved exactly apart from surrounding spaces/tabs.
See the [I2P structure specification](https://i2p.net/en/docs/specs/common-structures/)
and [reference Base64 implementation](https://github.com/i2p/i2p.i2p/blob/master/core/java/src/net/i2p/data/Base64.java).

Semantic seed identity is normalized hostname plus destination-derived B32 hostname,
computed by the neutral `I2pSeedIdentity` helper. Equivalent padded/unpadded spellings
deduplicate, retaining the first valid source spelling without canonicalizing stored
text. One hostname may have multiple binary destinations; different friendly names
may share a destination. Record equality still compares preserved text; callers
use the explicit identity helper for semantic comparisons. Successful snapshots
sort by hostname then retained destination text, both ordinally. Snapshots contain
immutable seeds and a source label (the absolute path for file
imports). Source plus the preserved destination provide minimal import provenance
for seed snapshots; scan evidence has a separate schema.
Errors carry a one-based line number and source label and never echo destinations.
Malformed entries, missing fields and exceeded limits throw
`AddressBookParseException` without returning a partial snapshot. File/reader I/O
and encoding failures propagate. Invalid API arguments throw argument exceptions.

Fixed limits bound both work and retained memory:

| Limit | Value | Rationale |
| --- | --- | --- |
| Total decoded input | 16,777,216 UTF-16 characters | Bounds the whole import, including comments, duplicate lines, BOM and line endings |
| Physical line (excluding CR/LF) | 16,384 characters | Checked while reading, before allocating an unbounded line, including comments/whitespace |
| Destination after edge whitespace removal | 8,192 characters | Allows variable certificate/key sizes well beyond common destinations while rejecting absurd values |
| Metadata suffix after `#!` | 4,096 characters | Bounds a complete property block before allocating its map |
| Metadata properties per line | 16 | Bounds generic annotation count; duplicate keys reject |
| Metadata key / value | 64 / 2,048 characters | Bounds each retained annotation |
| Unique hostname/B32 identities | 10,000 | Bounds collection overhead, including alternate destinations; equivalent duplicates still count toward total input |
| Source label | 1,024 characters, no controls | Bounded provenance and diagnostics |

Limits are inclusive. Empty/comment-only books produce an empty snapshot. Large
books must be explicitly split or reviewed before a future change to these limits.
