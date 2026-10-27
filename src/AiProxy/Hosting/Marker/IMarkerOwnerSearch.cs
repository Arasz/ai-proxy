namespace AiProxy.Hosting.Marker;

public interface IMarkerOwnerSearch
{
    ValueTask<int?> FindMarkerOwner(string path);
}
