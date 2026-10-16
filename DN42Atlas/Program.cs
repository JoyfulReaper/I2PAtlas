using DN42Atlas.Commands;
using DN42Atlas.Policy;
using DN42Atlas.Probing;
using DN42Atlas.Scanning;
using DN42Atlas.Publishing;

var command = args.FirstOrDefault() ?? "resolve";
if (command is "--help" or "-h" or "help")
{
    CommandUsage.Print();
    return 0;
}

if (command is not ("resolve" or "web-scan" or "report" or "probe-test" or "run" or "registry-update" or "republish" or "publish-existing" or "import-seeds" or "i2p-probe-test" or "i2p-scan" or "i2p-project" or "i2p-view"))
{
    CommandUsage.Print();
    return 2;
}

try
{
    // Public-only reporting must not initialize private I2P or DN42 services.
    if (command == "i2p-view")
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try { return await I2pViewCommand.ExecuteAsync(args, cancellationToken: cancellation.Token); }
        finally { Console.CancelKeyPress -= cancel; }
    }

    // Pure local I2P input transformation needs no DN42 policy, lock, registry or scanner.
    if (command == "import-seeds")
        return await ImportSeedsCommand.ExecuteAsync(args);

    // Explicit I2P integration diagnostic; never construct DN42 policy/discovery.
    if (command == "i2p-probe-test")
        return await I2pProbeTestCommand.ExecuteAsync(args);

    if (command == "i2p-project")
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try { return await I2pProjectCommand.ExecuteAsync(args, cancellationToken: cancellation.Token); }
        finally { Console.CancelKeyPress -= cancel; }
    }

    if (command == "i2p-scan")
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try { return await I2pScanCommand.ExecuteAsync(args, cancellationToken: cancellation.Token); }
        finally { Console.CancelKeyPress -= cancel; }
    }

    if (command == "publish-existing" && args.Length != 2)
    {
        PublishExistingCommand.PrintUsage();
        return 2;
    }
    if (command == "registry-update")
        return await RegistryUpdateCommand.ExecuteAsync();

    using var operation = command is "run" or "republish" or "publish-existing"
        ? await AtlasOperationLock.AcquireAsync(PublicationState.ConfiguredPath,
            Path.Combine(Environment.CurrentDirectory, "published"),
            onContention: () => Console.Error.WriteLine("Waiting for Atlas operation lock.")) : null;
    var exclusionPolicy = ExclusionPolicy.Load(
        Path.Combine(Environment.CurrentDirectory, "config", "excluded-hosts.txt"),
        Path.Combine(Environment.CurrentDirectory, "config", "excluded-prefixes.txt"),
        Environment.GetEnvironmentVariable("DN42ATLAS_RUNTIME_EXCLUSIONS_PATH"));
    if (command == "republish")
        return await new RepublishCommand(exclusionPolicy,
            Path.Combine(Environment.CurrentDirectory, "published"), PublicationState.ConfiguredPath).ExecuteAsync();
    if (command == "publish-existing")
        return await new PublishExistingCommand(exclusionPolicy,
            Path.Combine(Environment.CurrentDirectory, "published"), PublicationState.ConfiguredPath).ExecuteAsync(args);
    var probeTargets = HttpProbeTargets.All;
    var resolve = new ResolveCommand(new RegistryResolver(exclusionPolicy));
    var webScan = new WebScanCommand(new WebScanner(exclusionPolicy, probeTargets), probeTargets, exclusionPolicy);

    switch (command)
    {
        case "report":
            return await ReportCommand.ExecuteAsync(args, exclusionPolicy);
        case "probe-test":
            await new ProbeTestCommand(probeTargets, exclusionPolicy)
                .ExecuteAsync(args.ElementAtOrDefault(1) ?? "burble.dn42");
            return 0;
        case "web-scan":
            return await webScan.ExecuteAsync(args) != null ? 0 : 1;
        case "run":
            return await new RunCommand(resolve, webScan, exclusionPolicy).ExecuteAsync();
        default:
            await resolve.ExecuteAsync();
            return 0;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
