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
  exact recomputation of the B32 identity through the existing pure request-target
  derivation. No HTTP transport is constructed.
- Strict ordinal hostname/destination-text result ordering, no duplicate pairs,
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
Exact destination spellings are seed identities privately; optional Base64 padding
can produce different private pairs with the same B32. Those entries remain, while
`destinationsForHostname` counts distinct B32 identities rather than spellings.

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
