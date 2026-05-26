// Polyfill for C# 9 record / init-only setters on .NET Framework 4.8.
// The compiler emits a modreq referencing System.Runtime.CompilerServices.IsExternalInit
// for every `init` accessor; net48's BCL doesn't ship the type, so without this
// shim every `record` declaration in this project fails with CS0518.
//
// CI was green on 2026-05-25 by luck (a Windows runner image must have surfaced
// the type transitively) and red on 2026-05-26 — adding the shim makes the build
// independent of runner-image churn.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
