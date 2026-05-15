using System;
using BlockParam.UI;
using Serilog;

namespace BlockParam.DevLauncher;

/// <summary>
/// Headless <see cref="IMessageBoxService"/> for capture-script mode (#96).
/// Returns a canned answer set by the current scene's <see cref="PromptAnswer"/>
/// property rather than showing a modal dialog. Logs every call so the
/// workflow author can verify the right branch was exercised.
///
/// <para>
/// The <see cref="PromptAnswer"/> is consumed once per raise — after the
/// first call that reads it, it resets to null so stale answers don't
/// bleed into subsequent scenes. Callers set it from
/// <see cref="SceneApplier.Apply"/> just before the VM command that raises
/// the prompt.
/// </para>
/// </summary>
public sealed class ScriptedMessageBoxService : IMessageBoxService
{
    /// <summary>
    /// The answer to return for the NEXT prompt raised. Set from
    /// <see cref="SceneApplier.Apply"/> before invoking any VM command
    /// that triggers a 3-way dialog.
    ///
    /// Accepted values:
    /// <list type="bullet">
    ///   <item><description><c>"apply"</c> — <see cref="ApplyStashCancelResult.ApplyAndSwitch"/></description></item>
    ///   <item><description><c>"stash"</c> — <see cref="ApplyStashCancelResult.StashAndSwitch"/></description></item>
    ///   <item><description><c>"cancel"</c> — Cancel (all prompt types)</description></item>
    ///   <item><description><c>"add"</c> — <see cref="AddOrReplaceResult.Add"/></description></item>
    ///   <item><description><c>"replace"</c> — <see cref="AddOrReplaceResult.Replace"/></description></item>
    /// </list>
    /// </summary>
    public string? PromptAnswer { get; set; }

    /// <summary>
    /// The message text of the most recently raised prompt. Populated before
    /// the scripted answer is returned so scenes that want to render the
    /// prompt text can capture it.
    /// </summary>
    public string? LastPromptMessage { get; private set; }

    /// <summary>
    /// The title of the most recently raised prompt.
    /// </summary>
    public string? LastPromptTitle { get; private set; }

    public bool AskYesNo(string message, string title)
    {
        RecordPrompt(message, title);
        Log.Information("[ScriptedMsgBox] AskYesNo (using safe default=true): {Title}", title);
        return true;
    }

    public void ShowError(string message, string title)
    {
        RecordPrompt(message, title);
        Log.Warning("[ScriptedMsgBox] ShowError suppressed: {Title} — {Msg}", title, message);
    }

    public void ShowInfo(string message, string title)
    {
        RecordPrompt(message, title);
        Log.Information("[ScriptedMsgBox] ShowInfo suppressed: {Title} — {Msg}", title, message);
    }

    public ApplyStashCancelResult AskApplyStashCancel(string message, string title)
    {
        RecordPrompt(message, title);
        var answer = ConsumeAnswer();
        var result = answer switch
        {
            "apply"  => ApplyStashCancelResult.ApplyAndSwitch,
            "stash"  => ApplyStashCancelResult.StashAndSwitch,
            "cancel" => ApplyStashCancelResult.Cancel,
            null     => ApplyStashCancelResult.StashAndSwitch,   // safe default: stash
            _        => ApplyStashCancelResult.StashAndSwitch,
        };
        Log.Information(
            "[ScriptedMsgBox] AskApplyStashCancel → {Result} (answer='{Answer}'): {Title}",
            result, answer ?? "(default)", title);
        return result;
    }

    public AddOrReplaceResult AskAddOrReplace(string message, string title)
    {
        RecordPrompt(message, title);
        var answer = ConsumeAnswer();
        var result = answer switch
        {
            "add"     => AddOrReplaceResult.Add,
            "replace" => AddOrReplaceResult.Replace,
            "cancel"  => AddOrReplaceResult.Cancel,
            null      => AddOrReplaceResult.Add,   // safe default: add (non-destructive)
            _         => AddOrReplaceResult.Add,
        };
        Log.Information(
            "[ScriptedMsgBox] AskAddOrReplace → {Result} (answer='{Answer}'): {Title}",
            result, answer ?? "(default)", title);
        return result;
    }

    public CloseWithStashResult AskCloseWithStash(string message, string title)
    {
        RecordPrompt(message, title);
        var answer = ConsumeAnswer();
        var result = answer switch
        {
            "apply"  => CloseWithStashResult.ApplyActive,
            "discard"=> CloseWithStashResult.DiscardAll,
            "cancel" => CloseWithStashResult.Cancel,
            null     => CloseWithStashResult.DiscardAll,  // safe default: discard
            _        => CloseWithStashResult.DiscardAll,
        };
        Log.Information(
            "[ScriptedMsgBox] AskCloseWithStash → {Result} (answer='{Answer}'): {Title}",
            result, answer ?? "(default)", title);
        return result;
    }

    private void RecordPrompt(string message, string title)
    {
        LastPromptMessage = message;
        LastPromptTitle = title;
    }

    private string? ConsumeAnswer()
    {
        var answer = PromptAnswer;
        PromptAnswer = null;   // consume once
        return answer;
    }
}
