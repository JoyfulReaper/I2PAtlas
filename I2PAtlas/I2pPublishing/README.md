# Local public I2P projection

```text
dotnet run --project I2PAtlas -- i2p-project private/results/router-full.json published/i2p-latest.json
```

Both paths are explicit local files. The output directory must already exist;
`published/` is intentionally allowed for this public projection. Input and output
must differ; URLs, UNC/device paths and filesystem links/reparse-point ancestors
are rejected. This is a local artifact transformation with no
networking, DNS, scanning or viewer generation. It never
reopens the addressbook or seed snapshot. No discovered link creates work.

Exit codes: 0 installed, 1 invalid raw evidence or I/O/resource failure, 2 invalid
arguments/paths, 130 cancellation. Diagnostics contain aggregate counts and generic
errors, never private field values or paths.

## Strict private input

`I2pRawScanReader.ReadAsync` accepts only `i2patlas.raw-scan`, integer version 1.
The 128 MiB file limit matches the raw writer; it is checked both before reading
and against bytes consumed. JSON must be strict UTF-8 (no BOM), valid Unicode,
with at most 16 nesting levels. Comments and trailing commas are rejected.
Every object has an exact field allowlist: duplicate/unknown properties and
missing fields are rejected, including nullable fields. Numbers, booleans,
strings and arrays have exact types; enum strings are case-sensitive named values,
never integer values. Nullable observations remain nullable when state is unknown.

The reader validates:

- UTC timestamps with explicit `Z`/`+00:00`, import <= scan start <= scan end,
  and each result's start/end inside that scan interval.
- Existing strict hostname/destination rules, already normalized spelling, and
  exact recomputation of the B32 identity through the shared neutral
  `I2pSeedIdentity` helper. No HTTP transport is constructed.
- Strict ordinal hostname/destination-text result ordering, no duplicate hostname/B32 identities,
  declared counts, seed limit application and bounded settings. The entire snapshot
  count and limit must explain the number of selected results.
- Literal canonical loopback proxy identity and port, seed schema/version, bounded
  hashes and byte lengths, finite nonnegative durations, and existing per-field
  response/title/link bounds. HTTP statuses have three digits (100–999), including
  unrecognized statuses that remain observations.
- Response/outcome/evidence consistency, existing robots/homepage authorization
  invariants, separate homepage observations, and metadata matching a 200 homepage.
  Raw classifications are checked against the status/representation/observation
  evidence available in the artifact. Bodies are absent, so the reader cannot
  re-run robots parsing, establish authenticity, or verify a claimed response.
- All four metric dictionaries are recomputed exactly; attempt/response/unexpected
  failure totals must match results. Maximum active seeds must fit concurrency and
  result count. Monotonic elapsed times are bounded numbers, not reinterpreted as
  exact wall-clock durations or a reconstructable scheduling history.

Arrays and strings retain the producer's bounds: 10,000 results, 250 links per
page, 4,096 characters per URL/redirect, 256 per content-type field, 200 per raw
title, and three bounded private errors (phase 16, category 64, detail 256).
Discovered URLs must be absolute HTTP(S) DNS-host URLs, correctly tagged as I2P
or clearnet and uniquely sorted. Credentials, paths, queries and fragments can
exist in untrusted input URLs; they are never propagated to public output.
Private errors are never interpreted as public categories. There is no repair,
deduplication or reordering of malformed raw evidence.

## Public version 1 allowlist

`I2pPublicScan` is a separate immutable model, not raw-model serialization with
ignored properties. UTF-8 JSON uses camel-case names, string enums, explicit nulls,
fixed property order and indented formatting. The top-level fields are exactly:

```text
schema: "i2patlas.public-scan"
version: 1
scanStartedAtUtc: UTC timestamp
scanCompletedAtUtc: UTC timestamp
seedCount: number of projected seed entries
uniqueHostnameCount: distinct normalized friendly hostnames
entries: array ordered ordinally by hostname, then destinationHostname
```

Each entry contains exactly:

```text
hostname: normalized friendly .i2p name
destinationHostname: destination-derived .b32.i2p name
destinationsForHostname: distinct B32 identities for this friendly name
summaryState: I2pPublicSummaryState string
robotsRequestOutcome: I2pRequestOutcome string or null
robotsProxyClassification: I2pProxyResponseClassification string
robotsObservation: I2pRobotsObservation string
robotsStatusCode: integer or null
homepageRequestAttempted: boolean or null (unknown probe state)
homepageRequestOutcome: I2pRequestOutcome string or null
homepageProxyClassification: I2pProxyResponseClassification string or null
homepageStatusCode: integer or null
homepageContentType: lowercase bounded media type only, or null
title: bounded plain text or null
discoveredI2pHostnames: sorted unique normalized hostnames
linkExtractionIncomplete: boolean
```

