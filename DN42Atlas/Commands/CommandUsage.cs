namespace DN42Atlas.Commands;

public static class CommandUsage
{
    public static void Print()
    {
        Console.WriteLine("Usage: dn42atlas [command]");
        Console.WriteLine("  i2p-project <private-raw-file> <public-output-file> Validate raw I2P evidence and write allowlisted public JSON");
        Console.WriteLine("  import-seeds <addressbook-file> <snapshot-file> Import local I2P seeds into private JSON state");
        Console.WriteLine("  i2p-probe-test <snapshot-file> <hostname.i2p> <proxy-uri> [--timeout-ms <1-60000>] Private single-seed robots diagnostic");
        Console.WriteLine("  i2p-scan <snapshot-file> <private-output-file> <proxy-uri> [--concurrency <1-4>] [--timeout-ms <1-60000>] [--max-seeds <1-10000>] Private bounded I2P scan");
        Console.WriteLine("  resolve                         Registry parsing and DNS resolution (default)");
        Console.WriteLine("  web-scan [resolution-file]       HTTP/HTTPS scan and HTML report");
        Console.WriteLine("  report <web-probe.json>          Generate an HTML report");
        Console.WriteLine("  probe-test [hostname]           Single-host HTTP/HTTPS probe test");
        Console.WriteLine("  run                             Resolve, scan, generate HTML, and publish stable files");
        Console.WriteLine("  registry-update                 Safely refresh the configured local registry checkout");
        Console.WriteLine("  republish                       Rebuild current public artifacts without crawling");
        Console.WriteLine("  publish-existing <web-probe.json> Publish an explicitly selected raw scan without crawling");
        Console.WriteLine("  --help, -h, help                 Show this usage");
    }
}
