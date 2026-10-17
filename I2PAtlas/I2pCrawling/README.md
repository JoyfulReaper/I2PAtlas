# Private bounded breadth-first crawling

`i2p-crawl` runs a private depth-0 seed wave followed by bounded host-only waves.
Maximum depth defaults to 1 and can explicitly be raised to 2 or 3.
The frontier selector remains a pure offline component.
The separate local `i2p-crawl-project` command now projects validated raw-crawl
into public-crawl of the same version, then `i2p-crawl-view` generates standalone inspection HTML. Existing `i2p-project` and
`i2p-view` reject crawl input. Nothing below authenticates response origin.

```text
dotnet run --project I2PAtlas -- i2p-crawl <snapshot-file> <private-output-file> <proxy-uri> [--concurrency <1-4>] [--timeout-ms <1-60000>] [--max-seeds <1-10000>] [--max-discovered-hosts <0-1000>] [--max-depth <1-3>]
```

Defaults are concurrency 2, request deadline 10000 ms, at most 10000 known seeds
and 100 discovered hosts across the entire crawl, with maximum depth 1.
Output directories must exist. Exit codes are 0 for installed output, 2 for invalid
arguments, 1 for validation/I/O/infrastructure failures and 130 for caller
cancellation. Individual target failures remain observations and do not fail the
command. Console output contains only aggregate counts/duration and an
unauthenticated-response warning, without identities, URLs or private error text.

Progress goes to stderr; the final completion summary stays on stdout. The service
reports immutable aggregate events without a Console dependency. The CLI prints
wave starts, every 25 completions or a completion after 20 seconds since the last
line, and each barrier's probed/reference/admission counts. Counts of homepage
attempts/responses are cumulative across waves. There is no independent heartbeat
while requests are pending. A nonfatal callback/writer exception disables further
progress reporting for the run; it does not fail the crawl or affect installation.

## Orchestration

`I2pCrawlService` strictly loads and hashes the snapshot once using
`SeedSnapshotReader.ReadWithIdentityAsync`. It selects the first MaximumSeeds
entries in existing hostname-then-preserved-destination-text order. Each semantic
seed becomes its own `KnownSeed` probe, including alternate destinations.
Exactly `min(concurrency, wave target count)` workers claim stable result slots;
there is no task per seed/host waiting for capacity.

After every worker in depth N finishes, the selector can populate depth N+1, up
to the configured maximum depth. Every selection suppresses the full snapshot's
friendly/B32 names, all earlier admitted hosts and duplicates in this frontier.
Each discovered hostname is probed at most once; known semantic seeds still
retain alternate destinations. Admission takes the ordinally smallest hosts from
that wave with the remaining GLOBAL discovered-host budget, never a new per-wave
allowance. Referrer ties use the smallest immediate parent identity.
The same worker limit, single guarded transport and shared probe serve all waves.
Discovered requests use only `/robots.txt` and conditional `/`, never original
paths/queries/fragments. Links at max-depth remain evidence and never populate a
frontier. Zero remaining host capacity still records eligible reference/limit
metrics at a nonterminal barrier without making another wave's requests.

Host-only observations have no destination pinning claim. Friendly hosts depend
on the configured proxy/router addressbook; classic B32 hosts retain their
literal canonical name. No local target DNS or direct connector is added.

Expected network failures stay observations. Unexpected nonfatal probe exceptions
become fixed `UnexpectedProbeFailure` evidence with unknown partial state and no
exception text/stack/path. Resource exhaustion is not converted into an
observation. Infrastructure failure cancels siblings and aborts installation;
caller cancellation also preserves previous output. There are no application retries.

## Private raw-crawl v1/v2 compatibility

New crawls always emit `i2patlas.raw-crawl` version **2**, including the default
depth-1 run. Its shape extends v1 below with required `settings.maximumDepth`
(1..3), stored discovered depths (1..maximumDepth), and discovered results ordered
by depth then ordinal hostname, globally unique. Known entries still have depth 0.
Depth-1 referrers retain `{ hostname, destinationHostname }`. Deeper referrers use
exactly `{ hostname, hostForm }`, identifying an admitted discovered parent at
the immediately preceding depth. There is no destination field for that parent.
Every child must start after the whole preceding wave completes and have eligible
link evidence in its immediate parent. Missing/cyclic/skipped-depth/wrong-form
chains reject. Metrics cover all waves; maximum active probes is bounded by
concurrency and the largest individual wave. Discovery reference counts sum only
nonterminal selections, excluding full-snapshot/prior-admission suppression.

