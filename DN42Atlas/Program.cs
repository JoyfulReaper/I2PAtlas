using DN42Atlas.Commands;

var command = args.FirstOrDefault() ?? "help";
if (command is "--help" or "-h" or "help")
{
    CommandUsage.Print();
    return 0;
}

if (command is not ("import-seeds" or "i2p-probe-test" or "i2p-scan" or "i2p-project" or "i2p-view"))
{
    CommandUsage.Print();
    return 2;
}

try
{
    // Public-only reporting must not initialize private I2P services.
    if (command == "i2p-view")
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try { return await I2pViewCommand.ExecuteAsync(args, cancellationToken: cancellation.Token); }
        finally { Console.CancelKeyPress -= cancel; }
    }

    // Pure local I2P input transformation needs no scanner or transport.
    if (command == "import-seeds")
        return await ImportSeedsCommand.ExecuteAsync(args);

    // Explicit single-seed I2P integration diagnostic.
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

    return 2;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
