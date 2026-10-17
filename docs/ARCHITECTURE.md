# I2PAtlas architecture

I2PAtlas has eight explicit CLI commands: separate scan and one-hop crawl
pipelines through public projection to standalone HTML. There is no hosted service or deployment layer.

```text
local effective addressbook export
    |
    | import-seeds: AddressBookParser -> SeedSnapshotImporter
    v
private i2patlas.seed-snapshot v1
    |
    | SeedSnapshotReader -> I2pScanService
    |                         |
    |                         +-> I2pHttpProbe -> I2pHttpTransport
    |                              robots         fixed loopback HTTP proxy
    |                              conditional /  destination-derived B32 target
    v
private i2patlas.raw-scan v1
    |
    | i2p-project: I2pRawScanReader -> I2pPublicProjector
    v
public i2patlas.public-scan v1
    |
    | i2p-view: I2pPublicScanReader -> I2pViewerGenerator
    v
standalone HTML, static content plus optional JavaScript enhancement

i2p-probe-test: strict seed loading -> one uniquely selected friendly name
               -> same probe/transport -> bounded private console report

i2p-crawl: strict seed loading -> selected known-seed probe wave
            -> pure bounded frontier selection using full snapshot suppression
            -> terminal host-only probe wave -> private i2patlas.raw-crawl v1
            -> i2p-crawl-project -> public i2patlas.public-crawl v1
            -> i2p-crawl-view -> standalone crawl inspection HTML
```

## Trust boundaries

Local files are untrusted input, not authority merely because they are local.
Each boundary validates its own schema, fields, types, bounds and consistency.
Hashes identify exact bytes; they do not authenticate an edited artifact.

The configured HTTP proxy is the sole network peer. An HTTP response describes
what that proxy returned. Neither destination-derived addressing nor a plausible
robots response authenticates the response author. `I2pOriginEvidence` stays
`NotObserved` without a usable response or `Unproven` with one.
`I2pProxyResponseClassification` is a separate heuristic.

Seed snapshots and raw scan/crawl artifacts are private operational data. The explicit
scan and crawl projections are explicit allowlist boundaries into their respective
public schemas. Each viewer accepts only its own public schema and encodes text again for HTML.
No artifact's existence asserts origin authenticity.

## Addressbook import and seed identity

[AddressBook/](../I2PAtlas/AddressBook/README.md) owns `I2pSeed`,
`AddressBookParser` and immutable parsed snapshots. A normal mapping is
`hostname.i2p=destination`. The parser supports blanks, whole-line comments,
ASCII surrounding spaces/tabs and bounded structured SusiDNS `#!key=value#...`
metadata. Bare inline `#` text is not discarded. Malformed mappings and metadata
fail explicitly.

Hostname normalization lowercases ASCII names and removes one optional trailing
dot before strict `.i2p` validation. Destinations retain their validated I2P
Base64 spelling and padding, except surrounding spaces/tabs. Encoding and the
variable-length destination/certificate envelope are validated; signatures and
ownership are not.

Semantic identity is a normalized hostname plus destination-derived B32 hostname.
Equivalent Base64 spellings deduplicate on import, retaining the first private text.
`AddressBook/I2pSeedIdentity` owns the shared binary hash/B32 derivation used by
import, request targets and strict readers. Alternate destinations for the same friendly name stay
independent; different friendly names may share a destination. Metadata cannot
change identity or execute subscription actions. An `action=adddest` annotation
does not add any mapping beyond the ordinary mapping on that line.

Parsed seeds sort ordinally by hostname then preserved destination text.
Private per-line metadata remains in `AddressBookSnapshot.EntryMetadata`;
seed snapshot v1 does not persist it. The original source-byte hash still covers
all metadata. See the component README for exact grammar and inclusive limits.

## Private seed snapshot

[Seeds/](../I2PAtlas/Seeds/README.md) owns import and validated loading.
`SeedSnapshotImporter` hashes the same open byte stream consumed by the parser,
including BOM, comments, duplicates, metadata, whitespace and original newlines.
It records UTC import time, source path, SHA-256 and byte length alongside the
immutable seed array in `i2patlas.seed-snapshot` v1.

`SeedSnapshotReader` requires the exact schema and integer version 1, required
field sets, unique JSON properties, supported types, explicit UTC time, bounded
provenance and seeds reconstructed through `I2pSeed`. Identical normalized pairs
deduplicate; alternate destinations survive. Different Base64 spellings for one
semantic identity are rejected with a re-import instruction, never repaired.
Accepted seeds are ordinally sorted.
The reader is local and never reopens the recorded addressbook path.

