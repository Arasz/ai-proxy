using AiProxy.Cli;
using AiProxy.Client;
using Spectre.Console;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
var runtime = new GainRuntime(AnsiConsole.Console, Console.Out, Console.Error,
    ProxyClient.CreateHttpClient, Environment.CurrentDirectory,
    !Console.IsInputRedirected && !Console.IsOutputRedirected, TimeProvider.System,
    AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Error) }));
return await new CliApplication(runtime).RunAsync(args, cancellation.Token);
