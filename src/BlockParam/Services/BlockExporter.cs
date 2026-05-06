using Siemens.Engineering.SW.Blocks;
using BlockParam.Localization;

namespace BlockParam.Services;

/// <summary>
/// Wraps export / backup of a TIA Data Block with the inconsistency-prompt UX
/// (#19). Sole production caller wires the export <see cref="Action"/> to either
/// <see cref="ITiaPortalAdapter.ExportBlock"/> or
/// <see cref="ITiaPortalAdapter.BackupBlock"/>; both reuse the same compile-then-
/// retry path so the user sees identical wording whether the inconsistency is
/// caught at first export or at backup before Apply.
/// </summary>
public interface IBlockExporter
{
    /// <summary>
    /// Runs <paramref name="exportAction"/> against <paramref name="block"/>; on
    /// TIA "inconsistent block" error, asks the user via the prompt and retries
    /// after <see cref="ITiaPortalAdapter.CompileBlock"/>. Returns false if the
    /// user declined the compile prompt; true on success.
    /// </summary>
    bool TryExportWithCompilePrompt(DataBlock block, Action exportAction);
}

/// <summary>
/// Production <see cref="IBlockExporter"/> that drives compile via
/// <see cref="ITiaPortalAdapter"/> and prompts via <see cref="IUserPrompt"/>.
/// All sequencing logic lives in <see cref="CompilePromptWorkflow"/> so the
/// hard-to-test layer (TIA types, WPF prompts) is just thin glue.
/// </summary>
public sealed class BlockExporter : IBlockExporter
{
    private readonly ITiaPortalAdapter _adapter;
    private readonly IUserPrompt _prompt;

    public BlockExporter(ITiaPortalAdapter adapter, IUserPrompt prompt)
    {
        _adapter = adapter;
        _prompt = prompt;
    }

    public bool TryExportWithCompilePrompt(DataBlock block, Action exportAction)
    {
        return CompilePromptWorkflow.TryWithRetry(
            blockName: block.Name,
            exportAction: exportAction,
            compileAction: () => _adapter.CompileBlock(block),
            askUser: () => _prompt.AskYesNo(
                title: Res.Get("Udt_InconsistentPromptTitle"),
                message: Res.Format("Db_InconsistentPrompt", block.Name)));
    }
}
