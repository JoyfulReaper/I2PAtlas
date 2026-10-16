using I2PAtlas.Robots;

namespace I2PAtlas.Tests;

[TestClass]
public sealed class RobotsPolicyTests
{
    [TestMethod]
    [DataRow("", "/", true)]
    [DataRow("# comment only", "/", true)]
    [DataRow("User-agent: *\nAllow: /", "/", true)]
    [DataRow("User-agent: *\nDisallow: /", "/", false)]
    [DataRow("User-agent: Other\nDisallow: /", "/", true)]
    [DataRow("User-agent: I2PAtlas\nDisallow: /\nUser-agent: *\nAllow: /", "/", false)]
    [DataRow("User-agent: i2patlas\nDisallow: /\nUser-agent: *\nAllow: /", "/", false)]
    [DataRow("User-agent: *\nDisallow: /private\nAllow: /private/public", "/private/public/page", true)]
    [DataRow("User-agent: *\nDisallow: /private\nAllow: /private", "/private", true)]
    [DataRow("User-agent: *\nDisallow: /*.secret$", "/file.secret", false)]
    [DataRow("User-agent: *\nDisallow: /*.secret$", "/file.secret/other", true)]
    [DataRow("\uFEFFUser-agent: *\r\nDisallow: / # explanation\r\n", "/", false)]
    public void SharedPolicyRetainsAgentSelectionMatchingAndAllowTieBehavior(string robots, string path, bool expected)
    {
        Assert.AreEqual(expected, RobotsPolicy.IsPathAllowed(robots, "I2PAtlas", path));
    }

    [TestMethod]
    [DataRow("not robots")]
    [DataRow("User-agent:\nDisallow: /")]
    [DataRow("User-agent: *\nDisallow: relative")]
    [DataRow("User-agent: *\nAllow: relative")]
    public void MalformedPolicyRemainsUnknown(string robots)
    {
        Assert.IsNull(RobotsPolicy.IsPathAllowed(robots, "I2PAtlas", "/"));
    }

    [TestMethod]
    public void SharedPolicyHonorsCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            RobotsPolicy.IsPathAllowed("User-agent: *\nAllow: /", "I2PAtlas", "/", cancellation.Token));
    }
}
