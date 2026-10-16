namespace DN42Atlas.Tests;

[TestClass]
public sealed class TemporaryFilesTests
{
    [TestMethod]
    public void GenericFixtureCreatesOnlyRequestedFilesAndOwnsDirectoryCleanup()
    {
        string directory;
        using (var files = new TemporaryFiles())
        {
            directory = files.DirectoryPath;
            Assert.IsTrue(Directory.Exists(directory));
            Assert.IsEmpty(Directory.GetFileSystemEntries(directory));
            var path = files.Write("seeds.json", "private input");
            Assert.AreEqual(Path.Combine(directory, "seeds.json"), path);
            Assert.AreEqual("private input", File.ReadAllText(path));
            CollectionAssert.AreEqual(new[] { path }, Directory.GetFileSystemEntries(directory));
        }
        Assert.IsFalse(Directory.Exists(directory));
    }
}
