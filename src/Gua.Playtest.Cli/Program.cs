using Gua.Playtest.Cli;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
return await CliApplication.ExecuteAsync(args, Console.Out, cancellation.Token);
