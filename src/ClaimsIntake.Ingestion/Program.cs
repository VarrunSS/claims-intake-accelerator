using ClaimsIntake.Ingestion;

// Loads the policy wordings into Azure AI Search. See IngestionCli.Usage, and docs/adr/0006-policy-wording-rag.md.
if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine(IngestionCli.Usage);
    return args.Length == 0 ? 2 : 0;
}

try
{
    using var cancel = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cancel.Cancel();
    };
    return await IngestionCli.RunAsync(IngestionCli.Parse(args), Console.Out, cancel.Token);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine(IngestionCli.Usage);
    return 2;
}
catch (Exception ex) when (ex is IOException or InvalidDataException or Azure.RequestFailedException or Azure.Identity.AuthenticationFailedException)
{
    Console.Error.WriteLine($"Ingestion failed: {ex.GetType().Name}: {ex.Message}");
    return 1;
}
