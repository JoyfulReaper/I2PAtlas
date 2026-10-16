# Local I2P addressbook imports

`AddressBookParser.ParseFile(path)` reads a local UTF-8 file (optional UTF-8 BOM).
`Parse(reader, source)` reads a caller-owned `TextReader` and leaves it open.
Neither API performs DNS, router lookups, HTTP requests or proxy configuration.
This layer is not connected to the existing DN42 command/scanner pipeline.

Each non-comment line must be `hostname.i2p=destination`. LF, CRLF and CR line
endings are accepted, including a final line without a newline. Blank lines and
lines whose first non-space/tab character is `#` or `;` are ignored. Only ASCII
spaces and tabs are removed around fields. Split at the first `=`: trailing `=`
characters belong to Base64 padding. Inline comments, subscription commands,
continuations and escaped/property-file syntax are not supported and fail
validation when appended to an entry. A leading BOM is accepted only at the
start of input. Invalid UTF-8 fails explicitly.

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

Duplicate normalized hostnames with ordinally identical destination text are
deduplicated. Conflicts fail the entire import, including padded versus unpadded
spellings of the same destination; no destination canonicalization is attempted.
Different friendly names may share a destination. Successful snapshots retain
first-seen order, immutable seeds and a source label (the absolute path for file
imports). Source plus the preserved destination provide minimal import provenance
and destination identity for later artifacts; there is no raw scan schema here.
Errors carry a one-based line number and source label and never echo destinations.
Malformed entries, missing fields, conflicts and exceeded limits throw
`AddressBookParseException` without returning a partial snapshot. File/reader I/O
and encoding failures propagate. Invalid API arguments throw argument exceptions.

Fixed limits bound both work and retained memory:

| Limit | Value | Rationale |
| --- | --- | --- |
| Total decoded input | 16,777,216 UTF-16 characters | Bounds the whole import, including comments, duplicate lines, BOM and line endings |
| Physical line (excluding CR/LF) | 16,384 characters | Checked while reading, before allocating an unbounded line, including comments/whitespace |
| Destination after edge whitespace removal | 8,192 characters | Allows variable certificate/key sizes well beyond common destinations while rejecting absurd values |
| Unique normalized hostnames | 10,000 | Bounds collection overhead; identical duplicates still count toward total input |
| Source label | 1,024 characters, no controls | Bounded provenance and diagnostics |

Limits are inclusive. Empty/comment-only books produce an empty snapshot. Large
books must be explicitly split or reviewed before a future change to these limits.
