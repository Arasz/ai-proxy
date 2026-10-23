using System.Runtime.InteropServices;
using Titanium.Web.Proxy.Network;

namespace AiProxy.Hosting;

/// <summary>The <c>--guard-system-proxy</c> mode: no host, only a wait on stdin and a clear if the owner died.</summary>
public static class GuardCommand
{
    /// <summary>The first argument that selects guard mode; the second is the data directory.</summary>
    public const string Argument = "--guard-system-proxy";

    /// <summary>True when <paramref name="args"/> ask for guard mode.</summary>
    public static bool IsRequested(string[] args) => args is [Argument, _];

    /// <summary>Runs the guard on standard input and returns the process exit code.</summary>
    public static int Run(string[] args)
    {
        var dataDirectory = args[1];
        using var interrupt = Ignore(PosixSignal.SIGINT);
        using var hangup = Ignore(PosixSignal.SIGHUP);
        using var quit = Ignore(PosixSignal.SIGQUIT);
        CertificateManager.SuppressInteractiveRootStoreMutations = true;
        try
        {
            using var input = new StreamReader(Console.OpenStandardInput());
            if (new SystemProxyGuard(TitaniumSystemProxyRegistrar.ClearSystemProxy).Run(input))
            {
                SystemProxyMarker.TryDelete(dataDirectory);
            }

            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"ai-proxy guard could not clear the system proxy: {exception.Message}");
            return 1;
        }
    }

    private static PosixSignalRegistration Ignore(PosixSignal signal) =>
        PosixSignalRegistration.Create(signal, context => context.Cancel = true);
}
