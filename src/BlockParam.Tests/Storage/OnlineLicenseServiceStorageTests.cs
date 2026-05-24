using BlockParam.Licensing;
using BlockParam.Services.Storage;
using FluentAssertions;
using Newtonsoft.Json;
using Xunit;

namespace BlockParam.Tests.Storage;

/// <summary>
/// Regression tests pinning <see cref="OnlineLicenseService"/>'s persistence
/// layer to the <see cref="IBlockParamStorage"/> abstraction (#85 follow-up).
/// Covers the shared-license-file adoption paths and disk-roundtrip semantics
/// without touching the actual file system. Disk-based behavior continues to
/// live in <see cref="OnlineLicenseServiceSharedFileTests"/>.
/// </summary>
public class OnlineLicenseServiceStorageTests
{
    private static StoragePath StorageDir =>
        StoragePath.FromAbsolute(@"C:\bp\appdata") / "licensing";
    private static StoragePath SharedKey =>
        StoragePath.FromAbsolute(@"C:\bp\programdata") / "license.key";
    private static StoragePath LicenseJson => StorageDir / "license.json";
    private static StoragePath CacheDat => StorageDir / "license_cache.dat";

    [Fact]
    public void Missing_files_yield_free_tier_without_touching_storage_paths()
    {
        var fs = new InMemoryBlockParamStorage();
        using var svc = NewService(fs);

        var info = svc.GetLicenseInfo();
        info.Tier.Should().Be(LicenseTier.Free);
        info.LicenseKey.Should().BeNull();
        info.IsManagedKey.Should().BeFalse();
        info.ManagedKeyFilePath.Should().BeNull();

        // Constructor must not have created either file.
        fs.FileExists(LicenseJson).Should().BeFalse();
        fs.FileExists(CacheDat).Should().BeFalse();
    }

    [Fact]
    public void SharedKey_adopted_when_no_user_cache_exists()
    {
        var fs = new InMemoryBlockParamStorage();
        fs.WriteAllText(SharedKey, "PRO-IT-FRESH");

        using var svc = NewService(fs);

        var info = svc.GetLicenseInfo();
        info.LicenseKey.Should().Be("PRO-IT-FRESH");
        info.IsManagedKey.Should().BeTrue();
        info.ManagedKeyFilePath.Should().Be(SharedKey.FullPath);

        // license.json should have been written via the abstraction.
        fs.FileExists(LicenseJson).Should().BeTrue();
        fs.ReadAllText(LicenseJson).Should().Contain("PRO-IT-FRESH");
    }

    [Fact]
    public void SharedKey_trims_trailing_whitespace_from_batch_emitted_files()
    {
        // `echo PRO-X > license.key` adds CRLF; service must compare the
        // trimmed value to the server-side key.
        var fs = new InMemoryBlockParamStorage();
        fs.WriteAllText(SharedKey, "PRO-IT-TRIM\r\n");

        using var svc = NewService(fs);
        svc.GetLicenseInfo().LicenseKey.Should().Be("PRO-IT-TRIM");
    }

    [Fact]
    public void Whitespace_only_shared_file_is_ignored()
    {
        var fs = new InMemoryBlockParamStorage();
        SeedUserLicense(fs, "USER-LOCAL", "instance-1");
        fs.WriteAllText(SharedKey, "   \r\n\t");

        using var svc = NewService(fs);

        var info = svc.GetLicenseInfo();
        info.IsManagedKey.Should().BeFalse();
        info.LicenseKey.Should().Be("USER-LOCAL");
    }

    [Fact]
    public void SharedKey_rotation_replaces_user_cache_and_clears_cache_dat()
    {
        var fs = new InMemoryBlockParamStorage();
        SeedUserLicense(fs, "USER-OLD-KEY", "instance-1");
        SeedProGrantingCache(fs);
        fs.WriteAllText(SharedKey, "PRO-IT-ROTATED");

        using var svc = NewService(fs);

        var info = svc.GetLicenseInfo();
        info.LicenseKey.Should().Be("PRO-IT-ROTATED");
        info.IsManagedKey.Should().BeTrue();
        info.Tier.Should().Be(LicenseTier.Free); // server hasn't validated yet
        // Stale cache must be wiped so EvaluateTier doesn't grant Pro on it.
        fs.FileExists(CacheDat).Should().BeFalse();
    }

    [Fact]
    public void Matching_shared_key_preserves_existing_cache_and_tier()
    {
        var fs = new InMemoryBlockParamStorage();
        SeedUserLicense(fs, "PRO-SAME", "instance-keep");
        SeedProGrantingCache(fs);
        fs.WriteAllText(SharedKey, "PRO-SAME");

        using var svc = NewService(fs);

        var info = svc.GetLicenseInfo();
        info.Tier.Should().Be(LicenseTier.Pro);
        info.IsManagedKey.Should().BeTrue();
        fs.FileExists(CacheDat).Should().BeTrue();
    }

    [Fact]
    public void DeactivateKey_clears_both_persisted_files_via_storage()
    {
        var fs = new InMemoryBlockParamStorage();
        SeedUserLicense(fs, "PRO-USER", "instance-1");
        SeedProGrantingCache(fs);

        using (var svc = NewService(fs))
        {
            svc.GetLicenseInfo().Tier.Should().Be(LicenseTier.Pro);
            svc.DeactivateKey();
        }

        fs.FileExists(LicenseJson).Should().BeFalse();
        fs.FileExists(CacheDat).Should().BeFalse();
    }

    [Fact]
    public void Rotation_preserves_instance_id_across_restarts()
    {
        var fs = new InMemoryBlockParamStorage();
        fs.WriteAllText(SharedKey, "PRO-IT-V1");

        string? firstInstance;
        using (var svc1 = NewService(fs)) { svc1.GetLicenseInfo().LicenseKey.Should().Be("PRO-IT-V1"); }
        firstInstance = ReadStoredInstanceId(fs);
        firstInstance.Should().NotBeNullOrEmpty();

        fs.WriteAllText(SharedKey, "PRO-IT-V2");
        using (var svc2 = NewService(fs)) { svc2.GetLicenseInfo().LicenseKey.Should().Be("PRO-IT-V2"); }

        ReadStoredInstanceId(fs).Should().Be(firstInstance);
    }

    private static OnlineLicenseService NewService(IBlockParamStorage fs) =>
        new(fs, StorageDir, "https://example", sharedLicenseFilePath: SharedKey);

    private static void SeedUserLicense(IBlockParamStorage fs, string key, string instanceId)
    {
        var json = JsonConvert.SerializeObject(new
        {
            LicenseKey = key,
            InstanceId = instanceId,
            ActivatedAt = DateTime.UtcNow
        });
        fs.WriteAllText(LicenseJson, json);
    }

    private static void SeedProGrantingCache(IBlockParamStorage fs)
    {
        var cache = new
        {
            ReceivedAtUtc = DateTime.UtcNow,
            ExpiresAt = (DateTime?)null,
            MaxConcurrent = 1,
            ActiveSessions = 1,
            ErrorMessage = (string?)null
        };
        var json = JsonConvert.SerializeObject(cache);
        fs.WriteAllBytes(CacheDat, Obfuscation.Obfuscate(json));
    }

    private static string? ReadStoredInstanceId(IBlockParamStorage fs)
    {
        if (!fs.FileExists(LicenseJson)) return null;
        var json = fs.ReadAllText(LicenseJson);
        var obj = JsonConvert.DeserializeObject<Dictionary<string, object?>>(json);
        return obj?["InstanceId"]?.ToString();
    }
}
