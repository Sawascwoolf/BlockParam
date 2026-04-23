namespace BlockParam.Licensing;

/// <summary>
/// Result of a license key activation attempt.
/// </summary>
public class LicenseActivationResult
{
    public LicenseActivationStatus Status { get; }
    public string? ErrorMessage { get; }
    public LicenseInfo? LicenseInfo { get; }

    private LicenseActivationResult(LicenseActivationStatus status, string? errorMessage = null, LicenseInfo? licenseInfo = null)
    {
        Status = status;
        ErrorMessage = errorMessage;
        LicenseInfo = licenseInfo;
    }

    public bool IsSuccess => Status == LicenseActivationStatus.Success;

    public static LicenseActivationResult Success(LicenseInfo info) =>
        new(LicenseActivationStatus.Success, licenseInfo: info);

    public static LicenseActivationResult InvalidKey(string message = "Invalid license key") =>
        new(LicenseActivationStatus.InvalidKey, message);

    public static LicenseActivationResult TooManySessions(int current, int max) =>
        new(LicenseActivationStatus.TooManySessions,
            $"Too many concurrent sessions ({current}/{max})");

    public static LicenseActivationResult ServerError(string message = "Cannot reach license server") =>
        new(LicenseActivationStatus.ServerError, message);
}

public enum LicenseActivationStatus
{
    Success,
    InvalidKey,
    TooManySessions,
    ServerError
}
