using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace DN42Atlas.I2pTransport;

public sealed class I2pProxyConfigurationException(string message) : ArgumentException(message);

/// <summary>An explicit HTTP proxy at a literal loopback IP and explicit TCP port.</summary>
public sealed class I2pProxyEndpoint
{
    public string Address { get; }
    public int Port { get; }
    public Uri Uri { get; }

    private I2pProxyEndpoint(IPAddress address, int port)
    {
        Address = address.ToString();
        Port = port;
        Uri = new Uri($"http://{(address.AddressFamily == AddressFamily.InterNetworkV6 ? "[" + Address + "]" : Address)}:{port}/");
    }

    public static I2pProxyEndpoint Parse(string? value)
    {
        if (string.IsNullOrEmpty(value) || !value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            value.Length > 128 || value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            throw Invalid();

        var authority = value[7..];
        if (authority.EndsWith('/')) authority = authority[..^1];
        var colon = authority.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(authority.AsSpan(colon + 1), NumberStyles.None,
                CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
            throw Invalid();

        var host = authority[..colon];
        var ipv6 = host.StartsWith('[') && host.EndsWith(']');
        if (ipv6) host = host[1..^1];
        if (host.Contains('%') || !IPAddress.TryParse(host, out var address) ||
            !IPAddress.IsLoopback(address) || address.IsIPv4MappedToIPv6 ||
            ipv6 != (address.AddressFamily == AddressFamily.InterNetworkV6) ||
            (!ipv6 && !string.Equals(host, address.ToString(), StringComparison.Ordinal)))
            throw Invalid();

        return new I2pProxyEndpoint(address, port);
    }

    // Never hand out a mutable shared IPAddress/IPEndPoint.
    internal IPEndPoint CreateEndPoint() => new(IPAddress.Parse(Address), Port);

    private static I2pProxyConfigurationException Invalid() => new(
        "An explicit local I2P HTTP proxy URI is required, e.g. http://127.0.0.1:4444 or http://[::1]:4444. " +
        "Use a literal loopback address and explicit port; credentials, paths, queries and fragments are not supported.");
}
