using System;
using System.IO;
using System.Security;
using System.Security.Permissions;
using BlockParam.Services;
using FluentAssertions;
using Xunit;

namespace BlockParam.Tests;

/// <summary>
/// Reproduces TIA Portal's Add-In Loader sandbox in-process and asserts that
/// the partial-trust IL regression fixed in #131 (issue #130) stays fixed.
///
/// WHY a whole AppDomain instead of a normal unit test or PEVerify:
///   - The bug is neither a source defect (Roslyn syntax looks harmless) nor
///     an IL-verifiability defect — PEVerify/ILVerify both pass `ldflda`+`call`
///     on a readonly-struct field. It is a .NET Framework 4.8 *partial-trust
///     CAS runtime policy* that only fires inside a restricted AppDomain.
///   - No SonarQube / FxCop / analyzer rule covers it (legacy CAS, removed in
///     .NET Core). The only faithful detector is to re-create the runtime
///     conditions and JIT the method under the partial grant set TIA uses.
///
/// Two facts:
///   * <see cref="PersistenceEnabled_is_verifiable_under_partial_trust"/> is
///     the regression gate: with #131 in place it is GREEN; revert the
///     local-copy discipline in <c>UiZoomService.PersistenceEnabled</c> and it
///     goes RED with a VerificationException — exactly what TIA's loader threw.
///   * <see cref="Sandbox_actually_enforces_IL_verification"/> is a self-test:
///     a method carrying the *pre-#131* unverifiable shape MUST be rejected by
///     the same sandbox. If it isn't, the toolchain/runner no longer enforces
///     this policy and the regression gate above is inconclusive — so we fail
///     loudly rather than report a meaningless green.
/// </summary>
public sealed class PartialTrustSandboxTests
{
    [Fact]
    public void PersistenceEnabled_is_verifiable_under_partial_trust()
    {
        var baseDir = StageAssemblies();
        var domain = CreateTiaLikeSandbox(baseDir);
        try
        {
            var worker = CreateWorker(domain);
            var result = worker.ProbeUiZoomService();

            // "A:OK"  -> #131 local-copy discipline present; IL verifies.
            // "A:VERIFY:..." -> regression: direct `!_settingsPath.IsEmpty`
            //                   re-emitted `ldflda`+`call`, partial trust
            //                   rejected it — same crash TIA's loader hit.
            result.Should().Be(
                "A:OK",
                "UiZoomService must JIT cleanly under TIA's partial-trust grant; "
                + "a VerificationException here means the #131 local-copy fix was lost");
        }
        finally
        {
            try { AppDomain.Unload(domain); }
            finally { TryDeleteDir(baseDir); }
        }
    }

    [Fact]
    public void Sandbox_actually_enforces_IL_verification()
    {
        var baseDir = StageAssemblies();
        var domain = CreateTiaLikeSandbox(baseDir);
        try
        {
            var worker = CreateWorker(domain);
            var result = worker.ProbeCanary();

            // "B:REJECTED" is the GOOD outcome: the sandbox verified IL and
            // threw on the deliberate pre-#131 pattern, proving the gate bites.
            // "B:NOT-REJECTED" means this runner/toolchain no longer enforces
            // partial-trust verification for this shape — the regression test
            // above can no longer catch the #130 bug, so this is a hard fail.
            result.Should().Be(
                "B:REJECTED",
                "the partial-trust sandbox must reject the deliberate "
                + "readonly-struct `ldflda`+`call` canary; if it doesn't, the "
                + "#130 regression gate is no longer meaningful on this runner");
        }
        finally
        {
            try { AppDomain.Unload(domain); }
            finally { TryDeleteDir(baseDir); }
        }
    }

    private static string AsmDir(Type t)
    {
        var loc = t.Assembly.Location;
        return !string.IsNullOrEmpty(loc)
            ? Path.GetDirectoryName(loc)!
            : AppDomain.CurrentDomain.BaseDirectory;
    }

    // vstest shadow-copies the test assembly, so BlockParam.dll and
    // BlockParam.Tests.dll live in different directories at run time and
    // no single ApplicationBase contains both. Stage the union of both
    // output dirs into one temp dir so the sandbox's trusted loader
    // resolves everything (incl. Newtonsoft) from ApplicationBase — no
    // AssemblyResolve hook (transparent partial-trust code may not
    // install one) and no grant beyond Execution. Harness infrastructure,
    // not subject to the storage-layer guardrail.
    private static string StageAssemblies()
    {
        var staging = Path.Combine(
            Path.GetTempPath(), "BlockParamPT-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        foreach (var src in new[]
                 {
                     AsmDir(typeof(UiZoomService)),
                     AsmDir(typeof(PartialTrustSandboxTests)),
                 })
        {
            foreach (var dll in Directory.GetFiles(src, "*.dll"))
                File.Copy(dll, Path.Combine(staging, Path.GetFileName(dll)), overwrite: true);
        }
        return staging;
    }

