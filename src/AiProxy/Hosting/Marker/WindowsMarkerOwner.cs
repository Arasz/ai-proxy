using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AiProxy.Hosting.Marker;

[SupportedOSPlatform("windows")]
internal static partial class WindowsMarkerOwner
{
    private const int MoreData = 234;
    private const int SessionKeyLength = 33;

    internal static unsafe int? Find(string path)
    {
        var key = stackalloc char[SessionKeyLength];
        if (RmStartSession(out var session, 0, key) is not 0)
        {
            return null;
        }

        try
        {
            fixed (char* resource = path)
            {
                var resources = stackalloc char*[1];
                resources[0] = resource;
                if (RmRegisterResources(session, 1, resources, 0, 0, 0, 0) is not 0)
                {
                    return null;
                }
            }

            uint count = 0;
            var result = RmGetList(session, out var needed, ref count, null, out _);
            if (result is not MoreData || needed is not 1)
            {
                return null;
            }

            var entry = new ProcessInfo();
            count = 1;
            result = RmGetList(session, out _, ref count, &entry, out _);
            return result == 0 && count == 1 && entry.Process.Pid > 0 ? entry.Process.Pid : null;
        }
        finally
        {
            var rmEndSessionResult = RmEndSession(session);
            if (rmEndSessionResult != 0)
            {
            }
        }
    }

    [LibraryImport(KnownNames.MarkerDiscovery.RestartManager)]
    private static unsafe partial int RmStartSession(out uint session, uint flags, char* key);

    [LibraryImport(KnownNames.MarkerDiscovery.RestartManager)]
    private static unsafe partial int RmRegisterResources(uint session, uint files, char** paths, uint applications, nint processes, uint services, nint serviceNames);

    [LibraryImport(KnownNames.MarkerDiscovery.RestartManager)]
    private static unsafe partial int RmGetList(uint session, out uint needed, ref uint count, ProcessInfo* processes, out uint reasons);

    [LibraryImport(KnownNames.MarkerDiscovery.RestartManager)]
    private static partial int RmEndSession(uint session);

    [StructLayout(LayoutKind.Sequential)]
    private struct UniqueProcess
    {
        internal int Pid;
        internal uint StartLow;
        internal uint StartHigh;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct ProcessInfo
    {
        internal UniqueProcess Process;
        internal fixed char AppName[256];
        internal fixed char ServiceName[64];
        internal uint ApplicationType;
        internal uint AppStatus;
        internal uint SessionId;
        internal int Restartable;
    }
}
