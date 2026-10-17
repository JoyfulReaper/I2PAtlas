# I2PAtlas

I2PAtlas imports a local I2P addressbook, takes bounded HTTP observations through
an explicitly configured I2P HTTP proxy, projects an allowlisted public JSON
artifact, and generates a standalone HTML viewer.

A response records what the proxy returned. It does **not authenticate the
origin**, and neither a status code nor a title establishes that the destination
answered. Robots classification, request outcomes and HTTP metadata remain
separate observations.

The project originated from DN42Atlas; its former runtime has been removed.

## Build and test

Install the .NET 10 SDK. From the repository root:

```text
dotnet build I2PAtlas.slnx
dotnet test I2PAtlas.Tests/I2PAtlas.Tests.csproj
```

The dependency-free viewer script tests use Node.js:

```text
node --test I2PAtlas.Tests/I2pViewerTests.cjs
node --test I2PAtlas.Tests/I2pCrawlViewerTests.cjs
```

Automated network tests use fake local HTTP proxies and synthetic destinations;
they require no I2P router. The Unix file-permission test skips on Windows.

## Workflow

```text
local hosts/addressbook export
  -> private seed snapshot
  -> private raw scan
  -> public JSON projection
  -> standalone HTML viewer
```

Create `private/`, `private/results/` and `published/` before running these
examples; commands require existing output directories. Save your effective
router addressbook export as `private/hosts.txt`. These input/output names are
examples, not files supplied by the repository. Quote paths containing spaces.

Import the local export:

```text
dotnet run --project I2PAtlas -- import-seeds private/hosts.txt private/i2p-seeds.json
```

Optionally inspect one seed using a running local I2P HTTP proxy:

```text
dotnet run --project I2PAtlas -- i2p-probe-test private/i2p-seeds.json example.i2p http://127.0.0.1:4444
```

Replace `example.i2p` with a hostname present in your snapshot with exactly one
destination. This diagnostic rejects names with multiple destinations rather
than choosing one. Its bounded report is private and omits full destinations,
snapshot/source paths and response bodies. A complete HTTP response, including
an error response, can give exit 0; that is not an origin-authentication result.

Scan the snapshot, then generate public JSON and HTML locally:

```text
dotnet run --project I2PAtlas -- i2p-scan private/i2p-seeds.json private/results/i2p-scan.json http://127.0.0.1:4444
dotnet run --project I2PAtlas -- i2p-project private/results/i2p-scan.json published/i2p-latest.json
dotnet run --project I2PAtlas -- i2p-view published/i2p-latest.json published/i2p-index.html
```

The separate private bounded crawl command uses the same proxy and probe policy:

```text
dotnet run --project I2PAtlas -- i2p-crawl private/i2p-seeds.json private/results/i2p-crawl.json http://127.0.0.1:4444 --max-discovered-hosts 100
```

Depth defaults to 1; `--max-depth 2` or `--max-depth 3` explicitly enables deeper
breadth-first waves. Each wave completes before the next is selected. The
discovered-host cap is global across all depths; each admitted hostname is probed
once. Links at maximum depth remain evidence only. Discovered
host observations are unauthenticated and depend on the proxy/router addressbook.
Output is **private raw-crawl data**. Project its public allowlist locally with:

```text
dotnet run --project I2PAtlas -- i2p-crawl-project private/results/i2p-crawl.json published/i2p-crawl.json
dotnet run --project I2PAtlas -- i2p-crawl-view published/i2p-crawl.json published/i2p-crawl.html
```

New crawls write `i2patlas.raw-crawl` v2; projection writes public-crawl v2,
preserving stored depths and immediate known-seed or host-only referrers.
Existing v1 raw/public artifacts remain readable/projectable/viewable without
migration, and v1 projection retains version 1. The separate crawl viewer generates a standalone
inspection report from that validated public artifact. Existing `i2p-project`
and `i2p-view` remain scan-only and reject crawl artifacts.

Only the diagnostic, scan and crawl perform networking. Import, projection and viewer
generation are local transformations. Generating public files does not deploy
or serve them.

The executable's help branding is `i2patlas`. It has exactly eight commands:

| Command | Required arguments | Options |
| --- | --- | --- |
| `import-seeds` | addressbook file, private snapshot file | None |
| `i2p-probe-test` | snapshot file, existing friendly hostname, proxy URI | `--timeout-ms`: 1–60000 |
| `i2p-scan` | snapshot file, private output file, proxy URI | `--concurrency`: 1–4; `--timeout-ms`: 1–60000; `--max-seeds`: 1–10000 |
| `i2p-crawl` | snapshot file, private output file, proxy URI | Scan option ranges plus global `--max-discovered-hosts`: 0–1000; `--max-depth`: 1–3 (default 1) |
| `i2p-project` | private raw file, public output file | None |
| `i2p-crawl-project` | private raw-crawl file, public-crawl output file | None |
| `i2p-view` | public input file, HTML output file | None |
| `i2p-crawl-view` | public-crawl input file, HTML output file | None |

