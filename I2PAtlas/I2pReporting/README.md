# I2PAtlas public viewer v1

From the I2PAtlas repository root:

    dotnet run --project I2PAtlas -- i2p-view published/i2p-latest.json published/i2p-index.html

Both paths are explicit local files. Create the output directory first. The command
never constructs scanners, transports, seed readers or the private raw reader.
It reads only i2patlas.public-scan v1.

I2pPublicScanReader enforces the exact root/entry property sets (including explicit
nulls), rejects duplicate JSON properties, validates all string enums, UTC scan
times, normalized public hostname syntax, canonical 52-character destination B32
identities, public title/media-type bounds, observation consistency and derived
counts. Entries must already be strictly ordered ordinally by friendly hostname
then destination hostname, with no duplicate public pair. Discovered hostname
arrays must already be sorted and unique. It does not repair input. Friendly and
discovered hostnames use the existing public projection's hostname rules; no
Base64 destination is needed to validate them.

Bounds: 128 MiB UTF-8 input (checked before and during reading), JSON depth 8,
10,000 entries, 250 discovered hostnames per entry, 253 characters per public
hostname, 60 per destination B32 hostname, 160 per title and 80 per content type.
Generated directory/detail/overview content is limited to 64 Mi UTF-16 characters.
A public artifact whose private seed spellings collapsed to duplicate public
identities is rejected rather than silently merged or assigned unstable IDs.

The generator renders the full directory, details and discovery overview during
local HTML generation. No public JSON is embedded in JavaScript; enhancement
derives its inputs from HTML-encoded
attributes. Every public string is HTML-encoded again at rendering time. Unsafe
hostname text cannot become an active external link even if an internal caller
bypasses the reader. The page uses no framework, remote assets or dynamic fetch.
A restrictive CSP blocks subresource/network requests; an empty data favicon
avoids the browser's implicit favicon lookup. Explicit links remain user actions.

Each stable detail ID is i2p-service- followed by lowercase SHA-256 of the exact
normalized public hostname + "\n" + destinationHostname. Titles, array positions,
summary states and scan times do not affect it. Every destination remains a row;
multi-destination hosts get equal treatment and a visible count badge.

Fresh documents start with Homepage set to HTTP 200 observed and other filters
set to Any, independently of browser form restoration. Homepage also offers Any
homepage response (a non-null homepage status), No homepage response (a null
homepage status), and All entries. It uses only the existing public data-http
row attribute; robots HTTP statuses do not count as homepage responses.
Search covers friendly name, B32, title, content type and discovered names.
Summary and robots filters include every current enum; the compact discovery
filter offers title, discovered hosts, multiple destinations and incomplete
extraction. Sorting covers hostname, title, summary, homepage HTTP and discovered
count, with the full public pair as the final ordinal tie-breaker. Reset restores
the HTTP 200 homepage default, clears other filters, and retains sorting.
Explicit deep links relax only conflicting filters, including Homepage when needed.
History traversal preserves applied filters, except where revealing its explicit
target requires relaxing them; BFCache resumes applied controls.

Previous/Next/Random use the displayed filtered/sorted set and the History API
without scrolling or focusing. Native detail links support keyboard activation;
explicit detail-link activation focuses and reveals the selected panel.
Arrow keys and R work outside interactive controls. Copy link uses the full URL
and stable fragment; unavailable/denied clipboard access exposes a read-only
select-and-copy fallback. A stale clipboard response cannot announce a later
selection as copied.

The full static view remains usable without JavaScript. A head bootstrap hides
partially enhanced content until initialization is complete, suppresses premature
fragment positioning, and positions explicit initial links against the final
layout. An initialization exception restores all rows/details and disables
enhancement. At 600px and below the same semantic table becomes a labelled card
list. Large sticky controls are disabled on narrow or short viewports.

Destination links use http://<destinationHostname>/, friendly-name links use
http://<hostname>/; both are explicit _blank links with noopener noreferrer.
Friendly names depend on the visitor's I2P addressbook. Discovered hostnames have
no reconstructed paths/queries. Exact friendly-name matches navigate internally
to every matching Atlas destination; outside-seed links do not claim scan coverage.

Rendering/validation completes before staging a unique .i2p-view-*.tmp beside
the output. Flush, disk flush and close precede same-filesystem replacement.
Cancellation, validation and staging failures preserve previous HTML and clean
owned temporary files. Input/output cannot be the same path or use filesystem
reparse points. Existing hard-link aliases and simultaneous writers are outside
those path checks; replacement does not truncate the old file. No multi-file or
power-loss transaction is claimed.

Tests:

    dotnet build I2PAtlas.slnx
    dotnet test I2PAtlas.slnx --no-build
    node --test I2PAtlas.Tests/I2pViewerTests.cjs

The Node tests use DOM fixtures for behavior; use a browser for layout checks.

## Standalone crawl inspection

The crawl viewer is a separate generator and template. Existing `i2p-view`
continues accepting only public-scan v1 with its unchanged behavior and HTTP-200
default. The new command accepts only validated public-crawl v1:

```text
dotnet run --project I2PAtlas -- i2p-crawl-view <public-crawl-file> <html-output-file>
```

