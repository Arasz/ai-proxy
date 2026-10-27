using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace AiProxy.Hosting;

/// <summary>Uses Restart Manager to identify holders of one exact marker file.</summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsLegacyProxy
{
    internal static unsafe int[] Holders(string path)
    {
        if (sizeof(ProcessInfo) != 668)
        {
            throw new IOException("The native Restart Manager process layout is unsupported.");
        }

        uint session;
        var key = stackalloc char[33];
        Check(RmStartSession(out session, 0, key));
        try
        {
            fixed (char* resource = path)
            {
                var resources = stackalloc char*[1];
                resources[0] = resource;
                Check(RmRegisterResources(session, 1, resources, 0, 0, 0, 0));
            }

            uint count = 0;
            uint needed;
            var result = RmGetList(session, out needed, ref count, null, out _);
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (result == 0 && count == 0)
                {
                    return [];
                }

                if (result != 234 || needed > 1024)
                {
                    Check(result);
                    throw new IOException("The marker has too many holders to identify safely.");
                }

                var buffer = new ProcessInfo[needed];
                count = needed;
                fixed (ProcessInfo* entries = buffer)
                {
                    result = RmGetList(session, out needed, ref count, entries, out _);
                }

                if (result != 0)
                {
                    continue;
                }

                var holders = new List<int>();
                foreach (var entry in buffer.AsSpan(0, checked((int)count)))
                {
                    using var process = Process.GetProcessById(entry.Process.Pid);
                    var birth = ((long)entry.Process.StartHigh << 32) | entry.Process.StartLow;
                    if (process.StartTime.ToUniversalTime().ToFileTimeUtc() != birth || !IsCurrentUser(process))
                    {
                        throw new IOException("The marker holder is not a verified same-user process. Stop its owner manually.");
                    }

                    holders.Add(entry.Process.Pid);
                }

                return [.. holders.Distinct()];
            }

            throw new IOException("The system proxy marker holders changed during discovery. Retry restart.");
        }
        finally
        {
            RmEndSession(session);
        }
    }

    internal static bool IsCurrentUser(Process process)
    {
        if (OpenProcessToken(process.Handle, 8, out var token) == 0)
        {
            throw new IOException("Cannot verify the marker holder's user.", new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        using (token)
        using (var owner = new WindowsIdentity(token.DangerousGetHandle()))
        using (var current = WindowsIdentity.GetCurrent())
        {
            return owner.User == current.User;
        }
    }

    private static void Check(int result)
    {
        if (result != 0)
        {
            throw new IOException("Cannot identify the system proxy marker holder.", new Win32Exception(result));
        }
    }

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

    [LibraryImport("rstrtmgr.dll")]
    private static unsafe partial int RmStartSession(out uint session, uint flags, char* key);

    [LibraryImport("rstrtmgr.dll")]
    private static unsafe partial int RmRegisterResources(uint session, uint files, char** paths, uint applications, nint processes, uint services, nint serviceNames);

    [LibraryImport("rstrtmgr.dll")]
    private static unsafe partial int RmGetList(uint session, out uint needed, ref uint count, ProcessInfo* processes, out uint reasons);

    [LibraryImport("rstrtmgr.dll")]
    private static partial int RmEndSession(uint session);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    private static partial int OpenProcessToken(nint process, uint desiredAccess, out SafeAccessTokenHandle token);
}
