using System.Globalization;

namespace BlockParam.Updates;

/// <summary>
/// Parsed semver-ish release tag (e.g. <c>v0.4.0</c>, <c>0.4.0-rc1</c>).
/// Compares using the SemVer 2.0 precedence rule: any pre-release sorts
/// BEFORE the matching plain version, and pre-release ids compare
/// numerically when both sides parse as integers, lexicographically
/// otherwise. The leading <c>v</c> is optional and stripped on parse.
/// </summary>
public sealed class VersionTag : IComparable<VersionTag>, IEquatable<VersionTag>
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    /// <summary>Empty for stable releases; e.g. "rc1", "beta.2".</summary>
    public string PreRelease { get; }
    /// <summary>Original input — preserved so callers can echo it back ("v0.4.0").</summary>
    public string Raw { get; }

    public bool IsPreRelease => PreRelease.Length > 0;

    private VersionTag(int major, int minor, int patch, string preRelease, string raw)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        PreRelease = preRelease;
        Raw = raw;
    }

    public static bool TryParse(string? input, out VersionTag tag)
    {
        tag = default!;
        if (string.IsNullOrWhiteSpace(input)) return false;

        var s = input!.Trim();
        var raw = s;
        if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V')) s = s.Substring(1);

        var preIdx = s.IndexOf('-');
        var pre = "";
        if (preIdx >= 0)
        {
            pre = s.Substring(preIdx + 1);
            s = s.Substring(0, preIdx);
        }

        // Strip build metadata (+...) — irrelevant for ordering per SemVer.
        var buildIdx = pre.IndexOf('+');
        if (buildIdx >= 0) pre = pre.Substring(0, buildIdx);

        var plusIdx = s.IndexOf('+');
        if (plusIdx >= 0) s = s.Substring(0, plusIdx);

        var parts = s.Split('.');
        if (parts.Length is < 1 or > 3) return false;

        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int major))
            return false;
        int minor = 0, patch = 0;
        if (parts.Length >= 2 &&
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out minor))
            return false;
        if (parts.Length >= 3 &&
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out patch))
            return false;

        tag = new VersionTag(major, minor, patch, pre, raw);
        return true;
    }

    public static VersionTag FromSystemVersion(System.Version version, bool preserveRaw = false)
    {
        return new VersionTag(
            version.Major,
            Math.Max(0, version.Minor),
            Math.Max(0, version.Build),
            preRelease: "",
            raw: preserveRaw
                ? version.ToString()
                : $"{version.Major}.{Math.Max(0, version.Minor)}.{Math.Max(0, version.Build)}");
    }

    public int CompareTo(VersionTag? other)
    {
        if (other is null) return 1;
        int c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;

        // SemVer: stable > pre-release.
        if (PreRelease.Length == 0 && other.PreRelease.Length == 0) return 0;
        if (PreRelease.Length == 0) return 1;
        if (other.PreRelease.Length == 0) return -1;

        return ComparePreRelease(PreRelease, other.PreRelease);
    }

    private static int ComparePreRelease(string a, string b)
    {
        var ap = a.Split('.');
        var bp = b.Split('.');
        int len = Math.Min(ap.Length, bp.Length);
        for (int i = 0; i < len; i++)
        {
            bool aNum = int.TryParse(ap[i], NumberStyles.None, CultureInfo.InvariantCulture, out int an);
            bool bNum = int.TryParse(bp[i], NumberStyles.None, CultureInfo.InvariantCulture, out int bn);
            if (aNum && bNum)
            {
                int c = an.CompareTo(bn);
                if (c != 0) return c;
            }
            else if (aNum) return -1;   // numeric < alphanumeric
            else if (bNum) return 1;
            else
            {
                int c = string.CompareOrdinal(ap[i], bp[i]);
                if (c != 0) return c;
            }
        }
        return ap.Length.CompareTo(bp.Length);
    }

    public bool Equals(VersionTag? other) => other is not null && CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is VersionTag other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(Major, Minor, Patch, PreRelease);

    public override string ToString() =>
        IsPreRelease
            ? $"{Major}.{Minor}.{Patch}-{PreRelease}"
            : $"{Major}.{Minor}.{Patch}";
}
