namespace I2PAtlas.I2pTransport;

internal enum ProbeResource { Robots, Root }

/// <summary>URI authority and Host constructed together; no caller-supplied URL, path or headers.</summary>
internal sealed class I2pProbeRequest
{
    internal Uri RequestUri { get; }
    internal string HostHeader { get; }

    internal I2pProbeRequest(I2pProbeTarget target, ProbeResource resource)
    {
        ArgumentNullException.ThrowIfNull(target);
        var path = resource switch
        {
            ProbeResource.Robots => "/robots.txt",
            ProbeResource.Root => "/",
            _ => throw new ArgumentOutOfRangeException(nameof(resource))
        };
        HostHeader = target.RequestHostname;
        RequestUri = new Uri($"http://{HostHeader}{path}");
    }
}
