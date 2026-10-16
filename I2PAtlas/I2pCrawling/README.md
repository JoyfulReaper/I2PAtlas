# Internal offline host discovery

Crawler 1a adds only pure depth-1 frontier selection. There is no `i2p-crawl`
command, network orchestration, persistence schema or production caller scheduling
discovered hosts. Depth bounds beyond this fixed depth-1 work belong to future
orchestration; there is no max-depth setting here.

`I2pHostDiscovery.Select` receives a complete validated `I2pSeedSnapshot` and an
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
Output is immutable and ordinally sorted, each item at depth 1.

`EligibleUnknownReferenceCount` counts eligible unknown URL observations, including
repeated paths and references across parents, before host deduplication. It is not
a unique-host count. `AdmissionLimitApplied` means at least one distinct eligible
unknown host could not be retained, including eviction. At K=0 it is true when
any eligible unknown reference exists, while the frontier remains empty.

Input bounds remain 10000 snapshot/selected seeds and 250 links per parent; default
arrays, excess link counts, duplicate parents and out-of-snapshot parents fail
explicitly. At most 2,500,000 references can be counted. URL and Content-Type
length limits reuse 4096 and 256 characters. Retained selection state is O(K),
in addition to bounded snapshot/parent identity indexes and caller-owned inputs.
Cancellation is supported. There is no HTTP, DNS, socket, filesystem, environment
or proxy lookup, and no task per candidate.