Scan defaults are concurrency **2**, **10000 ms per request**, and at most
**10000 seeds**. `--max-seeds` takes a prefix in normalized hostname then
preserved destination-text order after validating the entire snapshot.
The crawl uses these same defaults plus **100 discovered hosts globally** and
**maximum depth 1**; zero hosts disables discovered requests. Aggregate progress
goes to stderr at wave starts/barriers and every 25 completions or a completion
after 20 seconds without a line. It contains no identities/proxy/response text.
A failing progress writer is disabled without failing the crawl; the final
completion summary remains stdout. The diagnostic also defaults to 10000 ms per request.

No arguments, `help`, `--help` or `-h` show usage and exit 0 without networking or
file mutation. Unknown commands exit 2. Command-specific error/exit details are in
the component documentation below. For `i2p-scan`, `i2p-crawl`, both projection and both viewer commands,
Ctrl+C requests cancellation and returns 130 if cancellation is handled before
final replacement.

## Addressbook identity

An entry maps a friendly name to an I2P Base64 destination:

```ini
example.i2p=<destination>
```

The placeholder above must be replaced by a full, valid destination. It is a
public-key destination identity, not an IP address or a B32 alias. The parser
validates its encoding and variable-length certificate envelope while preserving
its text, case and padding except for surrounding ASCII spaces/tabs.

Hostnames are normalized to lowercase with one optional trailing dot removed,
then checked as valid ASCII `.i2p` names. Blank/comment lines and bounded
structured SusiDNS export metadata are supported. Action metadata is inert
provenance: the importer consumes an effective addressbook export and never
executes subscription mutations.

A friendly hostname may have multiple destinations. Semantic seed identity is the
**normalized hostname plus destination-derived B32 hostname**. Equivalent Base64
spellings deduplicate on import, retaining the first spelling privately; different
binary destinations remain independent seeds. Each seed's binary destination
determines its `.b32.i2p` target by SHA-256 and lowercase Base32 encoding.
The source hash covers the exact export bytes,
including metadata, comments, whitespace and line endings.

## Transport and probe policy

The proxy URI is mandatory: HTTP, a literal loopback IP and an explicit port
1–65535, such as `http://127.0.0.1:4444` or `http://[::1]:4444`. An optional trailing
slash is accepted. Hostnames, including `localhost`, shorthand IPv4, non-loopback
addresses, mapped/scoped IPv6, credentials, other paths, queries and fragments are
rejected. No default or system/environment proxy is used.

All scan HTTP connections go only to that proxy IP/port. Both the absolute request
URL and Host header use the seed's destination-derived B32 hostname. There is no
operating-system DNS resolution, direct destination connection, friendly-name
routing fallback or clearnet fallback. Requests use HTTP/1.1 on target port 80
with User-Agent `I2PAtlas/0.1`. HTTPS and other target ports are unsupported.

The separate crawler uses destination-derived B32 for known seeds and literal
validated hostnames for discovered hosts. Friendly discovered hosts rely on the
configured router's resolution; no imported destination is inferred for them.

The probe requests `/robots.txt` first. It requests `/` only when:

```text
robots proxy classification == LikelyOriginResponse
AND robots observation == AllowsRoot
```

`LikelyOriginResponse` is a heuristic: status 200, an explicit valid `text/plain`
representation, supported UTF-8, a body within 64 KiB, successful robots parsing
and recognizable User-agent/Allow-or-Disallow policy structure. HTTP 200 alone
does not qualify. HTTP 502/503/504 are `LikelyProxyFailure`; 403, other ambiguous
responses, redirects, malformed/unsupported robots and explicit disallow do not
authorize a homepage request. Origin evidence remains `Unproven` for responses.

Each seed makes at most the robots request and one conditional root request.
Responses have a 16 KiB header cap and 256 KiB buffered-body cap, with a deadline
covering connection, headers and body. Titles and extracted links are bounded.
Redirects are recorded but never automatically followed. Discovered links,
including clearnet links retained privately, never become scan work.
`i2paddresshelper` material is not executed; request queries are rejected and
address-helper links are omitted from extraction.

## Private and public artifacts

| Schema | Role |
| --- | --- |
| `i2patlas.seed-snapshot` v1 | **Private** validated seeds, import timestamp, source path and exact source-byte SHA-256/length |
| `i2patlas.raw-scan` v1 | **Private** per-seed robots/homepage evidence, destinations, timings, proxy/settings and input provenance |
| `i2patlas.raw-crawl` v1/v2 | **Private** separate known-seed and bounded host-only evidence, depths/immediate parent identities, frontier counts and operational provenance; new crawls emit v2 |
| `i2patlas.public-scan` v1 | Explicitly allowlisted friendly/B32 names, scan-level times, observations, bounded titles and discovered I2P hostnames |
| `i2patlas.public-crawl` v1/v2 | Separate known-seed and host-only entries, depths/immediate parent identities, crawl-level times, observations, sanitized titles/media types and I2P hostname lists; input to the crawl inspection viewer |

