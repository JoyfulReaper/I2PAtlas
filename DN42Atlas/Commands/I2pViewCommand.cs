using DN42Atlas.I2pReporting;

namespace DN42Atlas.Commands;

public static class I2pViewCommand
{
    public static async Task<int> ExecuteAsync(string[] args, TextWriter? output = null, TextWriter? error = null,
        CancellationToken cancellationToken = default)
    {
        output ??= Console.Out;
        error ??= Console.Error;
        if (args.Length != 3 || args.Skip(1).Any(string.IsNullOrWhiteSpace))
        {
            await output.WriteLineAsync("Usage: dn42atlas i2p-view <public-input-file> <html-output-file>");
            return 2;
        }
        var installed = false;
        try
        {
            var scan = await new I2pViewerGenerator().GenerateAsync(args[1], args[2], cancellationToken);
            installed = true;
            await output.WriteLineAsync($"I2PAtlas viewer installed: {scan.SeedCount} destination entries, {scan.UniqueHostnameCount} friendly hostnames.");
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await error.WriteLineAsync("I2P viewer cancelled; previous HTML preserved.");
            return 130;
        }
        catch (DirectoryNotFoundException)
        {
            await error.WriteLineAsync("Viewer output directory does not exist, or the public input directory is missing. Create the output directory first.");
            return 1;
        }
        catch (FileNotFoundException)
        {
            await error.WriteLineAsync("Public input file was not found; previous HTML preserved.");
            return 1;
        }
        catch (ArgumentException)
        {
            await error.WriteLineAsync("Use separate explicit local input/output files without filesystem links.");
            return 2;
        }
        catch (InvalidDataException ex)
        {
            await error.WriteLineAsync("Invalid public artifact: " + ex.Message + " Previous HTML preserved.");
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            await error.WriteLineAsync(installed ? "Viewer installed, but writing its summary failed." :
                "I2P viewer I/O failure; previous HTML preserved.");
            return 1;
        }
    }
}