The reader still accepts strict v1 with its original four settings, depth-1-only
results, ordering and known-parent shape. Projecting v1 emits public v1; v2 emits
public v2. Existing one-hop artifacts need no migration. No downgrade from v2 to
v1 or reconstruction of deeper history from old files is provided.

The preserved v1 shape has schema `i2patlas.raw-crawl`, version 1 and these fields:

- `crawlStartedAtUtc`, `crawlCompletedAtUtc`.
- `snapshot`: seed schema/version, exact snapshot-file SHA-256/byte length,
  import UTC time and original addressbook source SHA-256/byte length.
- `proxy`: canonical loopback address and port, without credentials.
- `settings`: concurrency, requestTimeoutMilliseconds, maximumSeeds,
  maximumDiscoveredHosts.
- `snapshotSeedCount`, `selectedSeedCount`, `seedLimitApplied`.
- `discovery`: eligibleReferenceCount, admittedHostCount, admissionLimitApplied.
- `knownSeedResults`, `discoveredHostResults`, `metrics`.

Known results contain hostname, preserved Base64 destination, recomputed
destinationHostname, depth 0 and `evidence`. They retain snapshot order (hostname
then preserved destination text, ordinal), with unique hostname+B32 identities.
Discovered results contain only normalized hostname, hostForm (`Friendly` or
`ClassicBase32`), depth 1, `referrer` and `evidence`, ordered by hostname ordinally.
Referrer is a structured `{ hostname, destinationHostname }` selected-known-seed
identity; it contains no referring URL. Discovered results have no destination
field or inferred B32 identity for friendly hosts.

Both evidence shapes retain UTC times/duration, separate robots/homepage
outcomes, origin evidence, classifications, robots observation, homepage
attempt/disposition, bounded response metadata, page metadata and fixed error
categories/details. There are no bodies, arbitrary headers or exception messages.
Metrics retain total duration, outcome/classification dictionaries across all
waves, homepage attempts/responses, unexpected failures and maximum active probes.

The **entire artifact is private**, including destinations, identities, URLs,
errors, proxy settings, timings and hashes. Existing raw-scan/public-scan schemas and
their projection/viewer contracts are unchanged.

## Local public projection

```text
private i2patlas.raw-crawl v1 or v2
    -> i2p-crawl-project <private-raw-crawl-file> <public-output-file>
    -> public i2patlas.public-crawl of the same version
```

This boundary performs no networking or further crawling. It validates with the
existing strict raw reader, then copies an explicit public allowlist into a
separate immutable model. Known seeds expose hostname+B32; discovered hosts expose
only their literal normalized hostname/form and immediate parent identity. The
parent B32 is necessary to distinguish alternate known destinations, and is never
an inferred identity for the child. Public observations remain unauthenticated.

