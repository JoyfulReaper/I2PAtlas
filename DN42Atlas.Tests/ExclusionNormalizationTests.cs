using System.Net.Sockets;
using DN42Atlas.Policy;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DN42Atlas.Tests;

[TestClass]
public sealed class ExclusionNormalizationTests
{
    [TestMethod]
    public void DomainIsLowercasedTrimmedAndTrailingDotRemoved()
    {
        Assert.AreEqual(
            "example.dn42",
            ExclusionResourceNormalizer.NormalizeDomain(" Example.DN42. "));
    }

    [TestMethod]
    [DataRow("*.example.dn42")]
    [DataRow("example.com")]
    public void InvalidSelfServiceDomainIsRejected(string value)
    {
        Assert.ThrowsExactly<FormatException>(
            () => ExclusionResourceNormalizer.NormalizeDomain(value));
    }

    [TestMethod]
    public void PrefixesAreValidatedAndCanonicalized()
    {
        Assert.AreEqual(
            "172.20.1.0/24",
            ExclusionResourceNormalizer.NormalizePrefix(
                "172.20.1.77/24",
                AddressFamily.InterNetwork));
        Assert.AreEqual(
            "fd00:1234::/48",
            ExclusionResourceNormalizer.NormalizePrefix(
                "fd00:1234:0:1::1/48",
                AddressFamily.InterNetworkV6));
    }

}
