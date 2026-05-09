namespace BlockParam.Services;

/// <summary>
/// Case-insensitive substring matching across multiple candidate fields.
/// Collapses the four duplicate <c>x.IndexOf(filter, ...) || y.IndexOf(filter, ...)</c>
/// blocks the codebase had grown into (#83).
///
/// Uses <see cref="string.IndexOf(string, StringComparison)"/> rather than
/// <see cref="string.Contains(string, StringComparison)"/> so the assembly
/// does not pull in <c>System.Runtime.CompilerServices.Unsafe</c>, which is
/// missing in the TIA Portal partial-trust host.
/// </summary>
public static class StringMatcher
{
    /// <summary>
    /// Returns true if <paramref name="filter"/> appears as a case-insensitive
    /// substring in any non-null field. An empty / whitespace filter matches
    /// vacuously (returns true) so callers can pass user input directly.
    /// </summary>
    public static bool MatchesAny(string? filter, params string?[]? fields)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        if (fields == null) return false;
        for (int i = 0; i < fields.Length; i++)
        {
            var f = fields[i];
            if (f != null && f.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        return false;
    }
}
