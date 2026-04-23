using System.Threading.Tasks;

namespace BlockParam.Licensing;

/// <summary>
/// Manages license key lifecycle, server communication, and tier determination.
/// Separated from <see cref="IUsageTracker"/> to keep concerns clean:
/// IUsageTracker handles daily counting, ILicenseService handles license validation.
/// </summary>
public interface ILicenseService : IDisposable
{
    /// <summary>Returns a snapshot of the current license state.</summary>
    LicenseInfo GetLicenseInfo();

    /// <summary>Current license tier (Free or Pro).</summary>
    LicenseTier CurrentTier { get; }

    /// <summary>True if Pro tier is currently active and validated.</summary>
    bool IsProActive { get; }

    /// <summary>Activates a license key by contacting the server.</summary>
    Task<LicenseActivationResult> ActivateKeyAsync(string licenseKey);

    /// <summary>Removes the stored license key and reverts to Free tier.</summary>
    void DeactivateKey();

    /// <summary>Starts the periodic heartbeat timer. Call once when dialog opens.</summary>
    void StartHeartbeat();

    /// <summary>Stops the heartbeat timer and sends deactivation. Call when dialog closes.</summary>
    void StopHeartbeat();

    /// <summary>Raised when the license state changes (e.g., after heartbeat or activation).</summary>
    event EventHandler? LicenseStateChanged;
}
