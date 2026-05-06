namespace BlockParam.Services;

/// <summary>
/// Abstracts all TIA Portal Openness API interactions.
/// Implementations use the real API; tests use mocks.
/// </summary>
public interface ITiaPortalAdapter
{
    /// <summary>
    /// Exports a Data Block to a SimaticML XML file.
    /// Returns the path to the exported XML file.
    /// </summary>
    string ExportBlock(object dataBlock, string targetDirectory);

    /// <summary>
    /// Imports a modified SimaticML XML file back into TIA Portal.
    /// Uses ImportOptions.Override to replace the existing block.
    /// </summary>
    void ImportBlock(object blockGroup, string xmlPath);

    /// <summary>
    /// Creates a backup of a Data Block before modification.
    /// Returns the path to the backup XML file.
    /// </summary>
    string BackupBlock(object dataBlock, string backupDirectory);

    /// <summary>
    /// Restores a Data Block from a backup XML file.
    /// </summary>
    void RestoreFromBackup(object blockGroup, string backupPath);

    /// <summary>
    /// Acquires exclusive access to TIA Portal for bulk operations.
    /// Reduces overhead but disables the TIA undo stack.
    /// </summary>
    IDisposable? AcquireExclusiveAccess(string description);

    /// <summary>
    /// Sets a single start value via the Direct API (without XML export/import).
    /// Used for small scopes where TIA undo should be preserved.
    /// </summary>
    void SetStartValueDirect(object member, string value);

    /// <summary>
    /// Gets the name of a Data Block.
    /// </summary>
    string GetBlockName(object dataBlock);

    /// <summary>
    /// Gets the block group (parent folder) of a Data Block.
    /// </summary>
    object GetBlockGroup(object dataBlock);

    /// <summary>
    /// Compiles a single Data Block, used as the retry step when the export
    /// path hits TIA's "inconsistent block" error (#19).
    /// </summary>
    void CompileBlock(object dataBlock);
}
