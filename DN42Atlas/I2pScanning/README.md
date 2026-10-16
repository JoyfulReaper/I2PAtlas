# Private bounded I2P scans

```text
dotnet run --project DN42Atlas -- i2p-scan private/i2p-seeds.json private/results/i2p-scan-20261016-120000.json http://127.0.0.1:4444
```

Exact syntax:

```text
i2p-scan <snapshot-file> <private-output-file> <proxy-uri> [--concurrency <1-4>] [--timeout-ms <1-60000>] [--max-seeds <1-10000>]
```

All three positional arguments are required. Quote paths containing spaces. The
output directory must already exist. Relative paths use the working directory.
The proxy must pass the existing literal-loopback HTTP endpoint validation; there
is no default or ambient proxy. Default concurrency is **2**, with a hard cap of
4, to limit simultaneous work while allowing for I2P latency. Default timeout is
10000 ms per request, and default maximum seeds is 10000. Duplicate/unknown
options and values outside these bounds fail. `--max-seeds` selects the first N
normalized hostnames in ordinal order, after the entire snapshot is validated.

The public service API is `I2pScanService.ScanAsync(snapshotPath, outputPath,
proxyUri, settings, cancellationToken)`. CLI dispatch precedes DN42 policy,
registry, scanner, operation lock and publication setup. The command reads no
addressbook, registry, DNS or DN42 resolution artifact.

## Input identity and bounded work

Only `SeedSnapshotReader.ReadWithIdentityAsync` loads input. It performs the same
strict, local, bounded validation as `ReadAsync`, and returns SHA-256 and byte
length for the exact buffered snapshot bytes validated on that read. It does not
reopen the path to hash it. This hash includes JSON whitespace and escaping and
is distinct from the snapshot's original addressbook hash. Recorded addressbook
paths are never opened; local paths are omitted from the raw provenance.

The immutable snapshot is never edited. Exactly `min(concurrency, selected count)`
worker tasks claim indices from that fixed seed array. A worker finishes one
seed before claiming another. There is no task per seed waiting on a semaphore,
unbounded work queue, scheduling, retry loop or discovered-link expansion.
Stable index slots preserve normalized hostname order regardless of completion
order. Results are frozen into an ImmutableArray after all workers finish.

Workers share the existing fixed-proxy transport and stateless `I2pHttpProbe`.
Each seed retains the [existing robots/homepage authorization policy](../I2pProbing/README.md):
only likely-origin robots plus AllowsRoot authorizes one root request. Request
and response bounds remain unchanged: 16 KiB headers, 256 KiB buffered body,
64 KiB robots, 200-character title, 250 inert discovered links. No redirect,
query, helper, DNS, direct connection, clearnet fallback or recursive request is
introduced. The HTTP runtime may retry a failed pooled connection as already
documented by the transport; orchestration adds no retries.

Expected network failures remain per-seed observations. Timeout, connection
refusal and HTTP errors do not abort other seeds. An unexpected probe exception
is isolated into `UnexpectedProbeFailure` with unknown request state (nullable
outcome/attempt/disposition); exception messages and stack traces are not stored.
The result never guesses that a partial request was or was not sent. Out-of-memory
and orchestration/storage failures remain global failures.

The snapshot reader caps bytes at 32 MiB and entries at 10000. Retained result
text has an aggregate 32 Mi-character budget (counting all private identities,
response strings, titles, links and fixed errors), plus at most four in-flight
bounded probe results. Serialized output is capped at 128 MiB. These resource
failures abort the scan and preserve the previous output; use `--max-seeds` for a
smaller run. This version keeps results in memory rather than checkpointing.

Caller cancellation cancels in-flight requests and stops workers from claiming
new work. The API throws OperationCanceledException. CLI Ctrl+C signals that
token and exits 130 without installing a new final artifact. A cancellation
observed before the atomic replacement prevents commit; replacement itself is
the completion boundary, not a cancellable multi-file transaction.

## Version 1 raw artifact

The format is UTF-8 JSON with camel-case fields and string enums. The root has:

