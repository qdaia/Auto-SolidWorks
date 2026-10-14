using CadModeling.Ir;

namespace CadModeling.Core;

/// <summary>Exact queries only. Construct anew for every measurement pass, including reopen.</summary>
public sealed class InspectionPassCache<T>
{
    private readonly Dictionary<(int Face, Vector3 Point), Lazy<T>> values = new();

    public T GetOrQuery(int faceIndex, Vector3 point, Func<T> query)
    {
        var key = (faceIndex, point);
        if (!values.TryGetValue(key, out var value))
            values.Add(key, value = new Lazy<T>(query));
        // Lazy retains failures too: an identical failed measurement must remain unverifiable.
        return value.Value;
    }
}
