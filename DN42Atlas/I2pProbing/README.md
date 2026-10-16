# Isolated single-seed probe

`new I2pHttpProbe(transport).ProbeAsync(validatedSeed, cancellationToken)` uses a
caller-owned `I2pHttpTransport`. It sends only `GET /robots.txt`, with user agent
`I2PAtlas/0.1`, to the destination-derived Base32 identity. There is no CLI, scanner,
snapshot orchestration, publisher or viewer integration.

## Observations and authorization

`I2pRequestOutcome` distinguishes rejected null input (before networking), proxy
connection failure, deadline, other transport/HTTP failure, and a buffered proxy
HTTP response. Invalid seeds cannot be constructed; there is no arbitrary URL
input. Socket evidence plus a connection error is required for `ProxyUnavailable`;
oversized/invalid HTTP is `TransportFailure`. Caller cancellation throws
`OperationCanceledException`, including during parsing, and is not a result state.

`OriginEvidence` is `NotObserved` without a usable response and `Unproven` with
one. The configured HTTP proxy may generate any status or body. Neither a 200,
404, HTML error-page fingerprint nor a self-asserted header proves origin. The
[I2P tunnel documentation](https://www.i2p.net/en/docs/api/i2ptunnel/) describes
proxy-generated pages; it does not provide authenticated response provenance.
Destination-derived routing identifies the requested peer, not the response author.

`Robots` describes the observed bytes: not observed, unknown status (including
404), redirect, unsupported representation, too large, malformed, allows root or
disallows root. Only status 200 is parsed. UTF-8 (optionally BOM) is accepted;
invalid UTF-8 and control bytes fail conservatively. Absent Content-Type is
allowed for observation; an explicit type must be text/plain and any charset must
be UTF-8. Content-Encoding is unsupported. The shared DN42 robots evaluator
selects I2PAtlas groups before wildcard groups, uses longest matching rules with
Allow winning ties, and returns unknown for malformed policy. No DN42 network or
404 authorization logic is shared.

Homepage access requires both authoritative origin provenance and an allowing
robots policy. The current generic proxy provides no such provenance. Consequently
**no response authorizes `/` in this slice**, including an observed explicit Allow
or empty policy. `Homepage` records the skip reason (rejected input, failed
request, unknown robots, observed disallow or unproven origin), and
`HomepageRequestAttempted` is always false. There is no caller trust override.
Adding a trustworthy provenance mechanism must be an explicit later design change.

## Bounds and inert metadata

The existing transport retains its 16 KiB header cap, 256 KiB buffered body cap,
default 10-second connection/header/body deadline and caller cancellation. Robots
evaluation tightens its body cap to 64 KiB. Oversized transport responses fail;
robots between 64 and 256 KiB are recorded as too large and never parsed. Shared
robots regex matches have a 100 ms deadline and cancellation checks between rules.
Content-Type metadata is capped at 256 characters. Location is retained verbatim
up to 4096 characters with a truncation flag. **Zero redirects are followed**, even
same-identity redirects. No query/address-helper is processed or requested.

The network-independent `I2pPageMetadata.Read` helper is tested with fixture bytes
in preparation for a future authenticated homepage response. It is deliberately
not called on robots/proxy pages. It examines at most 256 KiB, captures content
type and a decoded/whitespace-normalized title up to 200 characters, and records
up to 250 unique HTTP(S) URLs (4096 characters each, at most 1000 candidates).
Extraction has cancellation checks and 100 ms non-backtracking regex deadlines;
truncation/incomplete extraction is explicit. UTF-8 is used; other homepage
charset support needs a later decision. Binary content is not inspected. Quoted
hrefs and plain HTTP(S) URLs are recognized, relative URLs resolve against the
approved destination identity, fragments are removed, and links are sorted
ordinally. HTML base tags do not change the approved base. Valid DNS-shaped I2P
names are classified separately from ordinary clearnet names; IP literals,
credentials, unsafe schemes and address-helper links are omitted. Discovered URLs
are inert metadata, never request targets, scheduled work or recursive discovery.

All seed mappings, destination identities, responses, locations and discovered
metadata in this API are **private scan state**. This slice defines no public
projection or artifact schema and copies nothing to public artifacts.

Real-router integration remains necessary to observe actual error/status/header
behavior and Base32 routing support. A router experiment alone cannot establish a
generic origin-authentication contract. Friendly-name virtual hosting, encrypted
LeaseSets, supported encodings and a well-supported provenance mechanism remain
future decisions; none may enable DNS, outproxy or direct fallback.