Public data includes crawl-level UTC times, counts, observation enums/statuses,
bounded sanitized titles/media types and sorted I2P hostname lists. It excludes
destinations, snapshot/source provenance, proxy identity, settings, private errors,
durations, raw metrics, redirects and full URLs. The strict public reader and
staged writer enforce the public schema before replacement. See
[publishing](../I2pPublishing/README.md#public-crawl-v1v2) for exact fields and limits.
The separate `i2p-crawl-view` command accepts public-crawl v1/v2 for local inspection;
existing scan projection/viewer commands do not accept raw-crawl or public-crawl data.

## Bounds and strict installation

The hard retained-evidence budget is 32 Mi UTF-16 characters across all waves:
known/discovered identity text, referrers, titles, content types, URLs, redirects
and errors. Exceeding it fails the whole run, without retaining a completion-order
dependent subset. Existing per-response/title/link bounds remain unchanged.
Artifact serialization and reading each cap at 128 MiB; JSON depth caps at 16.

`I2pRawCrawlReader` requires strict UTF-8, exact required property sets at every
level, unique properties, exact enum strings, schema/version, UTC time ordering,
bounded values/settings and consistent counts. It reconstructs known semantic
identities, revalidates discovered targets through Crawler 0, checks host forms,
depths, unique ordering, selected referrer membership and the wave time barrier.
It checks request/response/classification/homepage-gate consistency, metadata,
canonical sanitized errors, aggregate evidence and all recomputable metrics.
It never repairs, merges or sorts malformed input. Hashes are bounded provenance,
not authentication. Without original bodies/full snapshot it cannot rerun robots
parsing or prove the original frontier's reference count/full suppression set.

`I2pRawCrawlFile` rejects snapshot/output collisions, filesystem links and known
served-root components (`published`, `public`, `wwwroot`, `site`). It stages through
exclusive `PrivateFile` creation (Unix 0600; inherited Windows directory ACLs),
serializes within the byte cap, flushes to disk and closes. The strict reader
validates the staged bytes before cancellation is checked and the final path is
replaced. Validation, budget, I/O or cancellation failures before replacement
preserve previous output and clean owned staging files. This is a same-filesystem
per-file replacement, not a crash-proof transaction or multi-writer lock.

## Pure frontier selection

The compatible `I2pHostDiscovery.Select` entry point receives a complete validated `I2pSeedSnapshot` and an
immutable array of `I2pCrawlSeedObservation` pairs. These pairs explicitly identify
the selected/probed known seeds and their completed observations; snapshot members
without a supplied observation provide no discovery evidence. Selected parents
must be distinct semantic snapshot members, using hostname plus derived B32.
Different destinations for one friendly hostname remain separate parents.

A parent contributes only when its homepage was attempted, its outcome is
`ProxyHttpResponse`, its response status is 200, metadata exists, and both
truncation/incomplete flags are false. Response and metadata Content-Type must
both parse as the same explicit `text/html` or `application/xhtml+xml` media type.
Media types compare case-insensitively; syntactically valid parameters, including
charset, are ignored. No response/robots classification is promoted to origin
proof. The existing probe supplies metadata only for uncompressed HTTP 200;
observations do not retain Content-Encoding or bodies, so this layer does not
reconstruct that extraction decision or validate arbitrary persisted evidence.

Only existing I2P-kind URL observations contribute candidates. They must be
absolute HTTP URLs on effective port 80, without userinfo, unsafe whitespace,
controls, backslashes or an `i2paddresshelper` parameter (including encoded and
case variants). HTTPS and other ports remain inert. The raw hostname is passed
to Crawler 0's `DiscoveredHost` validator: control-host, hostname and classic-B32
rules are shared, never reimplemented. Only the normalized host survives; no
original URL/path/query/fragment enters a work item.

The entire snapshot supplies suppression indexes for friendly hostnames and all
destination-derived B32 hostnames, including unselected seeds. Unknown friendly
and B32 references are not inferred to be aliases. Work identity is normalized
hostname within this selection; it is not a cross-run destination claim.

`I2pCrawlDiscoverySettings.MaximumDiscoveredHosts` defaults to 100 and permits
0–1000. A capped ordered set plus dictionary retains the globally smallest K
unknown hosts ordinally. A smaller arrival replaces the largest at capacity;
there is no rejected-host set. Each retained host keeps only its smallest ordinal
parent identity (friendly hostname, then B32), independent of input/completion
order. The admission threshold only decreases: an evicted host cannot later be
in the final smallest K, so its discarded provenance cannot affect the result.
Output is immutable and ordinally sorted. `SelectWave` generalizes this algorithm
to one completed known-seed or discovered-host wave, with earlier admissions and
the remaining global budget supplied by orchestration. Each work item stores its
next-wave depth and immediate known-seed or host-only referrer.

`EligibleUnknownReferenceCount` counts eligible unknown URL observations, including
repeated paths and references across parents, before host deduplication. It is not
a unique-host count. `AdmissionLimitApplied` means at least one distinct eligible
unknown host could not be retained, including eviction. At K=0 it is true when
any eligible unknown reference exists, while the frontier remains empty.

Input bounds remain 10000 snapshot/selected seeds, 1000 discovered hosts globally and 250 links per parent; default
arrays, excess link counts, duplicate parents and out-of-snapshot parents fail
explicitly. At most 2,750,000 references can be examined across selections. URL and Content-Type
length limits reuse 4096 and 256 characters. Retained selection state is O(K),
in addition to bounded snapshot/parent identity indexes and caller-owned inputs.
Cancellation is supported. Within this selector there is no HTTP, DNS, socket, filesystem, environment
or proxy lookup, and no task per candidate.