`ReadWithIdentityAsync` additionally returns SHA-256 and byte length for the
exact snapshot JSON buffer validated on that read. This is distinct from the
earlier addressbook hash. Neither hash is a signature or a file-authentication
mechanism.

## Fixed proxy transport

[I2pTransport/](../I2PAtlas/I2pTransport/README.md) owns the proxy endpoint,
destination-bound request target and disposable HTTP transport.

`I2pProxyEndpoint` requires an explicit HTTP URI, literal loopback address and
port 1–65535. Canonical dotted-decimal IPv4 loopback and bracketed IPv6 loopback
are accepted, with an optional trailing slash. Hostnames (even `localhost`),
shorthand IPv4, mapped/scoped IPv6, non-loopback addresses, credentials, other
paths, queries, fragments and other schemes reject before networking.

The request target requires a validated seed and HTTP/80. It accepts that seed's
friendly name or derived B32 name, then binds the request URI and Host header to
`base32lower(SHA256(binary destination)) + .b32.i2p`. Decoding is temporary for
hashing and does not rewrite the seed's preserved destination text.

`SocketsHttpHandler` has a fixed proxy with no bypass. Its connection callback
checks the literal proxy IP/port and connects using an `IPEndPoint`. There is no
operating-system DNS, direct destination connector, ambient proxy selection,
clearnet request or fallback. Failure to use B32 at the proxy remains a failure.

Only HTTP/1.1 GET is used. Cookies, automatic authentication, decompression and
redirect following are disabled. Requests have no queries/fragments, and address
helpers are not executed. The transport is shared within a scan; the application
does not add retries, though the HTTP runtime may retry a failed pooled
connection through the same guarded proxy.

## Robots classification and conditional homepage

[I2pProbing/](../I2PAtlas/I2pProbing/README.md) owns `I2pHttpProbe`.
For each seed it requests only `/robots.txt` and, if authorized, `/`, using
User-Agent `I2PAtlas/0.1`.

`Robots/RobotsPolicy.cs` evaluates the observed text for product `I2PAtlas` and
root path `/`. Product groups take precedence over wildcard groups; longest
matching rules win, Allow wins ties, and malformed policy returns unknown.
Observation parsing and the classification gate remain distinct: an absent
Content-Type can still produce an Allow observation without authorizing `/`.

The classifier uses these rules:

| Classification | Robots response evidence |
| --- | --- |
| `LikelyProxyFailure` | HTTP 502, 503 or 504; authorship is still unknown |
| `LikelyOriginResponse` | HTTP 200; exactly one valid explicit `text/plain` Content-Type; no Content-Encoding/Content-Range; absent charset or one UTF-8 charset; body at most 64 KiB; valid UTF-8 without unexpected controls; successful Allow/Disallow parsing; recognizable User-agent followed by Allow/Disallow policy |
| `Ambiguous` | Everything else, including 4xx, redirects, bare 200, missing representation and malformed/oversized policy |

Homepage authorization is exactly:

```text
ProxyResponseClassification == LikelyOriginResponse
&& Robots == AllowsRoot
```

This heuristic can both accept proxy-generated policy and skip real origin
responses. It never promotes `Unproven`. A 403 stays ambiguous; 502/503/504 stop
the probe regardless of who authored them. There is no status-only permission,
header-based origin proof or proxy error-page fingerprinting.

The homepage attempt uses the same target identity and transport. Its outcome,
origin evidence, classification, response and disposition remain separate from
robots evidence. `HomepageRequestAttempted` records attempts including failures.
Homepage 502/503/504 are likely proxy failures; other homepage responses,
including 200, remain ambiguous.

Only HTTP 200 homepage bytes without Content-Encoding enter `I2pPageMetadata`
for bounded title and link extraction. Relative references resolve against the
approved B32 base; HTML base tags do not select another authority.
The probe retains URLs as metadata without following them; address-helper,
credential-bearing, IP-literal and unsafe-scheme links are omitted. The separate
crawler may admit only eligible host identities from depth-0 metadata.

## Bounded multi-seed scan and raw artifact

[I2pScanning/](../I2PAtlas/I2pScanning/README.md) owns `I2pScanService`.
It reads only the strict private seed snapshot, never the addressbook again.
It selects an optional prefix from the sorted seed array and starts exactly
`min(concurrency, selected count)` workers. Defaults are concurrency 2,
10000 ms per request and at most 10000 seeds; CLI bounds are 1–4, 1–60000 ms and
1–10000 seeds respectively.

