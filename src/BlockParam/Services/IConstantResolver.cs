namespace BlockParam.Services;

/// <summary>
/// Resolves PLC constant names to integer values. Used to expand array bounds
/// like <c>Array[1..MAX_VALVES]</c> into concrete indices.
/// </summary>
public interface IConstantResolver
{
    /// <summary>
    /// Tries to resolve a constant name to an integer value.
    /// Returns false if the constant is unknown or its value is not an integer.
    /// </summary>
    bool TryResolve(string name, out int value);
}

/// <summary>
/// Default resolver backed by <see cref="TagTableCache"/>.
/// </summary>
public class TagTableConstantResolver : IConstantResolver
{
    private readonly TagTableCache _cache;

    public TagTableConstantResolver(TagTableCache cache)
    {
        _cache = cache;
    }

    public bool TryResolve(string name, out int value)
        => _cache.TryGetConstantValue(name, out value);
}
