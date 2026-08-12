namespace KeyWars.LoadTesting;

internal static class LoadTestApplication
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Any(value => value.Equals("--self-test", StringComparison.OrdinalIgnoreCase)))
        {
            return await LoadTestSelfTests.RunAsync(cancellationToken);
        }

        if (args.Any(value => value.Equals("--help", StringComparison.OrdinalIgnoreCase) || value.Equals("-h", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine(LoadTestOptions.Usage);
            return 0;
        }

        if (args.Any(value => value.Equals("--signalr", StringComparison.OrdinalIgnoreCase)))
        {
            return await new SignalRLoadRunner(LoadTestOptions.Parse(args)).RunAsync(cancellationToken);
        }

        InMemoryLoadRunner.Run(args);
        return 0;
    }
}
