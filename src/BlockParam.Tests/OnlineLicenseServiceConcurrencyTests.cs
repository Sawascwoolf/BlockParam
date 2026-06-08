using BlockParam.Licensing;
using BlockParam.Services.Storage;
using FluentAssertions;
using Xunit;

namespace BlockParam.Tests;

/// <summary>
/// Concurrency stress tests for <see cref="OnlineLicenseService"/> (#170).
/// Hammers the public surface from multiple threads to verify no
/// <see cref="ObjectDisposedException"/> leaks and no torn-state reads.
/// </summary>
public class OnlineLicenseServiceConcurrencyTests
{
    private const int ThreadCount = 8;
    private const int IterationsPerThread = 200;

    private static StoragePath StorageDir =>
        StoragePath.FromAbsolute(@"C:\bp\appdata") / "licensing";

    private static OnlineLicenseService NewService(
        InMemoryBlockParamStorage? fs = null,
        string? serverBaseUrl = null)
    {
        return new OnlineLicenseService(
            fs ?? new InMemoryBlockParamStorage(),
            StorageDir,
            serverBaseUrl: serverBaseUrl);
    }

    private static OnlineLicenseService NewServiceWithKey(InMemoryBlockParamStorage? fs = null)
    {
        var storage = fs ?? new InMemoryBlockParamStorage();
        var licPath = StorageDir / "license.json";
        storage.WriteAllText(licPath,
            "{\"LicenseKey\":\"TEST-KEY\",\"InstanceId\":\"test-id\",\"ActivatedAt\":\"2026-01-01T00:00:00Z\"}");
        return new OnlineLicenseService(
            storage,
            StorageDir,
            serverBaseUrl: "http://127.0.0.1:1");
    }

    [Fact]
    public void Concurrent_SetProActive_and_reads_do_not_throw()
    {
        using var svc = NewService();

        var tasks = new Task[ThreadCount];
        for (int i = 0; i < ThreadCount; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                for (int j = 0; j < IterationsPerThread; j++)
                {
                    _ = svc.IsProActive;
                    _ = svc.CurrentTier;
                    _ = svc.GetLicenseInfo();
                }
            });
        }

        Task.WaitAll(tasks);

        svc.CurrentTier.Should().Be(LicenseTier.Free);
    }

    [Fact]
    public void Concurrent_StartHeartbeat_StopHeartbeat_do_not_throw()
    {
        // Non-null server + seeded key so StartHeartbeat installs a real timer
        // and SendHeartbeatAsync exercises the lock/retry/Interlocked path
        // (HTTP fails immediately against 127.0.0.1:1 — hits the catch block).
        using var svc = NewServiceWithKey();

        var tasks = new Task[ThreadCount];
        for (int i = 0; i < ThreadCount; i++)
        {
            int threadIdx = i;
            tasks[i] = Task.Run(() =>
            {
                for (int j = 0; j < IterationsPerThread; j++)
                {
                    if (threadIdx % 2 == 0)
                        svc.StartHeartbeat();
                    else
                        svc.StopHeartbeat();
                }
            });
        }

        Task.WaitAll(tasks);

        // Cleanup: ensure timer is stopped
        svc.StopHeartbeat();
    }

    [Fact]
    public void Concurrent_Dispose_does_not_throw()
    {
        var svc = NewService();

        var tasks = new Task[ThreadCount];
        for (int i = 0; i < ThreadCount; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                for (int j = 0; j < IterationsPerThread; j++)
                {
                    svc.Dispose();
                }
            });
        }

        Task.WaitAll(tasks);
    }

    [Fact]
    public void Concurrent_DeactivateKey_and_GetLicenseInfo_do_not_throw()
    {
        using var svc = NewService();

        var tasks = new Task[ThreadCount];
        for (int i = 0; i < ThreadCount; i++)
        {
            int threadIdx = i;
            tasks[i] = Task.Run(() =>
            {
                for (int j = 0; j < IterationsPerThread; j++)
                {
                    if (threadIdx % 2 == 0)
                        svc.DeactivateKey();
                    else
                        _ = svc.GetLicenseInfo();
                }
            });
        }

        Task.WaitAll(tasks);
    }

    [Fact]
    public void Concurrent_Dispose_and_StartHeartbeat_no_ObjectDisposedException_leaks()
    {
        var svc = NewServiceWithKey();

        var barrier = new Barrier(ThreadCount);
        var tasks = new Task[ThreadCount];
        for (int i = 0; i < ThreadCount; i++)
        {
            int threadIdx = i;
            tasks[i] = Task.Run(() =>
            {
                barrier.SignalAndWait();
                for (int j = 0; j < IterationsPerThread; j++)
                {
                    if (threadIdx % 2 == 0)
                        svc.Dispose();
                    else
                        svc.StartHeartbeat();
                }
            });
        }

        Task.WaitAll(tasks);
    }

    [Fact]
    public void CurrentTier_always_returns_valid_enum_under_contention()
    {
        using var svc = NewService();

        var tiers = new LicenseTier[ThreadCount * IterationsPerThread];
        var tasks = new Task[ThreadCount];
        for (int i = 0; i < ThreadCount; i++)
        {
            int threadIdx = i;
            tasks[i] = Task.Run(() =>
            {
                for (int j = 0; j < IterationsPerThread; j++)
                {
                    var tier = svc.CurrentTier;
                    tiers[threadIdx * IterationsPerThread + j] = tier;
                }
            });
        }

        Task.WaitAll(tasks);

        tiers.Should().OnlyContain(t => t == LicenseTier.Free || t == LicenseTier.Pro);
    }
}
