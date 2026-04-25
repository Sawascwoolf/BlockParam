using FluentAssertions;
using BlockParam.Licensing;
using Newtonsoft.Json;
using Xunit;

namespace BlockParam.Tests;

/// <summary>
/// #20: Verifies the machine-wide shared license file path
/// (<c>%PROGRAMDATA%\BlockParam\license.key</c>) takes precedence over the
/// per-user cache, so multi-seat deployments can roll out / rotate keys via
/// IT tooling without per-engineer interaction.
/// </summary>
public class OnlineLicenseServiceSharedFileTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _storageDir;
    private readonly string _sharedKeyPath;

    public OnlineLicenseServiceSharedFileTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"BlockParamLicTest_{Guid.NewGuid():N}");
        _storageDir = Path.Combine(_tempDir, "user");
        _sharedKeyPath = Path.Combine(_tempDir, "shared", "license.key");
        Directory.CreateDirectory(_storageDir);
        Directory.CreateDirectory(Path.GetDirectoryName(_sharedKeyPath)!);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void NoSharedFile_FallsBackToUserCache()
    {
        // No shared file present
        WriteUserLicense("PRO-USER-AAAA-BBBB", "instance-1");

        using var svc = NewService();

        var info = svc.GetLicenseInfo();
        info.LicenseKey.Should().Be("PRO-USER-AAAA-BBBB");
        info.IsManagedKey.Should().BeFalse();
        info.ManagedKeyFilePath.Should().BeNull();
    }

    [Fact]
    public void NullSharedPath_BehavesLikeOriginalConstructor()
    {
        WriteUserLicense("PRO-USER-AAAA-BBBB", "instance-1");

        // Explicitly null shared path → no managed-key probing at all
        using var svc = new OnlineLicenseService(_storageDir, "https://example", sharedLicenseFilePath: null);

        svc.GetLicenseInfo().IsManagedKey.Should().BeFalse();
        svc.GetLicenseInfo().LicenseKey.Should().Be("PRO-USER-AAAA-BBBB");
    }

    [Fact]
    public void SharedFile_NoUserCache_KeyIsAdopted()
    {
        File.WriteAllText(_sharedKeyPath, "PRO-IT-1111-2222");

        using var svc = NewService();

        var info = svc.GetLicenseInfo();
        info.LicenseKey.Should().Be("PRO-IT-1111-2222");
        info.IsManagedKey.Should().BeTrue();
        info.ManagedKeyFilePath.Should().Be(_sharedKeyPath);
    }

    [Fact]
    public void SharedFile_DiffersFromUserCache_SharedKeyWinsAndCacheIsCleared()
    {
        WriteUserLicense("PRO-USER-OLD-KEY", "instance-1");
        WriteUserCacheGrantingPro();
        File.WriteAllText(_sharedKeyPath, "PRO-IT-NEW-ROTATION");

        using var svc = NewService();

        var info = svc.GetLicenseInfo();
        info.LicenseKey.Should().Be("PRO-IT-NEW-ROTATION");
        info.IsManagedKey.Should().BeTrue();

        // Cache from the old key must be invalidated — otherwise the user would
        // appear Pro on the new key without the server having validated it.
        File.Exists(Path.Combine(_storageDir, "license_cache.dat")).Should().BeFalse();
        info.Tier.Should().Be(LicenseTier.Free);
    }

    [Fact]
    public void SharedFile_MatchesUserCache_KeepsExistingCache()
    {
        WriteUserLicense("PRO-SAME-KEY", "instance-keep");
        WriteUserCacheGrantingPro();
        File.WriteAllText(_sharedKeyPath, "PRO-SAME-KEY");

        using var svc = NewService();

        var info = svc.GetLicenseInfo();
        info.LicenseKey.Should().Be("PRO-SAME-KEY");
        info.IsManagedKey.Should().BeTrue();
        // Cache (and therefore Pro tier) must be preserved across restarts to
        // avoid pointless server-side session churn on every Add-In open.
        info.Tier.Should().Be(LicenseTier.Pro);
        File.Exists(Path.Combine(_storageDir, "license_cache.dat")).Should().BeTrue();
    }

    [Fact]
    public void SharedFile_TrimsWhitespaceAndIgnoresEmpty()
    {
        WriteUserLicense("PRO-USER-FALLBACK", "instance-fb");

        // Whitespace-only file is treated as "no managed key" → fall back to user cache
        File.WriteAllText(_sharedKeyPath, "   \r\n  ");

        using var svc = NewService();

        svc.GetLicenseInfo().IsManagedKey.Should().BeFalse();
        svc.GetLicenseInfo().LicenseKey.Should().Be("PRO-USER-FALLBACK");
    }

    [Fact]
    public void SharedFile_TrimsTrailingNewlineFromKey()
    {
        // Batch scripts often emit `echo PRO-X > license.key`, which adds a CRLF.
        // We must match the exact server-side key, so trim before comparing.
        File.WriteAllText(_sharedKeyPath, "PRO-IT-TRIMMED\r\n");

        using var svc = NewService();

        svc.GetLicenseInfo().LicenseKey.Should().Be("PRO-IT-TRIMMED");
    }

    [Fact]
    public void DefaultSharedLicenseFilePath_PointsAtProgramData()
    {
        // Sanity-check the documented location admins are told to use.
        OnlineLicenseService.DefaultSharedLicenseFilePath
            .Should().EndWith(Path.Combine("BlockParam", "license.key"));
        OnlineLicenseService.DefaultSharedLicenseFilePath
            .Should().StartWith(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
    }

    [Fact]
    public void RotatedKeyAdoption_PreservesInstanceIdAcrossRestart()
    {
        // First start: shared file rolls out a key, no prior user cache.
        File.WriteAllText(_sharedKeyPath, "PRO-IT-V1");
        using (var svc1 = NewService())
        {
            svc1.GetLicenseInfo().LicenseKey.Should().Be("PRO-IT-V1");
        }

        var firstInstance = ReadStoredInstanceId();
        firstInstance.Should().NotBeNullOrEmpty();

        // Rotation: IT pushes a new key.
        File.WriteAllText(_sharedKeyPath, "PRO-IT-V2");
        using (var svc2 = NewService())
        {
            svc2.GetLicenseInfo().LicenseKey.Should().Be("PRO-IT-V2");
        }

        // Reusing the existing instance id avoids burning a session slot on
        // every rotation — the server can replace key on the same instance.
        ReadStoredInstanceId().Should().Be(firstInstance);
    }

    private OnlineLicenseService NewService() =>
        new(_storageDir, "https://example", sharedLicenseFilePath: _sharedKeyPath);

    private void WriteUserLicense(string key, string instanceId)
    {
        var path = Path.Combine(_storageDir, "license.json");
        var json = JsonConvert.SerializeObject(new
        {
            LicenseKey = key,
            InstanceId = instanceId,
            ActivatedAt = DateTime.UtcNow
        });
        File.WriteAllText(path, json);
    }

    private void WriteUserCacheGrantingPro()
    {
        // Cache file is obfuscated on disk — easiest way to seed a valid one is
        // to call SaveCache via reflection. But Obfuscation is internal-ish;
        // instead, drive the service itself to produce a cache by activating.
        // Here we just write a syntactically-valid obfuscated cache.
        var cache = new
        {
            ReceivedAtUtc = DateTime.UtcNow,
            ExpiresAt = (DateTime?)null,
            MaxConcurrent = 1,
            ActiveSessions = 1,
            ErrorMessage = (string?)null
        };
        var json = JsonConvert.SerializeObject(cache);
        var bytes = Obfuscation.Obfuscate(json);
        File.WriteAllBytes(Path.Combine(_storageDir, "license_cache.dat"), bytes);
    }

    private string? ReadStoredInstanceId()
    {
        var path = Path.Combine(_storageDir, "license.json");
        if (!File.Exists(path)) return null;
        var json = File.ReadAllText(path);
        var obj = JsonConvert.DeserializeObject<Dictionary<string, object?>>(json);
        return obj?["InstanceId"]?.ToString();
    }
}
