using System.Security.Cryptography;
using System.Text;

namespace BlockParam.Services;

/// <summary>
/// Derives a stable, filesystem-safe identifier from a TIA project path. Used to
/// scope per-project on-disk caches (tag tables, UDT types) so that a second TIA
/// instance or a project switch cannot serve files from a different project —
/// see issue #14.
/// </summary>
public static class ProjectScope
{
    private const int HashBytes = 8;
    private const string Fallback = "default";

    public static string ForPath(string? projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
            return Fallback;

        var normalized = projectPath!.Trim().ToLowerInvariant();
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(normalized));
        var hex = new StringBuilder(HashBytes * 2);
        for (int i = 0; i < HashBytes; i++)
            hex.Append(bytes[i].ToString("x2"));
        return hex.ToString();
    }
}