    private static void TryDeleteDir(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Homogeneous (sandboxed) AppDomain whose grant set mirrors what TIA's
    /// Add-In Loader gives an Add-In: execution only, no full-trust assembly
    /// list — so BlockParam.dll receives the partial grant and the CLR runs
    /// the IL verifier on its methods at JIT, just like inside TIA Portal.
    /// No FileIOPermission is needed: the probe uses an empty settings path,
    /// which disables persistence before any storage call is reached, and
    /// assemblies load from ApplicationBase via the trusted loader.
    /// </summary>
    private static AppDomain CreateTiaLikeSandbox(string baseDir)
    {
        var setup = new AppDomainSetup { ApplicationBase = baseDir };

        var grant = new PermissionSet(PermissionState.None);
        grant.AddPermission(new SecurityPermission(SecurityPermissionFlag.Execution));

        return AppDomain.CreateDomain("BlockParam.PartialTrust.Smoke", null, setup, grant);
    }

    private static PartialTrustWorker CreateWorker(AppDomain domain)
    {
        var asm = typeof(PartialTrustWorker).Assembly;
        return (PartialTrustWorker)domain.CreateInstanceAndUnwrap(
            asm.FullName!, typeof(PartialTrustWorker).FullName!);
    }
}

/// <summary>
/// Runs inside the partial-trust domain. Everything crossing the AppDomain
/// boundary is a plain string so no test-framework type is ever loaded into
/// the sandbox and exceptions are translated rather than marshalled.
/// </summary>
public sealed class PartialTrustWorker : MarshalByRefObject
{
    /// <summary>
    /// Exercises the exact path TIA's loader hit: constructing the service
    /// (empty path → persistence disabled, zero I/O) and reading
    /// <c>ZoomFactor</c>, which forces <c>EnsureLoaded</c> →
    /// <c>PersistenceEnabled</c> to JIT under partial trust.
    /// </summary>
    public string ProbeUiZoomService()
    {
        try
        {
            var svc = new UiZoomService("");
            var zoom = svc.ZoomFactor;
            if (Math.Abs(zoom - UiZoomService.DefaultZoom) > 0.0001)
                return "A:UNEXPECTED-ZOOM:" + zoom;
            return "A:OK";
        }
        catch (VerificationException ex)
        {
            return "A:VERIFY:" + ex.Message;
        }
        catch (Exception ex)
        {
            return "A:ERR:" + ex.GetType().FullName + ":" + ex.Message;
        }
    }

    /// <summary>
    /// Invokes the deliberate pre-#131 unverifiable shape. Under a sandbox
    /// that genuinely enforces verification this throws; that throw is the
    /// proof the regression gate is live.
    /// </summary>
    public string ProbeCanary()
    {
        try
        {
            var hit = new PartialTrustCanary().TouchLikePreFix();
            return "B:NOT-REJECTED(" + hit + ")";
        }
        catch (VerificationException)
        {
            return "B:REJECTED";
        }
        catch (Exception ex)
        {
            return "B:ERR:" + ex.GetType().FullName + ":" + ex.Message;
        }
    }
}

/// <summary>
/// Mirrors <c>StoragePath</c> 1:1: a <c>readonly struct</c> exposing an
/// instance property. Reading it through a <c>readonly</c> field (below)
/// makes Roslyn emit `ldflda`+`call` with no defensive copy — the precise
/// pattern .NET Framework 4.8 partial trust rejects. Public + called
/// directly so the sandbox needs no ReflectionPermission.
/// </summary>
public readonly struct PartialTrustCanaryStruct
{
    private readonly string? _value;
    public PartialTrustCanaryStruct(string? value) => _value = value;
    public bool IsEmpty => string.IsNullOrEmpty(_value);
}

public sealed class PartialTrustCanary
{
    private readonly PartialTrustCanaryStruct _path = new("x");

    // Intentionally written the *broken* (pre-#131) way: a direct instance
    // call on the readonly-struct field instead of copying to a local first.
    // This is the canary, not production code — it must stay unverifiable.
    public bool TouchLikePreFix() => !_path.IsEmpty;
}
