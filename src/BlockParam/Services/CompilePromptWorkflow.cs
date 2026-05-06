using BlockParam.Diagnostics;

namespace BlockParam.Services;

/// <summary>
/// Coordinates the "run an export, on TIA inconsistency error ask the user to
/// compile and retry" sequence triggered for inconsistent DBs (#19) and inconsistent
/// UDTs (#27). Kept separate from <see cref="BlockExporter"/> so the prompt/retry
/// shape is fully unit-testable without TIA Portal — callers only need to supply
/// delegates for export, compile, and the user prompt.
///
/// Mirrors <see cref="InconsistentUdtRetry"/>'s callback-based shape: real DataBlock
/// / PlcType references stay outside the helper so tests can drive it with synthetic
/// exceptions and recorded actions.
/// </summary>
public static class CompilePromptWorkflow
{
    /// <summary>
    /// Runs <paramref name="exportAction"/>. If it throws an exception that
    /// <see cref="InconsistencyDetector.Matches"/> recognises as an inconsistency
    /// error, asks the user via <paramref name="askUser"/>; on yes, runs
    /// <paramref name="compileAction"/> then retries <paramref name="exportAction"/>.
    /// Returns false if the user declined the compile prompt; true on success
    /// (possibly after retry). Non-inconsistency errors propagate unchanged.
    /// </summary>
    public static bool TryWithRetry(
        string blockName,
        Action exportAction,
        Action compileAction,
        Func<bool> askUser)
    {
        try
        {
            exportAction();
            return true;
        }
        catch (Exception ex) when (InconsistencyDetector.Matches(ex))
        {
            Log.Warning("Block {Name} is inconsistent, asking user to compile", blockName);

            if (!askUser())
            {
                Log.Information("User cancelled compilation for {Name}", blockName);
                return false;
            }

            compileAction();
            exportAction();
            return true;
        }
    }
}