Separate seed entries survive even for one friendly hostname; none is preferred.
Base64 destination text is a preserved private representation; semantic identity
is hostname plus derived B32. The raw reader rejects duplicate semantic identities,
including equivalent Base64 spellings. The projector also rejects duplicate public
identities rather than merging scanned observations. `destinationsForHostname`
counts distinct B32 identities rather than spellings.

Summary mapping uses this precedence:

| Condition | Summary |
| --- | --- |
| Homepage HTTP response present, including error/redirect status | `HomepageResponseObserved` |
| Homepage attempted without HTTP response | `HomepageAttemptFailed` |
| Robots explicitly disallows root | `RobotsDisallowed` |
| Robots classification is likely proxy failure | `ProxyFailureObserved` |
| Robots request timed out | `RequestTimedOut` |
| Robots transport failure or unavailable proxy | `TransportFailure` |
| Robots redirect observed | `RobotsRedirect` |
| Remaining/unknown observations | `Ambiguous` |

Outcomes, classifications, robots observations, attempt state and HTTP statuses
remain separately exposed. A homepage timeout is `HomepageAttemptFailed` with its
`TimedOut` outcome retained. HTTP responses remain unauthenticated observations
through a proxy. No state claims reachability, liveness, verification or ownership.

Only raw links tagged `I2p` contribute; the existing hostname validator normalizes
their URI host. Only that hostname survives: no scheme, port, credentials, path,
query, helper value, fragment or clearnet URL is copied. B32 names remain valid
I2P hostnames. Results deduplicate and sort ordinally. Extraction is marked
incomplete if metadata is absent, marked incomplete, or body-truncated.

Public titles have a stricter 160 UTF-16 character bound without splitting Unicode
scalars. Control/format characters and line/paragraph separators are removed,
edge whitespace is trimmed, and empty titles become null. Markup is retained as
literal text, never interpreted. Default JSON escaping is enabled. **The viewer
HTML-escapes titles again; projection is not an HTML-escaping boundary.** Invalid
or oversized media types become null; valid types lose all parameters.

## Privacy and installation

Everything absent from the explicit model stays private. This includes Base64
destinations; addressbook/snapshot paths, hashes and provenance byte counts;
proxy address/port; scan settings; per-seed timestamps/durations; private errors;
redirect URLs; full discovered URLs; clearnet links; and raw metadata/annotations.
No arbitrary dictionary or raw object is copied. The public model intentionally
exposes friendly names, B32 identities, scan-level UTC times, observations, titles
and discovered I2P hostnames; raw privacy does not imply these are automatically
approved for another publication format. This command creates only the selected
local JSON file, with no deployment or web-server configuration.

After validation/projection, serialization uses a unique `.i2p-public-*.tmp` in
the output directory. The existing `PrivateFile` helper protects staging (0600 on
Unix, inherited ACLs on Windows). Output is capped at 128 MiB. Complete serialization,
async flush, disk flush and close precede same-filesystem replacement. Validation,
serialization, I/O and cancellation failures preserve previous output and remove
owned staging files. Filesystem aliases not represented as reparse points (for
example hard links) and simultaneous writers are outside these path checks;
replacement itself does not truncate the old file. A crash can leave a staging
file; replacement is not a multi-file or power-loss durability guarantee.

## Public crawl v1/v2

New crawl output uses raw/public version **2**. Projection retains the input
version, so existing raw v1 files still produce public v1. Both strict readers
continue accepting their original v1 shapes without repair or migration.

Public v2 retains the top-level v1 fields and KnownSeed shape below. Discovered
entries store depth 1..3 and sort by depth then ordinal hostname, globally unique.
At depth 1, `referrerHostname` + `referrerDestinationHostname` identify a KnownSeed.
At depth 2/3, the exact shape instead has `referrerHostname` + `referrerHostForm`
(`Friendly` or `ClassicBase32`) identifying a DiscoveredHost at depth N-1. The
destination field is absent, not inferred. Each parent must record a complete
HTTP-200 HTML/XHTML homepage observation with the child in its public hostname evidence.
The reader rejects absent/same-wave/skipped-depth/cyclic or mismatched-form
parents. Public v2 permits at most depth 3; the private reader also enforces the
configured maximum and whole-wave timestamp barriers.

The following v1 contract is preserved. There is no v2-to-v1 downgrade command.

The separate local crawl boundary is:

```text
private i2patlas.raw-crawl v1
    -> i2p-crawl-project
    -> public i2patlas.public-crawl v1

dotnet run --project I2PAtlas -- i2p-crawl-project private/results/i2p-crawl.json published/i2p-crawl.json
```

