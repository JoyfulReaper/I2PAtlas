using DN42Atlas.Seeds;

namespace DN42Atlas.Commands;

public static class ImportSeedsCommand
{
    public static void PrintUsage() => Console.WriteLine("Usage: dn42atlas import-seeds <addressbook-file> <snapshot-file>");

    public static async Task<int> ExecuteAsync(string[] args)
    {
        if (args.Length != 3 || string.IsNullOrWhiteSpace(args[1]) || string.IsNullOrWhiteSpace(args[2]))
        {
            PrintUsage();
            return 2;
        }

        var snapshot = await new SeedSnapshotImporter().ImportAsync(args[1], args[2]);
        Console.WriteLine($"Imported {snapshot.Seeds.Length} I2P seeds to: {Path.GetFullPath(args[2])}");
        return 0;
    }
}
