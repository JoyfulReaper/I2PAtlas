namespace DN42Atlas.Commands;

public static class CommandUsage
{
    public static void Print()
    {
        Console.WriteLine("Usage: dn42atlas [command]");
        Console.WriteLine("  import-seeds <addressbook-file> <snapshot-file> Import local I2P seeds into private JSON state");
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
