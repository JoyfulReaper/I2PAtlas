using DN42Atlas.Policy;

namespace DN42Atlas.Tests;

// Legacy DN42 policy fixture. I2P tests use TemporaryFiles directly.
internal sealed class TestFiles : IDisposable
{
    private readonly TemporaryFiles temporary;
    public string DirectoryPath => temporary.DirectoryPath;
    public string HostsPath => Path.Combine(DirectoryPath, "excluded-hosts.txt");
    public string PrefixesPath => Path.Combine(DirectoryPath, "excluded-prefixes.txt");
    public string RuntimePath => Path.Combine(DirectoryPath, "runtime-exclusions.json");

    public TestFiles(string hosts = "", string prefixes = "", string? directoryPath = null)
    {
        temporary = new TemporaryFiles(directoryPath);
        File.WriteAllText(HostsPath, hosts);
        File.WriteAllText(PrefixesPath, prefixes);
    }

    public ExclusionPolicy LoadPolicy(string? runtimePath = null) =>
        ExclusionPolicy.Load(HostsPath, PrefixesPath, runtimePath);

    public string Write(string filename, string content) => temporary.Write(filename, content);

    public void Dispose() => temporary.Dispose();
}
