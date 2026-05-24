using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security;
using System.Security.Permissions;
using System.Windows;
using System.Windows.Data;
using BlockParam.Services;
using BlockParam.UI.Controls.PillMultiSelect;
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

    // ─────────────────────────────────────────────────────────────────────────
    // #141 — partial-trust WPF binding regression
    //
    // The PillMultiSelect UserControl's content DataContext is a
    // MultiSelectInternalState; rows bind to MultiSelectRowViewModel; groups bind
    // to MultiSelectGroupViewModel. WPF's binding engine resolves `{Binding Member}`
    // by reflecting from PresentationFramework (a foreign assembly) over the
    // bound object. Under TIA Portal V20's partial-trust SandboxDomain that
    // reflection silently fails on non-public types — the trigger renders
    // blank, the popup never opens. Full-trust CI/DevLauncher never see it.
    //
    // The two tests below are the regression gate:
    //
    //   * Structural: each bound pill VM type must be `public`. Fast, exact,
    //     refuses to merge a future "let's make this internal again" PR.
    //
    //   * Behavioural (sandbox): from inside the same partial-trust grant
    //     set TIA gives the Add-In, load BlockParam.dll and confirm each
    //     pill VM type is reachable AND public. This mirrors what
    //     PresentationFramework does when it loads bound types for {Binding}
    //     resolution — `Assembly.Load(name)` is the same code path.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Bound_pill_vm_types_are_public_for_partial_trust_binding()
    {
        // WPF's binding engine reflects over bound source objects from
        // PresentationFramework, a foreign assembly. Under TIA V20's
        // partial-trust SandboxDomain, that reflection yields no value for
        // non-public types — the pill trigger renders empty, no IsOpen
        // write-back, the popup never opens. Full-trust CI/DevLauncher hide
        // this, which is why #141 regressed twice before this gate landed.
        const string why =
            "WPF data binding can't resolve members of a non-public type "
            + "from PresentationFramework under TIA's partial-trust sandbox — "
            + "see #141. Keep this `public`; the type is still presentation "
            + "infrastructure, hosts use the PillMultiSelect UserControl's DPs.";

        typeof(MultiSelectViewModelBase).IsPublic.Should().BeTrue(why);
        typeof(MultiSelectInternalState).IsPublic.Should().BeTrue(why);
        typeof(MultiSelectRowViewModel).IsPublic.Should().BeTrue(why);
        typeof(MultiSelectGroupViewModel).IsPublic.Should().BeTrue(why);

        // Converters referenced by `{Binding … Converter={StaticResource …}}`
        // and the ICommand implementation handed out through public command
        // properties — same partial-trust foreign-assembly reflection rule.
        typeof(MultiSelectGroupHeaderVisibilityConverter).IsPublic.Should().BeTrue(why);
        typeof(MultiSelectGroupExpandedVisibilityConverter).IsPublic.Should().BeTrue(why);
        typeof(MultiSelectRelayCommand).IsPublic.Should().BeTrue(why);
    }

    [Fact]
    public void Pill_vm_types_resolve_as_public_inside_partial_trust_sandbox()
    {
        var baseDir = StageAssemblies();
        var domain = CreateTiaLikeSandbox(baseDir);
        try
        {
            var worker = CreateWorker(domain);
            var result = worker.ProbePillVmTypeVisibility();

            // "C:OK" → all four bound pill VM types loaded from BlockParam.dll
            //          and reported IsPublic = true under TIA's grant set.
            //          That is the contract WPF binding depends on for #141.
            // anything else encodes which type went back to internal (or the
            // assembly stopped loading in the sandbox, which would also break
            // binding).
            result.Should().Be(
                "C:OK",
                "each pill VM bound by {Binding ...} must reflect as a public "
                + "type from inside the sandbox; otherwise WPF binding fails "
                + "silently under TIA Portal and the control renders blank (#141)");
        }
        finally
        {
            try { AppDomain.Unload(domain); }
            finally { TryDeleteDir(baseDir); }
        }
    }

    [UIFact]
    public void Pill_internal_state_property_binds_through_wpf()
    {
        // Behavioural positive test (full trust): drive a real WPF {Binding}
        // against a MultiSelectInternalState instance and verify the
        // bound DependencyProperty receives the source value. This proves
        // the surface that mattered for #141 — Label / IsOpen / HasSelection
        // / SelectedCount on the internal state — actually resolves through
        // WPF's binding engine end-to-end.
        //
        // Why this catches future regressions despite running in full trust:
        // pairing it with the structural test above means "internal again"
        // breaks the visibility gate, "renamed/removed bound property" breaks
        // this one. Together they cover the silent-binding-failure mode that
        // #141's TIA reproduction surfaced.
        var pill = new PillMultiSelect();
        pill.Label = "Bound";

        var source = (MultiSelectInternalState)((FrameworkElement)pill.Content).DataContext;
        source.IsOpen = true;

        var target = new BindingTarget();
        BindingOperations.SetBinding(target, BindingTarget.LabelValueProperty,
            new Binding(nameof(MultiSelectInternalState.Label)) { Source = source, Mode = BindingMode.OneWay });
        BindingOperations.SetBinding(target, BindingTarget.OpenValueProperty,
            new Binding(nameof(MultiSelectInternalState.IsOpen)) { Source = source, Mode = BindingMode.OneWay });

        target.LabelValue.Should().Be("Bound", "Label binding must resolve via reflection on the public VM type");
        target.OpenValue.Should().Be(true, "IsOpen binding must resolve via reflection on the public VM type");
    }

    // The ORIGINAL deployed directory of an assembly. vstest shadow-copies
    // assemblies into incomplete per-assembly cache dirs, so Assembly.Location
    // points at a dir that holds that one DLL but not its dependency closure.
    // Assembly.CodeBase is preserved as the original build-output path even
    // under shadow copy (valid on .NET Framework 4.8, the test TFM) — that
    // directory contains the full copy-local closure.
    private static string CodeBaseDir(Assembly a)
    {
        if (!a.IsDynamic && !string.IsNullOrEmpty(a.CodeBase))
        {
            var uri = new Uri(a.CodeBase);
            if (uri.IsFile)
                return Path.GetDirectoryName(uri.LocalPath)!;
        }
        var loc = a.IsDynamic ? null : a.Location;
        return string.IsNullOrEmpty(loc)
            ? AppDomain.CurrentDomain.BaseDirectory
            : Path.GetDirectoryName(loc)!;
    }

    // No single existing dir contains the whole closure (BlockParam.dll +
    // BlockParam.Tests.dll + Newtonsoft + deps) because of vstest shadow
    // copy. Stage the union of the test and production build-output dirs
    // (resolved via CodeBase, which survives shadow copy) into one temp
    // dir; the sandbox's trusted loader then resolves everything from
    // ApplicationBase with no AssemblyResolve hook (transparent
    // partial-trust code may not install one) and no grant beyond
    // Execution. Harness infrastructure — not subject to the storage-layer
    // guardrail.
    private static string StageAssemblies()
    {
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            CodeBaseDir(typeof(PartialTrustSandboxTests).Assembly),
            CodeBaseDir(typeof(UiZoomService).Assembly),
            AppDomain.CurrentDomain.BaseDirectory,
        };

        var staging = Path.Combine(
            Path.GetTempPath(), "BlockParamPT-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        foreach (var src in dirs)
        {
            if (!Directory.Exists(src)) continue;
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

    /// <summary>
    /// #141 gate, run from inside the partial-trust sandbox. Loads
    /// BlockParam.dll via <see cref="Assembly.Load(string)"/> — the same
    /// code path PresentationFramework takes when it resolves bound types
    /// for <c>{Binding}</c> — and asserts each pill VM type is reachable
    /// and <see cref="Type.IsPublic"/>. If any went back to internal,
    /// WPF binding fails silently in TIA and the pill renders blank.
    /// </summary>
    public string ProbePillVmTypeVisibility()
    {
        try
        {
            var asm = Assembly.Load("BlockParam");

            var checks = new[]
            {
                "BlockParam.UI.Controls.PillMultiSelect.MultiSelectViewModelBase",
                "BlockParam.UI.Controls.PillMultiSelect.MultiSelectInternalState",
                "BlockParam.UI.Controls.PillMultiSelect.MultiSelectRowViewModel",
                "BlockParam.UI.Controls.PillMultiSelect.MultiSelectGroupViewModel",
                "BlockParam.UI.Controls.PillMultiSelect.MultiSelectGroupHeaderVisibilityConverter",
                "BlockParam.UI.Controls.PillMultiSelect.MultiSelectGroupExpandedVisibilityConverter",
                "BlockParam.UI.Controls.PillMultiSelect.MultiSelectRelayCommand",
            };

            foreach (var fullName in checks)
            {
                var t = asm.GetType(fullName);
                if (t == null) return "C:MISSING:" + fullName;
                if (!t.IsPublic) return "C:INTERNAL:" + fullName;
            }
            return "C:OK";
        }
        catch (Exception ex)
        {
            return "C:ERR:" + ex.GetType().FullName + ":" + ex.Message;
        }
    }
}

/// <summary>
/// Tiny <see cref="DependencyObject"/> used by the #141 binding test to
/// receive bound values. Two DPs (string + bool) cover the property types
/// the pill control's trigger renders against — Label (string),
/// IsOpen / HasSelection (bool). Public so the WPF binding engine can
/// reflect on it the same way it does on any host VM.
/// </summary>
public sealed class BindingTarget : DependencyObject
{
    public static readonly DependencyProperty LabelValueProperty =
        DependencyProperty.Register(nameof(LabelValue), typeof(string), typeof(BindingTarget),
            new PropertyMetadata(null));

    public static readonly DependencyProperty OpenValueProperty =
        DependencyProperty.Register(nameof(OpenValue), typeof(bool), typeof(BindingTarget),
            new PropertyMetadata(false));

    public string? LabelValue
    {
        get => (string?)GetValue(LabelValueProperty);
        set => SetValue(LabelValueProperty, value);
    }

    public bool OpenValue
    {
        get => (bool)GetValue(OpenValueProperty);
        set => SetValue(OpenValueProperty, value);
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
