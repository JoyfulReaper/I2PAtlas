using System.Text;
using I2PAtlas.IO;

namespace I2PAtlas.Tests;

[TestClass]
public sealed class PrivateFileTests
{
    [TestMethod]
    public async Task ExclusiveCreationNeverOverwritesExistingContentsWhileOpenOrAfterClose()
    {
        using var files = new TemporaryFiles();
        var path = Path.Combine(files.DirectoryPath, "private.tmp");
        var payload = Encoding.UTF8.GetBytes("complete private payload");
        await using (var stream = PrivateFile.CreateNew(path))
        {
            Assert.IsTrue(stream.CanWrite);
            Assert.IsFalse(stream.CanRead);
            await stream.WriteAsync(payload);
            await stream.FlushAsync();
            stream.Flush(flushToDisk: true);
            Assert.ThrowsExactly<IOException>(() => PrivateFile.CreateNew(path));
            if (OperatingSystem.IsWindows())
                Assert.ThrowsExactly<IOException>(() => File.OpenRead(path));
        }
        CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(path));
        Assert.ThrowsExactly<IOException>(() => PrivateFile.CreateNew(path));
        CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(path));
        // Closure releases the handle required by the I2P writers' staged replacement.
        var final = files.Write("final.json", "previous complete output");
        File.Move(path, final, overwrite: true);
        CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(final));
        Assert.IsFalse(File.Exists(path));
    }

    [TestMethod]
    public void UnixPrivateCreationIsOwnerReadWriteOnly()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Unix creation modes are exercised on Unix; Windows inherits directory ACLs.");
            return;
        }
        using var files = new TemporaryFiles();
        var path = Path.Combine(files.DirectoryPath, "private.tmp");
        using (PrivateFile.CreateNew(path))
            Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }
}
