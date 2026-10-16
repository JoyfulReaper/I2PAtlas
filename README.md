# DN42Atlas

DN42Atlas is a small .NET 10 tool for discovering and browsing publicly reachable services on the [DN42](https://dn42.dev/) network.

It starts with registered `.dn42` domains, resolves them through a DN42-aware DNS resolver, probes a curated set of HTTP/HTTPS ports, respects `robots.txt`, and records basic metadata such as page titles, redirects, links, and referenced `.dn42` hostnames.

It can also turn a scan result into a self-contained HTML viewer for casually flipping through public DN42 services.

This is primarily a hobby project for exploring DN42 and learning more about routing, service discovery, and the weird little corners of private networks.

The site remains in private preview. Public service listings will not launch before **October 16, 2026**. Authenticated self-service at `/operator` and manual requests at `/contact` are live; the OptOut application runs persistently under systemd. The crawler can generate artifacts independently of this launch gate, so keep generated listings separate from the served preview site until an explicit launch decision.

## What It Does

DN42Atlas currently has three main stages:

1. Parse DN42 registry DNS objects.
2. Resolve registered `.dn42` names.
3. Probe reachable web services and optionally inspect their homepage.

For web services, Atlas records information such as:

- hostname
- scheme and port
- whether the origin responded
- `robots.txt` status
- homepage HTTP status
- page title
- content type
- redirects
- discovered links
- `.dn42` hostname mentions
- truncation status for large pages/link sets

Scan results are stored as JSON.

DN42Atlas can then ingest that JSON and generate a self-contained HTML browser for the results.

## Polite Discovery

DN42Atlas is intended to be a relatively conservative discovery tool, not a vulnerability scanner.

For each potential HTTP/HTTPS origin it:

1. Requests `/robots.txt`.
2. Does not automatically follow redirects.
3. Fetches `/` only when robots rules explicitly permit it or no robots file exists.
4. Does not recursively crawl discovered links.
5. Does not attempt authentication.
6. Does not brute-force paths.
7. Does not perform vulnerability checks.
8. Uses bounded concurrency and short timeouts.
9. Limits the amount of homepage content it reads.

The current scanner identifies itself as:

```text
DN42Atlas/0.2 (+https://dn42atlas.dn42/)
```

If `robots.txt` cannot be evaluated safely, Atlas does not fetch the homepage.

Discovered links are recorded for later analysis but are not automatically followed.

## Complete Exclusion

Atlas loads two required files from the current working directory before executing any resolution, scan, probe, or report command. Help and unknown commands do not load configuration or perform network work:

```text
config/excluded-hosts.txt
config/excluded-prefixes.txt
```

Keep both files present, even if they contain no rules. If either file is missing, Atlas fails closed: it stops rather than continuing without exclusions. Malformed CIDR rules also stop startup.

Deployments with self-service exclusions can additionally set `DN42ATLAS_RUNTIME_EXCLUSIONS_PATH` to the versioned JSON policy materialized by `DN42Atlas.OptOut`. Atlas combines and deduplicates its active exact-host and CIDR rules with the required manual files. If the variable is not set, manual-only behavior is unchanged. If it is set, the runtime file is required: a missing, unreadable, malformed, unsupported, or invalid bundle stops resolution, scanning, probing, and reporting rather than silently ignoring active exclusions. The crawler never reads the private OptOut SQLite database directly; see `DN42Atlas.OptOut/README.md` for initialization and recovery commands.

Use one rule per line. Blank lines are ignored, and `#` starts a comment, including after a rule.

Hostname rules go in `config/excluded-hosts.txt`, for example:

```text
example.dn42        # this exact hostname
*.private.dn42     # descendants of private.dn42
```

Exact rules match only the named host. Wildcard rules match both `foo.private.dn42` and `bar.foo.private.dn42`, but do not match the bare parent `private.dn42`. Add a separate exact rule to exclude the parent too. Matching ignores case, surrounding whitespace, and a trailing dot.

IPv4 and IPv6 CIDR rules go in `config/excluded-prefixes.txt`, for example:

```text
172.20.16.0/20
fd42:1234::/48
```

During the default registry/DNS command, hostname exclusions are checked before DNS resolution. If any resolved address matches an excluded prefix, the entire domain is omitted from `domain-resolution.json`.

`web-scan` checks hostname exclusions again when loading an existing resolution file. It then checks every saved address against prefix exclusions before probing. A hostname match or any matching address skips the entire domain: excluded scan targets are not probed and do not appear as service results in the scan JSON or its generated viewer.

The public scan JSON records `ExcludedByHostname` and `ExcludedByPrefix` counts, without publishing the matched excluded hostnames or prefix rules. Excluded links and hostname mentions are removed; excluded identifiers in other metadata are redacted. This filtering does not resolve or probe discovered references. Console diagnostics can include the matched hostname, address, and rule. Historical JSON in `results/` is a private raw snapshot: target exclusions still prevent probing, but reference redaction is applied to reports and public artifacts rather than altering that source snapshot. Do not serve the historical directory.

`robots.txt` controls HTTP page-content fetching. Atlas still requests `/robots.txt` and can record the origin and robots status when homepage fetching is disallowed. Complete exclusion removes the target from the scan entirely, before those HTTP requests.

Authenticated self-service supports exact registered domains and IPv4/IPv6 allocations owned by the active Auth42 maintainer. Auth42 establishes identity; the local registry's `mnt-by` determines authority. Stale, dirty, or unknown registry snapshots disable automatic mutations. Approved exclusions update the private audit database, runtime bundle, and current filtered artifacts without another crawl. **Include again** revokes a self-service exclusion and regenerates from the recorded raw scan; manual policy always wins. Broader/wildcard requests and ownership problems belong in the manual form. See the [OptOut guide](DN42Atlas.OptOut/README.md) for confirmations, recovery and configuration.

`probe-test` also checks hostname exclusions before DNS and validates the entire resolved address set before probing. Raw web scan results include `ProbeAddresses`, a string array containing the complete approved/pinned destination set at scan time, including for failed probes. Addresses are normalized, deduplicated, and ordered with an ordinal string comparison; IPv4-mapped IPv6 normalizes to IPv4 consistently with exclusion matching. This is historical provenance, not the single address that happened to connect or live DNS state.

`report` and public generation remove an entire service result when its hostname is excluded or any recorded `ProbeAddresses` address matches an excluded prefix. Both IPv4 and IPv6 CIDRs are supported; address families must match after IPv4-mapped normalization. After filtering decisions, `ProbeAddresses` is removed from every retained result: it stays private in the raw scan and is not emitted in public JSON or embedded HTML models. References in retained results are also filtered/redacted. No DNS lookup or probe is performed during publication or reporting, and source bytes and scan timestamps remain unchanged. Public artifacts omit this evidence and cannot substitute for the immutable raw scan when re-filtering under prefix policy.

Legacy scans without destination provenance remain readable and publishable with no prefix exclusions, including hostname-only policy. With any active manual or runtime prefix exclusion, every retained result must have a non-empty array of valid destination IP addresses. Missing, empty, malformed, or partially missing provenance fails closed instead of guessing historical destinations from current DNS. Publication withdraws `published/index.html` and `published/latest.json` where possible, preserves support pages and private publication state, and returns failure. Exclusion reconciliation keeps the recorded runtime exclusion effective for future crawling and reports the withdrawn listing. A new provenance-aware scan is needed for safe prefix re-filtering; old raw files are never retrofitted or modified.

## DN42 Address Guard

Before probing a hostname from a resolution file, `web-scan` requires a valid DNS hostname ending in `.dn42` and verifies that all its saved addresses fall within expected DN42 address space. Conflicting duplicate rows cannot override an exclusion or an unsafe address set.

Currently accepted ranges include:

```text
172.20.0.0/14
172.31.0.0/16
10.100.0.0/14
10.127.0.0/16
fd00::/8
```

Domains with any address outside these ranges, including mixed DN42/external results, are skipped rather than allowing DNS records to direct the scanner onto arbitrary clearnet hosts.

HTTP connections are pinned to the validated addresses while retaining the hostname for HTTP Host and TLS SNI. `web-scan` does not resolve the hostname again during probing or use ambient HTTP proxies. A stale resolution file can therefore produce failed probes rather than silently switching to a new destination; regenerate DNS results when needed.

## Requirements

- .NET 10 SDK
- access to DN42
- a DNS resolver capable of resolving `.dn42`
- a local checkout of the DN42 registry
- both exclusion files in `config/` under the current working directory

The local registry checkout is needed for the default resolution command. Resolution and probing need access to DN42; `web-scan` reads saved DNS results instead of the registry. Generating a report from an existing JSON file does not need network access, but still requires both exclusion files at startup.

When `DN42ATLAS_REGISTRY_PATH` is set, it names the checkout root (production uses `/var/lib/dn42atlas/registry`); resolution reads its `data/dns` directory. Without it, the developer fallback is:

```text
~/dn42-registry
```

Specifically:

```text
~/dn42-registry/data/dns
```

## Build

For a fresh checkout, use `git clone --recurse-submodules`, or initialize an existing checkout with:

```bash
git submodule update --init --recursive
```

The pinned `external/JoyfulReaperLib` submodule supplies only its ntfy project to `DN42Atlas.OptOut`; no machine-local sibling checkout or private package feed is required.

From the repository root:

```bash
dotnet build I2PAtlas.slnx
```

## Architecture and Tests

The crawler separates command handling, registry/DNS work, scanning, pinned networking, HTTP probing, policy filtering, reporting, and publishing. `DN42Atlas.OptOut` owns Auth42 authentication, exact-resource authorization, exclusion reconciliation, manual requests, and server-rendered endpoints.

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the component boundaries, trust model, persistent state, failure behavior, and deployment flow.

Run the automated tests from the repository root:

```bash
dotnet test
```

Tests use temporary fixtures, fake DNS/probe functions, HTTP responses, and in-memory connection streams; they do not contact DN42
or external services. MSTest is used only by the test project; CIDR matching uses
the existing implementation without additional networking packages.

## Manual Requests

The anonymous `/contact` form is the normal manual-review fallback for broader/wildcard requests, ownership or authentication problems, corrections, and other concerns. Authenticated self-service remains preferred. A submission stores resource, contact, request type, message, timestamp, and status in a separate private SQLite database; it never automatically changes exclusions or publication.

After durable persistence and a successful response, JoyfulReaperLib.Ntfy attempts a best-effort notification containing type, resource, contact, and request ID. It omits the full free-form message. Notification failure preserves success and the database record. There is no durable notification retry queue; review pending records even when ntfy is unavailable. The contact limiter is a process-local global limit of ten POST attempts per minute.

Private review commands belong to the **OptOut executable**, not the crawler:

```text
dotnet run --project DN42Atlas.OptOut -- manual-requests
dotnet run --project DN42Atlas.OptOut -- manual-request <id>
dotnet run --project DN42Atlas.OptOut -- manual-request-status <id> <Pending|Reviewed|Resolved|Rejected>
dotnet run --project DN42Atlas.OptOut -- manual-request-delete <id>
```

The list omits Message; detail includes it. Leaving Pending sets ReviewedUtc, changing between reviewed states preserves it, and returning to Pending clears it. Deletion affects only the selected manual request. No unauthenticated HTTP records endpoint exists.

`./dn42atlas-request-admin [env-file]` offers the same operations interactively and requires exact `DELETE` confirmation after showing a request. It sources trusted deployment configuration from `~/.config/dn42atlas/oidc.env` when present; its argument or `DN42ATLAS_ADMIN_ENV_FILE` overrides that path. See [OptOut configuration](DN42Atlas.OptOut/README.md#manual-requests-and-notifications) for database initialization and the standard `Ntfy__ServerUrl`, `Ntfy__Topic`, `Ntfy__AccessToken`, and `Ntfy__Timeout` settings. Keep secrets out of source control.

## Deployment and Private Preview

The deployed OptOut service uses systemd to run:

```text
/usr/bin/dotnet /opt/dn42atlas-optout/current/DN42Atlas.OptOut.dll
```

`./deploy-optout.sh` refuses a dirty checkout, pulls with `--ff-only`, synchronizes pinned submodules, and publishes Release output into `/opt/dn42atlas-optout/releases/<UTC-timestamp>-<sha>`. It atomically switches `current`, restarts `dn42atlas-optout.service` when installed, and checks that it is active. Only then does it prune completed releases: current plus the three newest other releases by default. Set `DN42ATLAS_OPTOUT_RETAIN_PREVIOUS` to a non-negative integer to change the number of other releases retained; zero keeps current only. Failed publishing or service restart does not prune. This active-state check is not an HTTP health probe. Source edits alone do not update the running binary; deploy them.

The existing deployment binds OptOut to `127.0.0.1:5078`. nginx serves static content and proxies only `/operator`, `/login`, `/logout`, `/signin-oidc`, and `/contact`. The listener address, nginx rules, and systemd unit are deployment configuration, not installed or hard-coded by the application. Keep the service account's Data Protection keys persistent across releases.

| Location | Purpose |
| --- | --- |
| `/opt/DN42Atlas` | Source checkout, config, CLI and deployment helpers |
| `DN42Atlas/site/` | Source preview pages served during the launch gate; already linked to live support routes |
| `results/` | Private historical raw scans and adjacent reports; never serve this directory |
| `published/` | Generated/derived listing and support artifacts, separate from the current preview root |
| `/var/lib/dn42atlas/registry` | Private dedicated registry authorization checkout |
| `/var/lib/dn42atlas/exclusions.db` | Private exclusion audit records |
| `/var/lib/dn42atlas/manual-requests.db` | Private manual request records |
| `/var/lib/dn42atlas/runtime-exclusions.json` | Private active exclusion policy consumed by crawler/report commands |
| `/var/lib/dn42atlas/publication-state.json` | Private exact raw-scan selection and SHA-256 |
| `~/.config/dn42atlas/oidc.env` | Secret deployment configuration |

Paths above describe the production layout conceptually; use the configured absolute paths for your deployment. Crawler commands use working-directory defaults for `results/`, `published/`, and manual policy files. Resolution reads `<DN42ATLAS_REGISTRY_PATH>/data/dns` when configured; the setting is the checkout root shared with OptOut authorization and `registry-update`. Explicit constructor overrides take priority in tests; without configuration the developer fallback remains `~/dn42-registry/data/dns`. Configured roots must be nonempty absolute paths.

The preview source pages already link to `/operator` and `/contact`. Separately, the publisher preserves already-installed support pages; updating those derived copies is an operator decision. Do not switch nginx to generated listings before the no-earlier-than **October 16, 2026** launch gate. Deployment does not make that switch or schedule crawls.

## Commands

The CLI command structure is:

```text
dn42atlas                              Registry parsing and DNS resolution
dn42atlas resolve                      Explicit registry/DNS resolution
dn42atlas web-scan [resolution-file]    HTTP/HTTPS scan from saved DNS results
dn42atlas probe-test                    Single-host HTTP/HTTPS probe test
dn42atlas report <web-probe.json>        HTML viewer from existing scan JSON
dn42atlas run                           Resolve, scan, generate HTML, publish stable files
dn42atlas republish                     Rebuild current public artifacts without crawling
dn42atlas publish-existing <web-probe.json> Publish a selected raw scan without crawling
dn42atlas registry-update               Update the configured dedicated registry snapshot
dn42atlas --help | -h | help             Show usage without scanning
```

The examples below use `dotnet run --project DN42Atlas` from the repository root. Generated resolution and scan files are written under the current working directory.

Unknown commands print usage and exit with code 2. Missing report arguments also return 2; missing input files and other command failures return 1. Successful commands return 0. Individual DNS lookup failures and unreachable HTTP origins remain recorded scan outcomes rather than failing the entire command.

### Combined Run

```bash
dotnet run --project DN42Atlas -- run
```

`run` resolves the registry into `domain-resolution.json`, passes that exact file to `web-scan`, and generates the corresponding HTML viewer through the existing web-scan report stage. It retains the timestamped JSON and HTML in `results/`, then generates filtered stable artifacts:

```text
published/
  index.html
  latest.json
  about.html
  opt-out.html
  robots.txt
```

`published/` is the derived static output root intended for the public Atlas. Its `index.html` and `latest.json` contain the current filtered artifact; `results/` holds private timestamped historical data. The current served preview uses source pages instead; generating output is not authorization to launch it.

Public generation reads the raw scan into a fresh in-memory JSON model and applies the current `PublicScanPolicy` exactly once. Both `latest.json` and the HTML's embedded scan are serialized from that same filtered/redacted model. The original raw JSON remains unchanged. HTML serialization retains its safe default encoder so scan content cannot close the embedded script.

### Private Publication State and Republish

Each successful `run` records which immutable raw scan backs the public artifacts. Set `DN42ATLAS_PUBLICATION_STATE_PATH` to a private location outside the web root, for example `/var/lib/dn42atlas/publication-state.json`. The default is `.dn42atlas/publication-state.json` under the working directory, also outside `published/`.

```json
{
  "version": 1,
  "rawScanPath": "/opt/DN42Atlas/results/web-probe-20261003-125026.json",
  "publishedAtUtc": "2026-10-03T13:00:00Z",
  "rawScanSha256": "<64 hexadecimal characters>"
}
```

State contains metadata only, including the absolute raw scan path and required SHA-256 hash. It is staged and flushed beside the private state file, then atomically replaced only after both public files have been replaced. It is not updated if public publication fails. Existing publications created before state tracking can use `publish-existing` to establish state without another crawl; Atlas never guesses a source from filenames or modification times.

```bash
dotnet run --project DN42Atlas -- publish-existing results/web-probe-20261003-125026.json
```

`publish-existing` requires exactly one existing raw scan path. It explicitly selects that file, preserves its bytes, applies the current manual and configured runtime exclusions through the same public generator as `run`, and updates `published/latest.json`, `published/index.html`, and the configured private publication state. It performs no DNS, registry resolution, HTTP probing, or crawling. It can bootstrap missing state or intentionally replace the current selection; it does not bypass exclusions. Missing arguments or extra arguments return usage error 2; a missing file or invalid policy fails without replacing public artifacts. Subsequent `republish` commands use this recorded selection and verify its hash.

```bash
dotnet run --project DN42Atlas -- republish
```

`republish` uses only the state-recorded raw file and the current required manual exclusion files plus `DN42ATLAS_RUNTIME_EXCLUSIONS_PATH`, if configured. It verifies state version, absolute source path, source existence, and SHA-256 before generating anything. Missing or invalid state, replaced source content, invalid runtime exclusions, and ordinary generation failures leave existing public artifacts unchanged. Insufficient destination provenance under an active prefix policy is a privacy failure: it withdraws both listing files rather than leaving an old listing pretending to comply with that policy. No registry access, DNS lookup, HTTP probe, or crawl is performed. `GeneratedAt` and the viewer's Last scan timestamp remain those of the original scan; only publication time changes on success.

Raw snapshots and private state must remain outside the served directory. Publish one pipeline at a time. The two public replacements and subsequent state replacement are not a single transaction: a crash or private state replacement failure after public replacement can leave state referring to the previous scan. Such failures return nonzero; preserve the raw snapshots and investigate before retrying. Configure filesystem paths directly, without aliases or links into the web root.

The application owns and updates only the generated `index.html` and `latest.json`. On first publication it also installs self-contained `about.html`, `opt-out.html`, and `robots.txt` files from `DN42Atlas/site` when each destination file is absent. Existing support files are preserved byte-for-byte, so operators can maintain them manually. Other pages and assets in `published/`, including an operator-provided `site.css`, are left untouched. The bundled pages currently use inline styles and do not require `site.css`. Template changes require a rebuild and do not automatically replace an installed support file; update that file manually when needed.

Generated files are staged in `published/`, flushed and closed, then individually replaced by same-directory atomic renames. Bundled support files use the same staging process but are installed without replacing existing files. Generation or staging failures leave the previous generated files intact. The two generated-file replacements are not a single transaction: a crash or replacement failure between them can leave JSON and HTML from different runs. The HTML viewer is self-contained and does not load `latest.json`.

The command stops and returns nonzero if a stage fails. Standalone `web-scan` and `report` keep their existing outputs and do not update `published/`. Crawler commands do not configure a web server, deploy OptOut, or schedule work. Temporary staging filenames begin with a dot and end in `.tmp`; servers should not expose these files.

For unattended execution, set the working directory explicitly: exclusion files, relative input paths, `domain-resolution.json`, `results/`, and `published/` depend on it. Set `DN42ATLAS_REGISTRY_PATH` to the dedicated checkout root; unset configuration retains the developer home-directory fallback. Commands currently have no pipeline-wide cancellation support. Resolution output overwrites its fixed filename; standalone scan filenames use local start time to the second, so coordinate standalone scans separately. Historical artifact writes are unchanged; only stable published files use atomic replacement.

### Operation lock and daily crawler

`run`, `republish`, and `publish-existing` acquire a shared cross-process operation lock **before loading exclusion policy** and hold it through the entire operation. OptOut exclusion/inclusion mutations, their rollback recovery, `exclusions-reconcile`, and pending-reconciliation startup recovery use the same lock. Runtime initialization/materialization CLI commands also participate. Standalone `resolve`, `web-scan`, `report`, `probe-test`, and help do not take it because they do not change stable public/runtime state.

The permanent private lock file is `<publication-state-path>.operation-lock`: production normally `/var/lib/dn42atlas/publication-state.json.operation-lock`; the CLI developer default is `.dn42atlas/publication-state.json.operation-lock` under its working directory. Both applications must configure the same absolute `DN42ATLAS_PUBLICATION_STATE_PATH`, the same manual/runtime policies, and the same published directory. No new lock setting is needed. Linux uses nonblocking exclusive `flock(LOCK_EX | LOCK_NB)` on an open descriptor, explicitly checked for errors; Windows uses `FileShare.None` sharing enforcement. Disposal or process death releases ownership; file existence is never ownership. New Unix files use `0600`. Use a protected local filesystem directory and never delete/rename the lock file or use path aliases while processes may run. Unsupported OS/filesystem locking fails closed. The Linux explicit check also avoids relying on .NET's best-effort sharing locks.

Publishing CLI, maintenance and startup recovery wait for ownership (lock acquisition is cancellable internally). Web final mutations wait at most one second for each local/OS gate, returning HTTP 503 with `Retry-After: 30` and retry guidance if busy, with no DB/runtime/public changes. A 10–15 minute crawl therefore temporarily defers self-service mutations instead of holding their HTTP requests. Existing served listing remains as it was until a mutation actually obtains ownership.

The [crawler deployment guide](deployment/systemd/README.md) includes opt-in systemd templates for a daily 01:30 local-time run with up to 15 minutes randomized delay and persistent missed-run handling. They use the source checkout, a separate minimal `crawler.env`, and a 45-minute service limit including build/lock waiting. Templates are not installed automatically. They refresh hidden `/opt/DN42Atlas/published` artifacts while nginx continues serving `/opt/DN42Atlas/DN42Atlas/site`; the no-earlier-than **October 16, 2026** launch gate is unchanged. No unit modifies nginx or switches the preview root.

### Registry / DNS Scan

Running without arguments, or using `resolve`, parses the registry and resolves registered `.dn42` domains:

```bash
dotnet run --project DN42Atlas
```

The result is written to:

```text
domain-resolution.json
```

### Single-Host Probe Test

For development, Atlas can probe `burble.dn42` across the configured port list:

```bash
dotnet run --project DN42Atlas -- probe-test
```

This is useful for checking HTTP probing, TLS handling, robots parsing, homepage metadata extraction, and link discovery before running a full scan.

Results are printed to the console; this command does not write scan JSON or an HTML viewer. It requires both exclusion files and skips its fixed host when hostname, prefix, or DN42 address checks reject it.

### Web Scan

Use an existing DNS-resolution file:

```bash
dotnet run --project DN42Atlas -- web-scan
```

The default source file is:

```text
domain-resolution.json.bk2
```

A different file can be supplied:

```bash
dotnet run --project DN42Atlas -- web-scan path/to/domain-resolution.json
```

Results are written under:

```text
results/
```

For example:

```text
results/web-probe-20261003-002948.json
```

The scan also generates a corresponding HTML viewer:

```text
results/web-probe-20261003-002948.html
```

The filename timestamp comes from the scan's local start time. Results are ordered by domain, scheme, then port.

To preserve console output as well:

```bash
mkdir -p results

dotnet run --project DN42Atlas -- web-scan \
  2>&1 | tee "results/web-scan-console-$(date +%Y%m%d-%H%M%S).log"
```

## Generate a Viewer From an Existing Scan

You do not need to rescan the network to generate the HTML viewer.

The command is `report <web-probe.json>`. It filters supplied results against current hostname exclusions and recorded destinations against prefix exclusions, filters/redacts references, safely embeds the JSON, and writes an HTML file alongside the JSON with the same basename. It does not alter the source snapshot or re-probe services. Insufficient prefix provenance fails closed and removes the previous output HTML where possible; legacy compatibility is described under Complete Exclusion.

```bash
dotnet run --project DN42Atlas -- \
  report results/web-probe-20261003-002948.json
```

This creates:

```text
results/web-probe-20261003-002948.html
```

On a Linux desktop:

```bash
xdg-open results/web-probe-20261003-002948.html
```

## HTML Atlas Viewer

The generated report is a self-contained HTML file.

It supports:

- Previous / Next navigation
- random origin selection
- hostname/title filtering
- direct links to discovered services
- HTTP status and content type
- robots status
- page title
- discovered links
- `.dn42` hostname mentions
- a list of discovered DN42 hosts outside the original seed set

Keyboard shortcuts:

```text
Left Arrow   Previous origin
Right Arrow  Next origin
R            Random origin
```

No framework or build system is required for the report.

It is intentionally just HTML, CSS, and a little JavaScript.

## Currently Probed Web Ports

The current curated list includes:

```text
HTTP:
80
81
3000
3001
4000
5000
5001
7000
8000
8001
8008
8080
8081
8088
8880
8888
9000
9090

HTTPS:
443
4443
8443
9443
10443
```

The goal is not to sweep every TCP port.

The list is intentionally biased toward ports commonly used for web applications and self-hosted services.

## Link Discovery

When homepage access is permitted, Atlas currently extracts:

- HTML `href` links
- plain HTTP URLs
- HTTPS URLs
- other supported URL text found in page content
- `.dn42` hostname mentions

Relative links are resolved against the source origin.

Fragments are removed.

`mailto:` and `javascript:` links are ignored.

The current limits are:

```text
Homepage body:       256 KiB
robots.txt body:      64 KiB
Discovered links:       250
DN42 mentions:           250
```

The JSON records when these limits caused truncation.

Recording a link does not mean Atlas probes its protocol. Discovery records references from HTTP/HTTPS pages; links are not automatically followed.

## Current Scope

DN42Atlas currently probes HTTP/HTTPS services only.

Planned or possible future work includes:

- deduplicated discovered-host graph
- showing which pages referenced each discovered host
- optional second-layer discovery
- better service classification
- Gopher probing
- Gemini probing
- Finger probing
- QOTD probing
- scan profiles such as `core`, `extended`, and `all`
- comparing scan history over time
- richer topology and relationship visualization

Any future recursive discovery should continue to use conservative limits and respect the policy of the destination service.

## Example Findings

A scan can uncover the usual mixture of DN42 infrastructure, personal pages, experiments, dashboards, and self-hosted software.

Examples observed during development included services identifying themselves as:

- Forgejo
- Gitea
- SearXNG
- CyberChef
- Portainer
- Vaultwarden
- qBittorrent WebUI
- BlueMap
- Kasm
- looking glasses
- network dashboards
- blogs
- personal homepages
- authentication services
- DN42-specific discovery sites

These names are based only on publicly returned page metadata.

DN42Atlas does not attempt to log in or inspect private application state.

## Why?

Mostly because DN42 is full of interesting things and manually finding them is annoying.

The registry gives you names and network resources, but it does not necessarily tell you what people are actually running on them.

DN42Atlas tries to bridge that gap without turning into a hostile scanner.

The end goal is something closer to:

```text
"What public stuff exists on DN42 right now?"
```

than:

```text
"How many ways can I annoy every router operator?"
```

## Status

Very experimental.

The crawler, authenticated self-service, reversible exclusions, manual request management, and persistent OptOut deployment are implemented. Public service listings remain gated until no earlier than October 16, 2026. See the [architecture guide](docs/ARCHITECTURE.md#known-limitations--future-work) for operational limitations.

Expect:

- changing JSON formats
- false positives
- disappearing services
- temporary DNS failures
- strange certificates
- unusual HTTP implementations
- servers that respond differently between scans
- code written primarily because it seemed fun at the time

That is also approximately the expected operating environment of DN42.

## License

DN42Atlas is licensed under the GNU Affero General Public License v3.0.

See [`LICENSE`](LICENSE) for details.
