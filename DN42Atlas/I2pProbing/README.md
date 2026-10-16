# Isolated single-seed probe

`new I2pHttpProbe(transport).ProbeAsync(validatedSeed, cancellationToken)` uses a
caller-owned `I2pHttpTransport`. It sends only `GET /robots.txt`, with user agent
`I2PAtlas/0.1`, to the destination-derived Base32 identity. The explicit diagnostic
CLI below is available; scanner, multi-seed orchestration, publisher and viewer
integration remain outside this component.

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

## Local proxy diagnostic

Select one existing friendly hostname from an explicit private seed snapshot and
an explicit local HTTP proxy:

```text
dotnet run --project DN42Atlas -- i2p-probe-test private/i2p-seeds.json some-site.i2p http://127.0.0.1:4444
```

The exact command syntax is:

```text
i2p-probe-test <snapshot-file> <hostname.i2p> <proxy-uri> [--timeout-ms <1-60000>]
```

Quote paths containing spaces. Relative snapshot paths resolve against the
working directory. There is no default seed, snapshot or proxy. Hostnames use
the existing case, surrounding-space/tab and single-trailing-dot normalization.
The entire snapshot passes the [validated local reader](../Seeds/README.md#validated-local-loading)
before any networking, even entries other than the selected seed. Missing seeds
fail; the command does not import a hostname supplied outside that snapshot.

Proxy validation requires an HTTP URI with literal loopback IP and explicit port.
The existing transport alone performs networking. The existing probe runs without
policy changes: one destination-bound `/robots.txt` request, no homepage, no
redirects, no helper processing, no discovered-link requests. Command dispatch
precedes all DN42 policy, registry, scanner, lock and publication setup. There is
no DNS, ambient proxy, direct route, clearnet fallback or output artifact.

The report shows the normalized friendly hostname, destination-derived Base32
hostname, canonical configured proxy, request outcome, origin-evidence state,
HTTP status, content media type, buffered body byte count, robots observation,
homepage disposition/attempt flag, redacted redirect location and its probe
truncation flag, and elapsed probe milliseconds. Timing covers the probe, not
snapshot loading. Default timeout is the transport's 10 seconds; an explicit
`--timeout-ms` can select 1 through 60000 milliseconds. A timeout, connection
refusal or transport failure still prints the report, with unobserved fields
labeled accordingly.

Reports are private diagnostic observations, capped at 4096 characters. No full
destination, source path/hash, snapshot path, robots body, raw headers or reason
phrase is printed. Content-Type parameters are omitted; only a safe ASCII media
type up to 80 characters is shown. HTTP(S) redirects show a bounded ASCII authority
only; credentials, paths, queries and fragments are redacted. Relative, malformed,
unsupported and unsafe-authority locations receive a redacted placeholder. This
also prevents address-helper values, tokens and terminal controls leaking through
redirects. The probe's reusable raw observation remains unchanged. Input/file
errors omit private paths and values. This command adds no header-based trust
heuristics and no origin-verification override.

Exit codes: **0** means a complete proxy HTTP response was observed (including
404/407/500), **1** means snapshot/selection/I/O failure or unsuccessful probe,
and **2** means invalid/missing command arguments, hostname, proxy or timeout.
Exit 0 never asserts origin reachability or authorizes a homepage. API caller
cancellation propagates as `OperationCanceledException`, distinct from timeout.
Unit/CLI tests use local fake proxies only. Running the example above is an
explicit real-router diagnostic; automated tests never require a running router.
