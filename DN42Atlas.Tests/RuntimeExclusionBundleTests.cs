using DN42Atlas.Policy;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DN42Atlas.Tests;

[TestClass]
public sealed class RuntimeExclusionBundleTests
{

    [TestMethod]
    public async Task RepeatedAtomicMaterializationLeavesCompleteValidJson()
    {
        using var files = new TestFiles();

        await RuntimeExclusionBundle.WriteAtomicAsync(
            files.RuntimePath,
            ["first.dn42"],
            ["172.20.0.0/24"]);
        await RuntimeExclusionBundle.WriteAtomicAsync(
            files.RuntimePath,
            ["second.dn42"],
            ["fd00::/8"]);

        var bundle = RuntimeExclusionBundle.Load(files.RuntimePath);
        CollectionAssert.AreEqual(
            new[] { "second.dn42" },
            bundle.HostRules.ToArray());
        CollectionAssert.AreEqual(
            new[] { "fd00::/8" },
            bundle.PrefixRules.ToArray());
        Assert.IsEmpty(Directory.GetFiles(files.DirectoryPath, "*.tmp"));
    }

    [TestMethod]
    public void MissingConfiguredBundleFailsClosed()
    {
        using var files = new TestFiles();

        Assert.ThrowsExactly<FileNotFoundException>(
            () => files.LoadPolicy(files.RuntimePath));
    }

    [TestMethod]
    [DataRow("not json")]
    [DataRow("{\"version\":2,\"hostRules\":[],\"prefixRules\":[]}")]
    [DataRow("{\"version\":1,\"hostRules\":null,\"prefixRules\":[]}")]
    [DataRow("{\"version\":1,\"hostRules\":[\"*.example.dn42\"],\"prefixRules\":[]}")]
    [DataRow("{\"version\":1,\"hostRules\":[],\"prefixRules\":[\"bad\"]}")]
    public void InvalidBundleFailsClosed(string json)
    {
        using var files = new TestFiles();
        File.WriteAllText(files.RuntimePath, json);

        Assert.ThrowsExactly<InvalidDataException>(
            () => files.LoadPolicy(files.RuntimePath));
    }

    [TestMethod]
    public async Task ManualAndRuntimeRulesCombineAndDeduplicate()
    {
        using var files = new TestFiles(
            "manual.dn42\nduplicate.dn42\n",
            "172.20.0.0/24\n");
        await RuntimeExclusionBundle.WriteAtomicAsync(
            files.RuntimePath,
            ["runtime.dn42", "DUPLICATE.DN42."],
            ["fd00::/8", "172.20.0.1/24"]);

        var policy = files.LoadPolicy(files.RuntimePath);

        Assert.IsTrue(policy.IsHostExcluded("manual.dn42"));
        Assert.IsTrue(policy.IsHostExcluded("runtime.dn42"));
        Assert.IsTrue(policy.IsAddressExcluded("172.20.0.2"));
        Assert.IsTrue(policy.IsAddressExcluded("fd42::1"));
        Assert.HasCount(3, policy.HostRules);
        Assert.HasCount(2, policy.PrefixRules);
    }

    [TestMethod]
    public void OmittingRuntimeBundlePreservesManualOnlyBehavior()
    {
        using var files = new TestFiles("manual.dn42", "172.20.0.0/24");

        var policy = files.LoadPolicy();

        Assert.IsTrue(policy.IsHostExcluded("manual.dn42"));
        Assert.IsTrue(policy.IsAddressExcluded("172.20.0.1"));
    }
}
