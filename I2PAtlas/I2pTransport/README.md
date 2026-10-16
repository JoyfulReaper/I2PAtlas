# Proxy-only I2P HTTP transport

The [single-seed probe](../I2pProbing/README.md), used by the diagnostic and
[bounded scanner](../I2pScanning/README.md), uses this reusable, disposable GET transport;
constructing it and validating a target perform no network access. Only an explicit
`GetAsync` call sends a request. Tests use synthetic destinations and local fake
HTTP proxies; no I2P router or Internet service is required.

## Explicit proxy and approved targets

Supply an HTTP URI with a literal loopback address and explicit TCP port, e.g.
`http://127.0.0.1:4444` or `http://[::1]:4444`. Other 127/8 IPv4 addresses and IPv6
loopback spellings work. Ports are 1–65535. A single optional trailing `/` is
allowed. Missing/blank configuration, hostnames (including `localhost`), shorthand
IPv4, non-loopback or mapped/scoped IPv6, credentials, paths, queries, fragments
and other schemes are rejected. There is no default endpoint.

`I2pHttpRequestTarget(seed, absoluteUrl)` requires an existing validated immutable
`I2pSeed`. URLs must use HTTP, port 80 (implicit or literal `:80`), and that seed's
normalized friendly hostname or its destination-derived Base32 hostname. Another
seed's `.i2p` name is rejected too. Userinfo, IPs, clearnet names, other schemes,
malformed authorities, backslashes, whitespace/control characters, fragments and
queries are rejected, including empty `?`/`#`. Queries are deliberately excluded
to avoid proxy address-helper commands and their binding semantics.
URL length is capped at 4096 characters. The model has only getters, an immutable
seed and an immutable URI. Callers cannot override Host, headers, methods or the
underlying HttpClient/handler.

## Internal host-only probe foundation

Existing commands still use destination-bound seeds. There is no discovery
frontier or crawler command. Internal `I2pProbeTarget` has only two closed variants:
`KnownSeed` retains its validated seed and destination-derived B32, while
`DiscoveredHost` contains only a normalized hostname and `Friendly` or
`ClassicBase32` host form. It has no Base64 destination or inferred destination
identity. Neither target contains crawl depth or provenance.

Discovered friendly names use the existing ASCII LDH `.i2p` validation: at most
253 characters, labels of 1–63 characters, lowercase normalization, one optional
trailing dot, and surrounding ASCII spaces/tabs. URL syntax, IP literals,
localhost/non-`.i2p` names, ports, userinfo, paths, queries and fragments reject.
`proxy.i2p`, `router.i2p`, `console.i2p` and their subdomains reject as a narrow
routing-safety rule; this does not change known-seed validation or add operator exclusions.

Names under `.b32.i2p` must have exactly one 52-character hash label using
`a-z2-7`, with zero trailing unused bits (last character `a` or `q`). Bare
`b32.i2p`, malformed hashes, extra labels and extended/encrypted-lease-set forms
(56+ encoded characters) reject; none falls back to friendly routing.

Internal immutable `I2pProbeRequest` constructs its URI and Host together from
one validated target and `ProbeResource.Robots` or `ProbeResource.Root`. No path
or headers can be supplied. Known targets use derived B32; host-only targets use
the literal normalized hostname. For example:

```text
GET http://foo.i2p/robots.txt HTTP/1.1
Host: foo.i2p
```

The only other probe resource is `/`. Typed transport support is internal, always
uses the fixed probe User-Agent, and shares the existing private send path and
unchanged guarded proxy connection path. The public seed-bound `GetAsync` API
remains available with its existing behavior.

Host-only responses are unauthenticated proxy/router-routed observations. The
proxy may resolve the same friendly hostname differently between requests;
robots and homepage are not claimed to be pinned to one destination.

## Destination binding

Routing uses `base32lower(SHA256(binary destination)) + .b32.i2p`, with 52 Base32
characters and no padding, per the [I2P naming specification](https://www.i2p.net/en/docs/overview/naming/).
A temporary decoded copy is used only for hashing; the validated seed's destination
text and the existing destination-validation rules remain unchanged. Equivalent
padded/unpadded encodings derive the same identity. The approved path is preserved
under normal URI escaping/canonicalization. Both the proxy's absolute-form request
URL and HTTP Host use the derived Base32 hostname. The friendly name is metadata
for reporting, never proxy routing authority. There is no friendly-name or
addressbook fallback when Base32 lookup fails.

This deterministically selects the expected destination under a correctly behaving
configured proxy; it does not authenticate the proxy or prove which peer actually
answered. The [I2P HTTP proxy](https://www.i2p.net/en/docs/api/i2ptunnel/) can generate
its own success/error pages. No status code, including 200, is labeled destination
reachability by this component. Friendly-name virtual hosting and encrypted/blinded
LeaseSet credentials/extended Base32 addresses need later decisions. Unsupported
destinations may fail at the proxy; they must not cause fallback or weaker binding.

## Connection invariants

`SocketsHttpHandler` has an explicit fixed `IWebProxy` that always returns the
configured proxy URI and never bypasses it. It performs no system/PAC/environment
proxy lookup or local-name detection. HTTP_PROXY, HTTPS_PROXY, ALL_PROXY, NO_PROXY,
WinHTTP/system settings and `HttpClient.DefaultProxy` do not select routing.
The handler's `ConnectCallback` also checks the requested connection endpoint is
the configured literal proxy IP and port, then calls `Socket.ConnectAsync` with a
fresh **IPEndPoint**, never a hostname/DnsEndPoint. There is no DNS call, direct
destination socket, alternate connector or fallback path. Proxy failure remains a
failure. Target validation also prevents a configured I2P outproxy from receiving
clearnet requests. Cookies, automatic authentication and automatic decompression
are disabled; no caller-supplied headers or credentials are forwarded.

Redirects are returned unchanged and never followed, whether they point to the
same destination, another I2P identity or clearnet. HTTP/1.1 is required; HTTPS,
CONNECT and protocol upgrades are outside this API.

## Responses and failures

The transport owns its HttpClient/handler and supports reuse. Dispose it when done;
dispose each returned HttpResponseMessage. Bodies are buffered up to 256 KiB, and
response headers are capped at 16 KiB. The default ten-second deadline (configurable
at construction) and caller cancellation cover connection, headers **and body**.
No reachability inference, robots parsing, content decoding or retries are added.
The HTTP runtime may retry a failed pooled connection; every connection still
passes the same endpoint guard and uses only the configured proxy.

| Outcome | Representation |
| --- | --- |
| Invalid/unapproved target | ArgumentException (null arguments use ArgumentNullException), before sending |
| Missing/invalid proxy | I2pProxyConfigurationException, before constructing a client |
| Proxy TCP refusal/other connection failure | HttpRequestException; underlying SocketException retains SocketErrorCode such as ConnectionRefused |
| Deadline expires | TimeoutException |
| Caller cancels | OperationCanceledException |
| Proxy HTTP response, including errors/redirects | HttpResponseMessage, unchanged status/headers/body, no EnsureSuccessStatusCode |
| Invalid/oversized HTTP response | HttpRequestException from the bounded HTTP runtime |
| Disposed transport | ObjectDisposedException on further sends; in-flight requests are canceled |

Proxy authentication, detailed interpretation of proxy-generated pages, streaming
larger bodies and request query support are not implemented. Transport responses
and seed mappings are private provenance; public output must pass through the
[explicit public projection](../I2pPublishing/README.md).