The completed local CLI pipeline is:

```text
private seed snapshot
    -> i2p-crawl
    -> private i2patlas.raw-crawl v1
    -> i2p-crawl-project
    -> public i2patlas.public-crawl v1
    -> i2p-crawl-view
    -> standalone crawl HTML report
```

`I2pCrawlViewerGenerator` calls only the existing strict `I2pPublicCrawlReader` for
input. It never opens a seed/raw artifact or initializes crawling/networking.
Both paths must be explicit local files and differ. The output directory must
exist; URL, UNC/device and reparse-point paths/ancestors reject using the existing
local projection/viewer policy. Exit codes are 0 installed, 2 arguments/paths,
1 invalid public input/render/I/O failure and 130 caller cancellation. Ctrl+C
requests cancellation. CLI diagnostics contain counts and generic errors without
input values or paths, and do not imply verification.

The report shows crawl start/completion UTC, known/discovered/total counts,
homepage-response and title counts, and a complete directory with Kind, Hostname,
Depth, Title, Summary state, Homepage HTTP and Referrer columns. Known seeds are
depth 0, with friendly and destination-derived B32 links/details and no referrer.
Discovered entries are depth 1, with HostForm, their actual hostname and exact
parent hostname+B32. The parent's B32 distinguishes alternate known destinations;
it is never presented as a destination identity of the child.

Each detail contains all public observation fields: summary, robots outcome/
classification/observation/status, homepage attempt/outcome/classification/status,
content type, title, discovered I2P hostnames and extraction-incomplete flag.
Unknown nullable state is shown as Not observed. HTTP statuses, including errors,
are observations through an unauthenticated proxy, not liveness/ownership/origin
authentication. Friendly discovered hosts depend on router/addressbook resolution.

Stable detail IDs are `i2p-crawl-entry-` plus lowercase SHA-256 of UTF-8:

```text
KnownSeed\n<hostname>\n<destinationHostname>
DiscoveredHost\n<hostname>\n<referrerHostname>\n<referrerDestinationHostname>
```

Array position, crawl times, title, status and other observations do not affect
IDs. Each detail links back to the directory; discovered entries link to their
exact known parent's detail. All rows/details remain in the standalone HTML.
Details are hidden by default; selecting a hostname reveals only its fragment's
CSS `:target` article, without JavaScript. Back to directory targets `#directory`
and hides the selected detail. The detail container has no empty visible heading.

The enhanced View defaults to Likely to respond, exactly the entries whose public
SummaryState is `HomepageResponseObserved` or `Ambiguous`. This is a browsing
heuristic based on proxy observations, not origin/liveness verification. Ambiguous
results are included without claiming an origin response. Failure, timeout,
disallow and redirect summary states are excluded. All entries restores the full directory.
The View combines with the independent Summary, Kind and search filters; choosing
All entries does not clear those filters. Restored filter values are cleared at
initialization. Enhancement adds case-insensitive hostname/title search, All/Known/Discovered Kind
filter, Summary filter, four column sorts and reset. Sorts toggle direction with
aria-sort; numeric Homepage HTTP places missing observations last in both
directions, with hostname and stable ID as deterministic tie-breakers. Reset restores
Likely to respond and clears other filters while retaining sorting. The counter
labels matching entries as likely to respond when that View is selected. Without
JavaScript all directory rows remain available. Filtering affects directory rows only; native
fragment targeting controls detail visibility independently. Enhancement failure
reveals all rows and disables controls. No History API navigation, clipboard
feature or elaborate keyboard navigation is added.

Random site chooses uniformly among the rows currently visible after View,
search, Kind and Summary filtering. It invokes the selected row's marked safe
Open link (`a[data-crawl-open]`), with its existing root URL, `_blank` and
`noopener noreferrer`, rather than constructing URLs or visiting detail fragments.
Sorting changes display order, not eligibility. No matches disables the button;
with one match it opens that entry. The button is disabled in static HTML and
hidden with the other controls without JavaScript or after enhancement failure.

Every public string is HTML-encoded again, including attributes, titles and link
labels. Even an internal caller bypassing the reader cannot turn unsafe hostname
text into an active external URL. External I2P links are explicit user actions,
HTTP roots only, `_blank` with `noopener noreferrer`; original paths/queries are
never reconstructed. The separate embedded template has a restrictive CSP, data
favicon, no remote assets/fetches and no public JSON in executable JavaScript.
Public title markup stays literal text and Unicode is preserved.

The complete generated document is bounded to 64 Mi UTF-16 characters, including
template, encoded attributes and all details. Rendering completes before creating
a unique private `.i2p-crawl-view-*.tmp` beside output. UTF-8 writing, async flush,
disk flush and close precede cancellation check and replacement. Failure before
replacement preserves previous HTML and cleans the owned stage. The same existing
hard-link, concurrent-writer and crash limitations apply. This produces one local
report, with no deployment/hosting automation.

Additional validation:

```text
dotnet test I2PAtlas.Tests/I2PAtlas.Tests.csproj --filter "FullyQualifiedName~I2pCrawlView"
node --test I2PAtlas.Tests/I2pCrawlViewerTests.cjs
```
