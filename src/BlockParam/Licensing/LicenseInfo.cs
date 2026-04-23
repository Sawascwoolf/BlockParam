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
}
