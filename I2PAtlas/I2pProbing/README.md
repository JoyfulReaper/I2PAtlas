# Isolated single-seed probe

`new I2pHttpProbe(transport).ProbeAsync(validatedSeed, cancellationToken)` uses a
caller-owned `I2pHttpTransport`. It sends `GET /robots.txt`, then conditionally
`GET /`, with user agent `I2PAtlas/0.1`, to the destination-derived Base32 identity. The explicit diagnostic
CLI below is available. The [bounded private snapshot scanner](../I2pScanning/README.md)
reuses this component; publisher and viewer integration remain outside it.

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

## Heuristic classification and homepage authorization

`ProxyResponseClassification` is separate from `OriginEvidence` and describes the
robots response. It never promotes `Unproven` into an authenticated state:

- `LikelyProxyFailure`: HTTP 502, 503 or 504, regardless of body. This is a
  conservative stop signal, not proof the local proxy authored it. An origin or
  another intermediary could return these statuses too.
- `LikelyOriginResponse`: HTTP 200, a single valid explicit text/plain Content-Type,
  no Content-Encoding or Content-Range, absent charset or one UTF-8 charset,
  body at most 64 KiB, strict valid UTF-8 without unexpected controls, and a
  successful Allow/Disallow result from the shared robots parser. Additionally,
  require recognizable policy: a nonempty User-agent directive followed by an
  Allow or Disallow directive. Empty, comment-only, agent-only and unrecognized
  directive-only policies do not qualify, even if the parser defaults to Allow.
- `Ambiguous`: everything else, including all 4xx (403 and 404 included), other
  5xx, redirects, bare 200, missing/invalid representation, oversized or malformed
  robots. Without a response it is also Ambiguous, with evidence NotObserved.

**The exact homepage gate is `LikelyOriginResponse && Robots == AllowsRoot`.**
An explicit Disallow prevents access even when classification is likely origin.
There is no caller trust override, status-only permission, error-page fingerprint,
header-based origin proof or recursive discovery. The homepage is requested once
at the same approved destination's root, with no queries or redirects.

This is an intentional heuristic authorization policy, not authentication. A
proxy-generated page could imitate a valid robots policy. Conversely, an actual
origin may return an ambiguous error or a harmless empty robots file and be
skipped. The [I2P proxy documentation](https://www.i2p.net/en/docs/api/i2ptunnel/)
describes locally generated error pages. [RFC 9110 sections 15.6.3–15.6.5](https://www.rfc-editor.org/rfc/rfc9110.html#section-15.6.3)
define gateway failures/timeouts and service unavailability; 503 alone does not
identify a proxy. These sources support caution, not an authenticated author test.

Top-level `RequestOutcome`, `OriginEvidence`, `ProxyResponseClassification` and
`RobotsResponse` always describe robots. `HomepageRequestOutcome`,
`HomepageResponse`, `HomepageMetadata`, `HomepageOriginEvidence` and
`HomepageProxyResponseClassification` describe the separate root attempt.
`HomepageRequestAttempted` follows the presence of a homepage request outcome,
including timeout/refusal/oversized HTTP failures. `Homepage` records a skip
reason, `ResponseObserved`, or `RequestFailed`. Homepage failures never overwrite
successful robots evidence or classification. Neither phase has a Reachable flag.
Homepage 502/503/504 are likely proxy failures; all other homepage statuses remain
Ambiguous, including 200. Its HTML is not evidence of authorship.

### Reported real-router observations

These are user-reported examples from the earlier robots-only diagnostic. They
are not automated test fixtures tied to live hosts or latency-based classifiers:

| Friendly hostname | Robots status/type | Observation | Approximate elapsed time |
| --- | --- | --- | --- |
| stats.i2p | 200, text/plain | AllowsRoot | 869 ms |
| smtp.postman.i2p | 403, text/html | Unknown | 6772 ms |
| 00.i2p | 504, text/html | Unknown | 44 ms |

The 403 remains Ambiguous; the 504 becomes LikelyProxyFailure. The 200 qualifies
only if its actual policy passes every structural/representation check above;
the old summary alone is insufficient. Hostname, timing and "known good" labels
are never classifier inputs. All unit tests use synthetic local proxies.

## Bounds and inert metadata

The existing transport retains its 16 KiB header cap, 256 KiB buffered body cap,
default 10-second connection/header/body deadline per request and caller cancellation. Robots
evaluation tightens its body cap to 64 KiB. Oversized transport responses fail;
robots between 64 and 256 KiB are recorded as too large and never parsed. Shared
robots regex matches have a 100 ms deadline and cancellation checks between rules.
Content-Type metadata is capped at 256 characters. Location is retained verbatim
up to 4096 characters with a truncation flag. **Zero redirects are followed**, even
same-identity redirects. No query/address-helper is processed or requested.

The network-independent `I2pPageMetadata.Read` helper processes homepage HTTP 200
bytes without Content-Encoding; errors, redirects and encoded bodies retain only
bounded response observations. It is not called on robots bodies. It examines at most 256 KiB, captures content
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
dotnet run --project I2PAtlas -- i2p-probe-test private/i2p-seeds.json some-site.i2p http://127.0.0.1:4444
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
If the friendly hostname has multiple destinations, this single-host command
fails before networking rather than selecting one arbitrarily. Use `i2p-scan`
to probe those distinct seeds independently.

Proxy validation requires an HTTP URI with literal loopback IP and explicit port.
The existing transport alone performs networking. The probe sends one
destination-bound `/robots.txt` request and, only through the exact gate above,
one root request. There are no redirects, helper processing or discovered-link requests. Command dispatch
precedes all DN42 policy, registry, scanner, lock and publication setup. There is
no DNS, ambient proxy, direct route, clearnet fallback or output artifact.

The report shows the normalized friendly hostname, destination-derived Base32
hostname, canonical configured proxy, request outcome, origin-evidence state,
HTTP status, content media type, buffered body byte count, robots observation,
homepage disposition/attempt flag, both heuristic classifications, separate
homepage request outcome/evidence/status/content-type/body count/link count,
redacted redirect locations and the robots truncation flag, and elapsed probe
milliseconds. Timing covers both phases of the probe, not
snapshot loading. Default timeout is the transport's 10 seconds; an explicit
`--timeout-ms` can select 1 through 60000 milliseconds per request (at most two
requests; normally at most 20 seconds of network deadlines by default). A timeout, connection
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
proof or origin-verification override. It reports heuristic classification
without presenting it as authenticated evidence. Titles and URLs remain private
in the result model; stdout shows only the extracted link count.

Exit codes: **0** means a complete proxy HTTP response was observed (including
404/407/500, and including an HTTP error from an attempted homepage), **1** means
snapshot/selection/I/O failure or a transport/timeout failure in either phase,
and **2** means invalid/missing command arguments, hostname, proxy or timeout.
Exit 0 never asserts origin reachability; homepage authorization uses only the
explicit robots gate, not the exit code. API caller
cancellation propagates as `OperationCanceledException`, distinct from timeout.
Unit/CLI tests use local fake proxies only. Running the example above is an
explicit real-router diagnostic; automated tests never require a running router.
