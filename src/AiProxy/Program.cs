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

if (args is [CliProcess.Command, ..])
{
    return await new CliProcess().RunAsync(args);
}

if (args is [McpServerProcess.Command, .. var options])
{
    return await new McpServerProcess().RunAsync(options);
}

var builder = WebApplication.CreateBuilder(args);

var app = ProxyApplication.Create(builder);

app.Run();
return 0;
