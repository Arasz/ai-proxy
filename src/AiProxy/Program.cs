using System.Runtime.InteropServices;
using AiProxy.Cli;
using AiProxy.Hosting;
using Titanium.Web.Proxy.Network;

if (GuardCommand.IsRequested(args))
{
    // The guard outlives Ctrl-C and terminal hangups in the owner's process group; it ends when the owner's pipe does.
    using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, static context => context.Cancel = true);
    using var hangup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, static context => context.Cancel = true);
    using var quit = PosixSignalRegistration.Create(PosixSignal.SIGQUIT, static context => context.Cancel = true);
    using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, static context => context.Cancel = true);
    CertificateManager.SuppressInteractiveRootStoreMutations = true;
    using var input = new StreamReader(Console.OpenStandardInput());
    var lockWait = TimeSpan.FromSeconds(5);
    return new GuardCommand(args[1], TitaniumSystemProxyRegistrar.ClearSystemProxy, lockWait).Run(input);
}

if (CliApplication.IsCommandInvocation(args))
{
    return await CliApplication.RunProcessAsync(args);
}

return await ProxyInstance.RunAsync(args);
