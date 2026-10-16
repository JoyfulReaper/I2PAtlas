using DN42Atlas.I2pPublishing;

namespace DN42Atlas.Commands;

public static class I2pProjectCommand
{
    public static async Task<int> ExecuteAsync(string[] args, TextWriter? output = null, TextWriter? error = null,
        CancellationToken cancellationToken = default)
    {
        output ??= Console.Out;
        error ??= Console.Error;
        if (args.Length != 3 || args.Skip(1).Any(string.IsNullOrWhiteSpace))
        {
            await output.WriteLineAsync("Usage: dn42atlas i2p-project <private-raw-file> <public-output-file>");
            return 2;
        }
        var installed = false;
        try
        {
            var result = await new I2pProjectionService().ProjectAsync(args[1], args[2], cancellationToken);
            installed = true;
            await output.WriteLineAsync($"Public I2P projection installed: {result.SeedCount} entries, {result.UniqueHostnameCount} friendly hostnames. Proxy observations remain unauthenticated.");
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await error.WriteLineAsync("I2P projection cancelled; no new public artifact was installed.");
            return 130;
        }
        catch (ArgumentException)
        {
            await error.WriteLineAsync("Invalid projection paths; use separate explicit local input/output files without filesystem links.");
            return 2;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            await error.WriteLineAsync(installed ? "Public artifact was installed, but writing the summary failed."
                : "I2P projection failed: invalid private raw artifact, resource limit or output I/O failure. Previous public output was preserved.");
            return 1;
        }
    }
}