Keep seed/raw files outside every served directory. `/private/` is ignored by
Git. Raw output rejects path components named `published`, `public`, `wwwroot` or
`site`, but cannot identify arbitrary server roots.

Projection strictly validates raw v1 before selecting public fields. Base64
destinations, source paths/hashes, proxy details, settings, private errors,
per-seed timings, redirects and full discovered URLs are omitted. B32 identities
are intentionally public. Projection does not turn an observation into an
authenticated-origin claim. `i2p-view` accepts **only public-scan v1**;
`i2p-crawl-view` accepts **only public-crawl v1/v2**. Neither accepts seed or raw artifacts.

Crawl projection strictly validates raw-crawl v1/v2 and its staged public-crawl of the same version
output before replacement. It omits operational provenance, destinations, proxy
identity, settings, metrics, private errors, per-request times, redirects and full
URLs. Friendly discovered hosts never gain an inferred destination identity.

Writers stage a complete private temporary file beside the output, flush and
close it, then replace the final file. Normal validation/I/O failures and
cancellation observed before replacement preserve previous output. A crash may
leave a temporary file; there is no multi-file transaction or cross-process lock.

## Viewer

The generated HTML contains the directory and details, with no dynamic JSON fetch
or remote assets. It provides a useful static/no-JavaScript view and progressive
enhancement for search, filters, sorting, Previous/Next/Random navigation and
stable deep links. Multiple destinations remain separate rows and details.

The enhanced default is **HTTP 200 observed**, based on homepage status, with
other entries available through filters. This is an observation filter. Public
text is HTML-escaped; a restrictive CSP limits subresource/network activity.

I2P links activate only through user actions. Friendly `.i2p` links depend on the
visitor's addressbook resolution; destination-derived B32 links are also
available. Browser access needs the visitor's own I2P setup and does not inherit
the scanner's proxy configuration.

The enhanced crawl inspection viewer defaults to **Likely to respond**, including
`HomepageResponseObserved` and `Ambiguous` entries as a browsing heuristic based
on proxy observations, without origin/liveness verification. **All entries** restores
the complete directory, which is also available without JavaScript. It shows
kind, hostname, depth, title, summary, homepage HTTP and referrer columns. Known
destination B32 and exact parent identities are visible in complete detail
sections. Optional JavaScript adds hostname/title search, Kind and Summary filters,
four column sorts and reset. Random site uniformly opens an existing safe Open
link among currently visible rows, respecting every filter; it is disabled when
none match. Reset restores Likely to respond and clears other
filters while retaining sorting. Filters affect only directory rows. Selecting a
hostname reveals its public observation details through CSS fragment targeting,
including without JavaScript; Back to directory hides the detail again. It uses no remote assets
or network fetches. This is a local CLI pipeline, not deployment or hosting automation.

## Current limitations

- Proxy responses are unauthenticated; a proxy could imitate plausible robots.
- Only target HTTP/80 is supported. Conservative robots handling skips some sites
  whose homepages might otherwise be useful to observe.
- `i2p-scan` stays snapshot-only. The separate private crawler defaults to depth 1 and supports at most depth 3;
  there is no deeper recursion or application retry loop.
- There is no scheduler, deployment package or hosting command.
- Addressbook quality affects coverage, and friendly-name resolution can differ
  between addressbooks. Routing by B32 does not provide friendly-name virtual hosting.
- Older private snapshots with equivalent Base64 spellings for one identity must
  be re-imported. Readers reject these duplicates; scanned observations are never merged.

## Technical documentation

- [Architecture and trust boundaries](docs/ARCHITECTURE.md)
- [Addressbook grammar, metadata and validation](I2PAtlas/AddressBook/README.md)
- [Private seed snapshots and strict loading](I2PAtlas/Seeds/README.md)
- [Proxy-only transport](I2PAtlas/I2pTransport/README.md)
- [Robots/homepage policy and diagnostic](I2PAtlas/I2pProbing/README.md)
- [Bounded scanning and private raw artifacts](I2PAtlas/I2pScanning/README.md)
- [Private bounded crawling and strict raw-crawl validation](I2PAtlas/I2pCrawling/README.md)
- [Strict raw validation and public projection](I2PAtlas/I2pPublishing/README.md)
- [Public reader and standalone viewer](I2PAtlas/I2pReporting/README.md)

Licensed under the [GNU Affero General Public License v3.0](LICENSE).
