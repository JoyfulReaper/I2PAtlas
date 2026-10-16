# I2PAtlas public viewer v1

From the I2PAtlas repository root:

    dotnet run --project I2PAtlas -- i2p-view published/i2p-latest.json published/i2p-index.html

Both paths are explicit local files. Create the output directory first. The command
dispatches before DN42 configuration and never constructs scanners, transports,
seed readers or the private raw reader. It reads only i2patlas.public-scan v1.

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

The generator is separate from the DN42 report generator/template. It renders the
full directory, details and discovery overview on the server. No public JSON is
embedded in JavaScript; enhancement derives its inputs from HTML-encoded
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
