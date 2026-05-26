// Polyfill for C# 9 record / init-only setters on .NET Framework 4.8.
// See src/BlockParam.DevLauncher/IsExternalInit.cs for the full rationale —
// the test project uses `record` types too and hits the same CS0518 without
// this shim.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
