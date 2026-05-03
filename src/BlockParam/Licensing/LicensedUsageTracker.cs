namespace BlockParam.Licensing;

/// <summary>
/// Bridge that implements <see cref="IUsageTracker"/> with license-awareness.
/// Pro tier: unlimited operations. Free tier: delegates to <see cref="LocalUsageTracker"/>.
/// </summary>
public class LicensedUsageTracker : IUsageTracker
{
    private readonly ILicenseService _licenseService;
    private readonly LocalUsageTracker _freeTracker;

    public LicensedUsageTracker(ILicenseService licenseService, LocalUsageTracker freeTracker)
    {
        _licenseService = licenseService;
        _freeTracker = freeTracker;
    }

    public int DailyLimit => _licenseService.IsProActive ? int.MaxValue : _freeTracker.DailyLimit;

    public UsageStatus GetStatus()
    {
        if (_licenseService.IsProActive)
            return new UsageStatus(0, int.MaxValue);

        return _freeTracker.GetStatus();
    }

    public bool RecordUsage(int count)
    {
        if (_licenseService.IsProActive)
            return true;

        return _freeTracker.RecordUsage(count);
    }
}
