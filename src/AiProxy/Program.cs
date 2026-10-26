using System.Runtime.InteropServices;
using AiProxy;
using AiProxy.Cli;
using AiProxy.Hosting;
using AiProxy.Mcp;
using Titanium.Web.Proxy.Network;

if (GuardCommand.IsRequested(args))
{
    // The guard outlives Ctrl-C and terminal hangups in the owner's process group; it ends when the owner's pipe does.
    using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, context => context.Cancel = true);
    using var hangup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, context => context.Cancel = true);
    using var quit = PosixSignalRegistration.Create(PosixSignal.SIGQUIT, context => context.Cancel = true);
    using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => context.Cancel = true);
    CertificateManager.SuppressInteractiveRootStoreMutations = true;
    using var input = new StreamReader(Console.OpenStandardInput());
    return new GuardCommand(args[1], TitaniumSystemProxyRegistrar.ClearSystemProxy, TimeSpan.FromSeconds(5)).Run(input);
}

var commands = new (string Name, string Description, Func<string[], Task<int>> Run)[]
{
    (CliProcess.Command, "Show compression savings", options => new CliProcess().RunAsync([CliProcess.Command, .. options])),
    (McpServerProcess.Command, "Run the retrieval MCP server", options => new McpServerProcess().RunAsync(options)),
    (ClaudeProcess.Command, "Launch Claude Code through the running proxy", options => new ClaudeProcess().RunAsync(options))
};

if (args is ["-h" or "--help"])
{
    Console.WriteLine("Usage: ai-proxy [server options]\n\nWithout a command, start the proxy. Commands:");
    foreach (var command in commands)
    {
        Console.WriteLine($"  ai-proxy {command.Name,-8} {command.Description}");
    }
    Console.WriteLine("\nUse ai-proxy <command> --help for command options.");
    return 0;
}

if (args is [var name, .. var options])
{
    foreach (var command in commands)
    {
        if (command.Name == name)
        {
            return await command.Run(options);
        }
    }
}

var builder = WebApplication.CreateBuilder(args);

var app = ProxyApplication.Create(builder);

app.Run();
return 0;
