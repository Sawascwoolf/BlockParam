namespace BlockParam.Licensing;

/// <summary>
/// Snapshot of the current license state, returned by <see cref="ILicenseService"/>.
/// </summary>
public class LicenseInfo
{
    public LicenseTier Tier { get; set; }
    public string? LicenseKey { get; set; }
    public DateTime? ValidUntil { get; set; }
    public int MaxConcurrent { get; set; }
    public int CurrentConcurrent { get; set; }
    public bool IsServerReachable { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// True when the active key was sourced from a machine-wide managed file
    /// (e.g. <c>%PROGRAMDATA%\BlockParam\license.key</c>) pushed by IT
    /// deployment tooling. The user should not edit or remove the key locally —
    /// the dialog surfaces a read-only hint in this case.
    /// </summary>
    public bool IsManagedKey { get; set; }

    /// <summary>Absolute path of the managed key file when <see cref="IsManagedKey"/> is true.</summary>
    public string? ManagedKeyFilePath { get; set; }
}