A worker finishes one seed before claiming the next. Stable result slots preserve
hostname/destination-text order independent of completion order. There is no
task per seed waiting on a semaphore, discovered-link queue or orchestration retry
loop. Expected network failures remain observations. Unexpected per-seed
exceptions produce bounded `UnexpectedProbeFailure` evidence with unknown
partial request state; exception messages/stack traces are omitted. Cancellation,
resource exhaustion and orchestration/storage failures abort the whole scan.

`i2patlas.raw-scan` v1 records:

- UTC scan start/completion, seed snapshot identity/hash and original source hash,
  canonical proxy IP/port, settings, selected/full counts and seed-limit status.
- Each friendly hostname, preserved Base64 destination and derived B32 identity.
- Separate robots and homepage outcomes, origin evidence, classifications,
  response metadata, robots observation and homepage attempt/disposition.
- Bounded homepage title, content type, discovered URLs, timing and fixed errors.
- Duration, outcome/classification counts, homepage attempt/response counts,
  unexpected failures and maximum active seeds.

Raw data includes no response bodies, arbitrary response headers or exception
text. All its fields remain private by default, even fields that the projection
later selects. Raw status/response counts include HTTP error responses.

Selected resource limits:

| Boundary | Bound |
| --- | --- |
| Addressbook | 16,777,216 decoded UTF-16 characters; 16,384 per physical line; 8192 per destination; 10000 unique semantic identities |
| Seed snapshot reader | 32 MiB JSON; depth 8; 10000 entries before pair deduplication |
| HTTP request | 16 KiB response headers; 256 KiB buffered body; default 10-second connection/header/body deadline |
| Robots evaluation | 64 KiB; regex match deadline 100 ms |
| Homepage extraction | 200-character raw title; 250 unique links; 4096 characters per URL; at most 1000 candidates |
| Scan retained text | 32 Mi UTF-16 characters plus bounded in-flight probes |
| Raw/public JSON | 128 MiB |
| Public title / media type | 160 UTF-16 characters / 80 characters |
| Generated viewer content | 64 Mi UTF-16 characters across generated directory/details/overview |

These are implementation limits, not configurable promises beyond the documented
CLI options. Component READMEs describe additional per-field bounds.

## Private one-hop crawl

[I2pCrawling/](../I2PAtlas/I2pCrawling/README.md) owns the separate `i2p-crawl`
command's service, settings, private raw model, staged writer and strict reader.
It reads/hashes the seed snapshot exactly once, selects its sorted prefix, and
uses one guarded transport and the shared Crawler 0 probe state machine.
Known seeds remain destination-bound and independent, including alternate
destinations for one friendly name.

The fixed worker count is `min(concurrency, wave target count)`. All depth-0 work
finishes before the unchanged pure Crawler 1a selector runs exactly once, using
the complete snapshot for known friendly/B32 suppression. Only complete eligible
HTTP-200 HTML/XHTML parents contribute. The lexically smallest K unknown hosts
retain deterministic smallest known-parent provenance. A second bounded worker
wave probes those host-only targets; depth 1 is terminal. Its links stay private
evidence, never another frontier. No target DNS, application retries, direct
connections or original discovered URL requests are added.

Defaults are 2 workers, 10000 ms per request, 10000 seeds and 100 discovered hosts;
CLI ranges are 1–4, 1–60000, 1–10000 and 0–1000 respectively. Zero host capacity
disables depth-1 requests while preserving frontier counts. There is no depth
option. Expected target failures remain observations; unexpected nonfatal probe
exceptions retain sanitized unknown partial state. Infrastructure failure cancels
siblings; cancellation/resource limits abort installation and preserve old output.

`i2patlas.raw-crawl` v1 separates known results (hostname, preserved destination,
derived B32, depth 0, evidence) from discovered results (hostname, HostForm,
depth 1, structured selected-known-parent hostname+B32 referrer, evidence).
Known order is hostname then preserved destination text; discovered order is
hostname, all ordinal. Host-only results never acquire a destination or inferred
identity. Friendly hosts depend on proxy/router resolution, and all response
origin evidence remains unauthenticated. Top-level provenance/settings, seed and
frontier counts, UTC times and metrics are private alongside every result field.

The hard bounds are 32 Mi UTF-16 retained evidence characters and 128 MiB JSON,
with existing per-response/page limits unchanged. `I2pRawCrawlReader` rejects
unknown/duplicate/missing fields, unsupported schema/version, malformed identity,
host form/depth/order/referrer mismatches, invalid times/settings/gates, inconsistent
counts/metrics and excess evidence. It validates selected parent membership and
the wave barrier without reopening source files or claiming origin proof.
The private writer flushes and closes a bounded staged file, strictly validates
its bytes, then checks cancellation and replaces the output. No partial final
artifact is installed. Existing raw-scan/public-scan schemas stay unchanged;
`i2p-project` and `i2p-view` reject crawl artifacts; the crawl commands remain separate.

