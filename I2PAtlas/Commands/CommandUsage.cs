namespace I2PAtlas.Commands;

public static class CommandUsage
{
    public static void Print()
    {
        Console.WriteLine("Usage: i2patlas [command]");
        Console.WriteLine("  i2p-view <public-input-file> <html-output-file> Generate standalone I2PAtlas HTML from public v1 JSON");
        Console.WriteLine("  i2p-project <private-raw-file> <public-output-file> Validate raw I2P evidence and write allowlisted public JSON");
        Console.WriteLine("  import-seeds <addressbook-file> <snapshot-file> Import local I2P seeds into private JSON state");
        Console.WriteLine("  i2p-probe-test <snapshot-file> <hostname.i2p> <proxy-uri> [--timeout-ms <1-60000>] Private single-seed robots diagnostic");
        Console.WriteLine("  i2p-scan <snapshot-file> <private-output-file> <proxy-uri> [--concurrency <1-4>] [--timeout-ms <1-60000>] [--max-seeds <1-10000>] Private bounded I2P scan");
        Console.WriteLine("  --help, -h, help                 Show this usage");
    }
}
