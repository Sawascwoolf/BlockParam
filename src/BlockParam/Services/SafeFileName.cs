using System.IO;
using System.Linq;

namespace BlockParam.Services;

/// <summary>
/// Maps an arbitrary TIA-derived name (UDT, group, tag table, DB) to a Windows-safe
/// filename. TIA permits characters in symbol names that <see cref="Path.Combine"/>
/// rejects (<c>&lt; &gt; : " | ? *</c> plus control chars). Without sanitization,
/// real customer projects throw <c>ArgumentException("Illegal characters in path")</c>
/// the first time we try to cache one of their UDTs to disk.
///
/// Replacement is per-character and deterministic so the same input always produces
/// the same filename — the on-disk staleness check (<c>File.GetLastWriteTime</c> vs.
/// <c>type.ModifiedDate</c>) keeps working across runs.
/// </summary>
internal static class SafeFileName
{
    private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

    public static string Sanitize(string? name)
    {
        if (string.IsNullOrEmpty(name)) return "_";

        var chars = name!.Select(c => InvalidChars.Contains(c) ? '_' : c).ToArray();
        var cleaned = new string(chars).TrimEnd('.', ' ');

        return cleaned.Length == 0 ? "_" : cleaned;
    }
}