`i2p-crawl-project` is a separate purely local path through the unchanged strict
`I2pRawCrawlReader`, `I2pPublicCrawlProjector` and staged `I2pCrawlProjectionService`.
`i2patlas.public-crawl` v1 contains crawl UTC start/completion, known/discovered
counts and one entries array with closed `KnownSeed` and `DiscoveredHost` shapes.
Known entries expose hostname+derived B32, depth 0 and public observations.
Discovered entries expose hostname, public HostForm, depth 1, parent hostname+B32
and public observations; they never acquire a destination identity. Parent B32
keeps alternate destinations unambiguous. Known entries sort hostname then B32;
discovered entries follow in hostname order, all ordinal.

The allowlist excludes operational provenance, Base64 destinations, proxy/settings,
raw metrics, errors, individual timings, redirects and full URLs. Only sanitized
titles/media types and normalized I2P hostnames survive metadata projection.
Scan and crawl share small neutral policy helpers without changing public-scan
serialized output. `I2pPublicCrawlReader` requires exact fields, named enums,
canonical identities/order, valid kind-specific shapes/depths/referrers, matching
counts, UTC times, consistent observations/gates and public metadata bounds.
The 128 MiB staged public file is flushed, closed and strictly read before final
replacement. Failure or cancellation before replacement preserves previous output.
Projection creates local JSON only, with no deployment or network access.

## Strict raw reader and public projection

[I2pPublishing/](../I2PAtlas/I2pPublishing/README.md) implements a separate offline
boundary. `I2pRawScanReader` validates raw v1 with strict UTF-8, exact field sets,
unique properties, named string enums, explicit UTC times, bounds, ordering and
counts. It reconstructs seed identity, recomputes B32, checks request/response/
classification/homepage-gate consistency and recomputes metric dictionaries.
It rejects malformed evidence instead of repairing or reordering it.

Stored bodies are absent, so validation cannot rerun robots parsing or establish
response authenticity. Passing validation means structural/contract consistency.

`I2pPublicProjector` produces a distinct immutable `i2patlas.public-scan` v1 model.
Its explicit allowlist includes friendly/B32 names, scan-level UTC times,
observations/statuses, sanitized bounded title/media type and sorted discovered
I2P hostnames. It excludes Base64 destinations, proxy/settings, source paths/
hashes, per-seed times, private errors, redirects, full URLs and clearnet links.
Public strings remain untrusted display data.

Private seed/result order is hostname then destination text; public order is
hostname then B32. No destination is preferred or removed for sharing a friendly
hostname. `destinationsForHostname` counts distinct B32 identities. The raw reader
and projector reject duplicate friendly-name/B32 identities rather than merging
scanned observations. The public viewer reader retains the same strict duplicate
check and unchanged stable IDs.

## Strict public reader and viewer

[I2pReporting/](../I2PAtlas/I2pReporting/README.md) owns public-only loading and
HTML generation. `I2pPublicScanReader` accepts only public-scan v1, with exact
fields including explicit nulls, bounds, named enums, normalized names, canonical
B32 identities, state consistency, derived counts and strictly sorted unique
public identities/discovered-host arrays. It never loads private snapshots/raw
data or constructs a transport.

`I2pViewerGenerator` embeds a local template and renders all directory rows,
details and discovery overview during generation. Every public string is
HTML-encoded. Enhancement reads encoded HTML attributes; there is no dynamic
JSON fetch, framework or remote asset. A restrictive CSP limits network
subresources, and the empty data favicon avoids an implicit lookup.

Stable detail IDs are `i2p-service-` plus lowercase SHA-256 of UTF-8
`hostname + "\n" + destinationHostname`. They do not depend on title, time,
position or classification. The enhanced default filter is homepage HTTP 200
observed, independently of restored browser form values. No-JavaScript use
retains the complete static view; enhancement adds search/filter/sort/history
and navigation.

External I2P links are explicit user actions. Friendly links depend on the
visitor's addressbook; B32 links preserve the displayed destination identity.
They use HTTP root URLs with no reconstructed private paths/queries. The
browser does not inherit the scan proxy setting.

## Standalone crawl inspection viewer

