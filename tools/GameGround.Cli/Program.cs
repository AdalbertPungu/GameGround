using GameGround.Cli;

if (args.Length > 0 && args[0] == "evaluate-models")
{
    using var cancellation = new CancellationTokenSource();
    ConsoleCancelEventHandler handler = (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellation.Cancel();
    };
    Console.CancelKeyPress += handler;
    try
    {
        return await ModelEvaluationCommand.RunAsync(args[1..], Console.Out, Console.Error, cancellation.Token);
    }
    finally
    {
        Console.CancelKeyPress -= handler;
    }
}

return DatasetCommand.Run(args, Console.Out, Console.Error);