`I2pCrawlProjectionService` uses only the existing strict `I2pRawCrawlReader` for
input. It does not reopen the snapshot/addressbook, construct a transport, resolve
DNS or make requests. The existing raw-scan/public-scan schemas and
scan/viewer pipeline are unchanged. The separate `i2p-crawl-view` command now
generates standalone inspection HTML from validated public-crawl v1/v2. `i2p-view`
accepts only public-scan v1, and `i2p-project` accepts only raw-scan v1. Neither
existing command accepts either crawl schema. See [reporting](../I2pReporting/README.md#standalone-crawl-inspection)
for the completed local pipeline; no hosting/deployment is introduced.

The new immutable `I2pPublicCrawl` has exactly these top-level fields:

```text
schema: "i2patlas.public-crawl"
version: 1
crawlStartedAtUtc: explicit UTC timestamp
crawlCompletedAtUtc: explicit UTC timestamp, >= start
knownSeedCount: 0..10000
discoveredHostCount: 0..1000
entries: at most 11000 entries, counts must match their kinds
```

Entries use a closed hierarchy with exact kind-specific property sets:

```text
KnownSeed:
    kind: "KnownSeed"
    hostname: normalized .i2p name
    destinationHostname: canonical 52-character hash label + ".b32.i2p"
    depth: 0
    observation: public observation object below

DiscoveredHost:
    kind: "DiscoveredHost"
    hostname: literal normalized discovered .i2p name
    hostForm: "Friendly" or "ClassicBase32"
    depth: 1
    referrerHostname: known parent hostname
    referrerDestinationHostname: known parent's destination-derived B32
    observation: public observation object below
```

The referrer pair must identify exactly one KnownSeed entry. Its B32 distinguishes
alternate destinations for one friendly name. It never identifies the child.
Friendly discovered entries have no destinationHostname; classic B32 references
retain their literal hostname and also have no separate destination field.
Discovered names use the existing host-only target validator, including control
hostname exclusions and canonical classic B32 checks. Known semantic identities
remain hostname+B32. Known entries come first, ordered ordinally by hostname then
B32; discovered entries follow in unique hostname order. Alternate known
destinations survive without a preferred mapping. Discovered identities cannot
duplicate selected known friendly/B32 names.

Both entry kinds share exactly this separate public observation object:

```text
summaryState: existing I2pPublicSummaryState name
robotsRequestOutcome: existing I2pRequestOutcome name or null
robotsProxyClassification: existing I2pProxyResponseClassification name
robotsObservation: existing I2pRobotsObservation name
robotsStatusCode: integer 100..999 or null
homepageRequestAttempted: boolean or null for unknown partial state
homepageRequestOutcome: existing I2pRequestOutcome name or null
homepageProxyClassification: existing I2pProxyResponseClassification name or null
homepageStatusCode: integer 100..999 or null
homepageContentType: sanitized lowercase media type, at most 80 characters, or null
title: sanitized plain text, at most 160 UTF-16 characters, or null
discoveredI2pHostnames: at most 250 sorted unique normalized .i2p hostnames
linkExtractionIncomplete: boolean
```

`I2pPublicProjectionPolicy` shares the existing scan summary precedence,
title/media-type sanitation and safe hostname-only metadata projection. These
observations never claim origin authentication, liveness or ownership. A public
hostname list is retained evidence, not work or a destination-resolution claim.

Everything else stays private: Base64 destinations, snapshot/source paths,
hashes/byte lengths/import time, proxy identity, settings, private errors,
individual request timestamps/durations, raw metrics, full discovered URLs,
redirect locations, clearnet links, headers and raw objects. Only the crawl-level
UTC interval is exposed; no request timings are copied. Plain titles may contain
literal markup, which the crawl viewer HTML-encodes again at render time.

`I2pPublicCrawlReader` enforces strict UTF-8 without BOM, JSON depth <=8 and a
128 MiB byte limit before and during reading. Every object requires its exact
property set, including explicit nullable fields. Unknown/duplicate/missing
properties, malformed enum names/types, non-normalized hostnames, invalid B32,
wrong kind/depth/form/referrer combinations, duplicate/unordered identities,
invalid counts/timestamps/metadata and inconsistent observations are rejected.
HTTP outcomes must match statuses, classifications must match their recorded
status/robots role, and homepage attempts require the recorded robots gate.
Unknown state stays null; no input is repaired, sorted or canonicalized.
Structural validation cannot establish proxy response authenticity.

Both paths must be explicit local files and differ. The output directory must
exist; URL, UNC/device paths and filesystem reparse points/ancestors reject using
the existing projection policy. A unique private `.i2p-public-crawl-*.tmp` is
created beside the output. Serialization caps at 128 MiB, followed by async flush,
disk flush and close. The new strict public reader validates the staged bytes
before cancellation is checked and replacement occurs. Failure before replacement
preserves previous output and cleans the owned temporary file. The existing
hard-link/concurrent-writer/crash limitations above also apply.

Exit codes: 0 installed, 2 bad arguments/paths, 1 invalid raw/public artifact,
resource or I/O failure, 130 cancellation. Ctrl+C requests cancellation.
Diagnostics expose bounded counts and generic errors, not paths or private values.