`i2p-crawl-view` uses the separate `I2pCrawlViewerGenerator` and embedded
`crawl-viewer.html`; it never guesses between schemas or changes the scan viewer.
Its only input boundary is the unchanged strict `I2pPublicCrawlReader`.
No private reader, crawler or transport is constructed. The page shows the crawl
UTC interval, known/discovered/total counts, homepage responses observed and title
counts. All entries and complete public observations are rendered statically.
Known seeds retain visible destination B32; discovered hosts retain their host
form and exact parent hostname+B32, never an invented destination identity.

Detail IDs are `i2p-crawl-entry-` plus lowercase SHA-256 of UTF-8 identity material:

```text
KnownSeed\n<hostname>\n<destinationHostname>
DiscoveredHost\n<hostname>\n<referrerHostname>\n<referrerDestinationHostname>
```

Observations, times and array position do not affect IDs. Native detail and parent
anchors work with JavaScript disabled. Enhancement only searches hostname/title,
filters View/Kind/Summary and sorts directory rows by hostname, kind, summary or numeric
homepage HTTP. View defaults to Likely reachable: exactly `HomepageResponseObserved`,
an unauthenticated proxy observation of a homepage HTTP response, not origin/liveness
verification. All entries restores the directory, subject to independent search,
Kind and Summary filters. Reset restores Likely reachable, clears other filters
and retains sorting; missing HTTP statuses sort last in either direction. Without
JavaScript or on enhancement failure, all directory rows remain available. Detail
articles remain in the HTML but are hidden until selected by CSS `:target`, with
no JavaScript requirement. Back to directory hides the selected article. Filters
never control detail visibility, so filtered rows cannot make deep links inaccessible.
There is no History API, random selection, clipboard feature or extra keyboard
navigation. An enhancement failure restores all directory rows and disables controls.

Every public string is HTML-encoded again. Links validate canonical `.i2p` syntax
and use only explicit HTTP root anchors with `_blank`/`noopener noreferrer`.
The report has a restrictive CSP, data favicon, no remote assets, no fetch and no
public JSON in executable code. Unicode and literal title markup remain text.
The complete HTML, including encoded attributes, details and template, is bounded
to 64 Mi UTF-16 characters. Rendering completes before exclusive private staging,
async/disk flush, close, cancellation check and same-filesystem replacement.
Input/output use the existing explicit-local-path and reparse-point policy.
Validation, rendering, I/O and observed cancellation before replacement preserve
previous HTML. This is local generation, not a hosted service or deployment step.

## File installation, failures and cancellation

All output paths are explicit and output directories must already exist.
Import, projection and viewer generation perform no network access. Scan/crawl output
must be distinct from its snapshot and rejects known served-root path components
(`published`, `public`, `wwwroot`, `site`); this cannot detect every served root.
Seed and raw files must remain outside all served directories.

Each writer creates a unique private temporary file beside its final output,
serializes/renders completely, flushes asynchronously and to disk, closes, checks
cancellation and replaces the final path. `IO/PrivateFile.cs` provides exclusive
creation and Unix 0600 permissions; Windows inherits directory ACLs.
Observed cancellation and failures before replacement preserve prior output,
with owned staging files removed on normal failure.

Replacement is the completion boundary. A subsequent stdout failure can return
a failure code even though the artifact was installed. A crash can leave staging
files. This is a per-file same-filesystem replacement, not a power-loss guarantee,
multi-file transaction, checkpoint system or cross-process lock. Strict path
checks do not detect every hard-link alias or coordinate simultaneous writers.

The scan cancels in-flight requests and stops workers on caller cancellation.
`Program.cs` installs Ctrl+C handlers for scan, crawl, both projection and both viewer commands;
they return 130 when cancellation is handled. Import/probe APIs accept cancellation
tokens, but their CLI dispatch has no equivalent graceful Ctrl+C handler.

Help/no arguments exit 0 and unknown commands exit 2 before any work.
Argument errors generally exit 2; input/I/O failures exit 1. Diagnostic exit 0
means complete HTTP responses were observed in all attempted phases, even HTTP
errors. Scan exit 0 means the private artifact was installed, even for empty or
entirely failed seed observations. Crawl exit 0 likewise means its private
artifact was installed. Consult component READMEs for exact contracts.

## Validation and future work

Build and test commands are in the [root README](../README.md#build-and-test).
The .NET suite covers strict imports/readers, exact-byte hashes, multiple
destinations, fake-proxy routing, robots/homepage gates, bounds, cancellation,
replacement failures, projection privacy and viewer generation. Node tests cover
the viewer's HTTP-200 default, filtering, sorting, navigation and stable identities.
No automated test requires a real I2P router.

Crawling beyond depth 1 remains future work. There is no scheduler or deployment package.
