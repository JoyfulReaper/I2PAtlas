namespace DN42Atlas.Tests;

/// <summary>Generic temporary-directory ownership; no policy, configuration or network setup.</summary>
internal sealed class TemporaryFiles : IDisposable
{
    public string DirectoryPath { get; }

    public TemporaryFiles(string? directoryPath = null)
    {
        DirectoryPath = directoryPath ?? Path.Combine(Path.GetTempPath(), "DN42Atlas-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
    }

    public string Write(string filename, string content)
    {
        var path = Path.Combine(DirectoryPath, filename);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
}
