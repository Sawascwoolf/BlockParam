namespace BlockParam.UI;

/// <summary>
/// Abstracts message box interactions for testability.
/// The ViewModel uses this instead of calling MessageBox.Show directly.
/// </summary>
public interface IMessageBoxService
{
    bool AskYesNo(string message, string title);
    void ShowError(string message, string title);
    void ShowInfo(string message, string title);

    /// <summary>
    /// 3-way prompt: Apply &amp; switch / Stash &amp; switch / Cancel.
    /// Used by the DB-switcher when the current DB has staged edits (#59).
    /// </summary>
    ApplyStashCancelResult AskApplyStashCancel(string message, string title);

    /// <summary>
    /// 3-way prompt: Add alongside / Replace / Cancel.
    /// Used when reactivating a stashed DB while ≥2 other DBs are active (#92).
    /// </summary>
    AddOrReplaceResult AskAddOrReplace(string message, string title);

    /// <summary>
    /// 3-way prompt: Apply active (discard stash) / Discard all / Cancel.
    /// Used by the close-confirm when both active-DB and stashed-DB edits exist.
    /// </summary>
    CloseWithStashResult AskCloseWithStash(string message, string title);
}

public enum ApplyStashCancelResult { ApplyAndSwitch, StashAndSwitch, Cancel }
public enum AddOrReplaceResult { Add, Replace, Cancel }
public enum CloseWithStashResult { ApplyActive, DiscardAll, Cancel }

/// <summary>
/// Default implementation using custom WPF dialogs with named buttons.
/// </summary>
public class WpfMessageBoxService : IMessageBoxService
{
    public bool AskYesNo(string message, string title)
    {
        var result = System.Windows.MessageBox.Show(
            message, title,
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);
        return result == System.Windows.MessageBoxResult.Yes;
    }

    public void ShowError(string message, string title)
    {
        System.Windows.MessageBox.Show(
            message, title,
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Error);
    }

    public void ShowInfo(string message, string title)
    {
        System.Windows.MessageBox.Show(
            message, title,
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Information);
    }

    public ApplyStashCancelResult AskApplyStashCancel(string message, string title)
    {
        var dlg = new ThreeButtonDialog(
            message, title,
            Localization.Res.Get("Dialog_SwitchDb_KeepConfirm_ApplyButton"),
            Localization.Res.Get("Dialog_SwitchDb_KeepConfirm_StashButton"),
            Localization.Res.Get("Dialog_Cancel"));
        dlg.ShowDialog();
        return dlg.Choice switch
        {
            ThreeButtonDialog.ButtonChoice.Primary   => ApplyStashCancelResult.ApplyAndSwitch,
            ThreeButtonDialog.ButtonChoice.Secondary => ApplyStashCancelResult.StashAndSwitch,
            _                                        => ApplyStashCancelResult.Cancel,
        };
    }

    public AddOrReplaceResult AskAddOrReplace(string message, string title)
    {
        var dlg = new ThreeButtonDialog(
            message, title,
            Localization.Res.Get("Reactivate_AdditiveOrReplace_AddButton"),
            Localization.Res.Get("Reactivate_AdditiveOrReplace_ReplaceButton"),
            Localization.Res.Get("Dialog_Cancel"));
        dlg.ShowDialog();
        return dlg.Choice switch
        {
            ThreeButtonDialog.ButtonChoice.Primary   => AddOrReplaceResult.Add,
            ThreeButtonDialog.ButtonChoice.Secondary => AddOrReplaceResult.Replace,
            _                                        => AddOrReplaceResult.Cancel,
        };
    }

    public CloseWithStashResult AskCloseWithStash(string message, string title)
    {
        var dlg = new ThreeButtonDialog(
            message, title,
            Localization.Res.Get("Dialog_UnsavedChanges_ApplyActiveButton"),
            Localization.Res.Get("Dialog_UnsavedChanges_DiscardAllButton"),
            Localization.Res.Get("Dialog_Cancel"));
        dlg.ShowDialog();
        return dlg.Choice switch
        {
            ThreeButtonDialog.ButtonChoice.Primary   => CloseWithStashResult.ApplyActive,
            ThreeButtonDialog.ButtonChoice.Secondary => CloseWithStashResult.DiscardAll,
            _                                        => CloseWithStashResult.Cancel,
        };
    }
}
