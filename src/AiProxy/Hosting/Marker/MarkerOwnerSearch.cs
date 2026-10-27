namespace AiProxy.Hosting.Marker;

public sealed class MarkerOwnerSearch : IMarkerOwnerSearch
{
    private static IMarkerOwnerSearch GetSearchForPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsMarkerOwnerSearch();
        }
        return new UnixMarkerOwnerSearch(OperatingSystem.IsMacOS() ? KnownNames.MarkerDiscovery.MacLsof : KnownNames.MarkerDiscovery.LinuxLsof);
    }

    public async ValueTask<int?> FindMarkerOwner(string path) => await GetSearchForPlatform().FindMarkerOwner(path);
}