- `schema`: exactly `i2patlas.raw-scan`; `version`: integer 1.
- `startedAtUtc`, `completedAtUtc`: scan wall-clock timestamps in UTC.
- `seedInput`: seed schema/version, exact snapshot `fileSha256`/`fileByteLength`,
  snapshot import UTC, and original `addressBookSha256`/`addressBookByteLength`.
- `proxy`: canonical literal loopback `address` and `port`, without credentials.
- `settings`: concurrency, per-request timeout milliseconds, maximum seeds.
- `snapshotSeedCount`, `totalSeeds`, `seedLimitApplied`: distinguish a selected
  diagnostic prefix from the full snapshot.
- `results`: immutable per-seed evidence, sorted by hostname.
- `metrics`: diagnostic aggregates described below.

Each result preserves `hostname`, exact validated `destination`, derived
`destinationHostname`, UTC start/completion and monotonic duration milliseconds;
separate robots outcome/evidence/classification/observation/HTTP response; separate
homepage attempt/disposition/outcome/evidence/classification/HTTP response;
`homepageMetadata` with bounded title/content type/body accounting/discovered
links; and bounded `errors`. Response records contain status, content type, body
byte count, redirect location and its truncation flag. Bodies, arbitrary headers
and exception text are not persisted. At most two errors (one per HTTP phase),
or one unexpected-error record, contain fixed phase/category/detail text and
numeric HTTP status. There is no Reachable flag or authenticated-origin claim.

The constructor enforces the supported raw schema/version and basic envelope
counts. JSON round-trip/unsupported-version tests cover that envelope contract;
this is **not a general validated reader for untrusted raw artifacts**. A future
offline publisher must introduce full raw-input validation, size limits and an
explicit projection rather than treating a JSON deserialize as trust.

`metrics` contains total monotonic scan duration, sorted counts by robots request
outcome/classification and homepage outcome/classification, homepage attempted
and HTTP-response counts, unexpected probe failures and maximum active seeds.
Missing count keys mean zero. `NotAttempted` distinguishes a known skipped
homepage from `NotObserved` (no classification/unknown state). Every counts map
accounts for all selected seeds. HTTP response count includes HTTP error pages;
it is not an origin-reachability metric. Duration includes input loading and
probing, excludes final serialization, and does not rely on wall-clock ordering.

## Private storage and failure safety

**The entire artifact is private.** Destinations are public-key identities, but
their mappings, Base32 identities, hashes, timestamps, proxy/settings, failures,
titles, redirects and URLs remain private operational provenance here. Raw
response metadata is untrusted and may contain control characters, URL tokens or
other sensitive text. No field is automatically suitable for publication. A
future public projection must apply publication/exclusion policy, omit private
identities and operations, and escape/filter text and URLs explicitly. Heuristic
classification and Unproven evidence must remain distinct from reachability.

Explicit input/output paths must be local ordinary files, not URLs, UNC/device
paths, the same normalized path or filesystem links/reparse-point ancestors.
Output path components named `published`, `public`, `wwwroot` or `site` are
rejected as known served-root names. These checks do not discover arbitrary web
server configuration: keep private output outside any other served directory too.
No public file or directory is generated and no DN42 publisher/viewer is invoked.

After successful probing, a unique `.i2p-scan-*.tmp` file is created in the output
directory with the existing PrivateFile helper (0600 on Unix; inherited directory
ACLs on Windows). Complete serialization, async flush, disk flush and close precede
same-filesystem replacement of the final path. Input, worker, resource, encoding,
serialization, cancellation and replacement failures preserve previous output.
Owned temporary files are removed on normal failure. A process/OS crash may leave
a private temporary file; this is not a power-loss durability guarantee. No final
partial artifact is installed and no checkpoint/public artifact is written.

Exit codes: **0** means the complete private artifact was installed, even if all
seeds failed or the snapshot was empty; **1** means a global scan/input/output
failure; **2** means invalid arguments/configuration/unsafe paths; **130** means
caller cancellation before commit. Stdout contains only bounded aggregate counts
and duration, never seed identities, destinations, hashes, paths or raw errors.
If reporting to stdout fails after commit, stderr distinguishes that case from
failure to install the artifact. Tests use synthetic destinations and concurrent
local fake proxies, never a real I2P router.
