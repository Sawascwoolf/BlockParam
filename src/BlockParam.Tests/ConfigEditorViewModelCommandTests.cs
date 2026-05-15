using FluentAssertions;
using BlockParam.Config;
using BlockParam.UI;
using Xunit;

namespace BlockParam.Tests;

/// <summary>
/// Command-level coverage for <see cref="ConfigEditorViewModel"/> commands that
/// the sibling <c>ConfigEditorViewModelTests</c> does not exercise:
/// <c>DuplicateSelectedCommand</c>, <c>DeleteFileCommand</c>,
/// <c>ResetToBaseCommand</c> and <c>SaveAndCloseCommand</c>. Construction /
/// temp-dir / ConfigLoader plumbing mirrors the sibling test harness; helpers
/// are copied (not shared) so the two classes stay independent.
/// </summary>
public class ConfigEditorViewModelCommandTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _rulesDir;
    private readonly string _projectDir;
    private readonly string _projectRulesDir;

    public ConfigEditorViewModelCommandTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"BlockParamConfigCmdTest_{Guid.NewGuid():N}");
        _rulesDir = Path.Combine(_tempDir, "rules");
        // AppDirectories.ProjectRulesDir => {projectDir}\UserFiles\BlockParam
        _projectDir = Path.Combine(_tempDir, "tiaProject");
        _projectRulesDir = Path.Combine(_projectDir, "UserFiles", "BlockParam");
        Directory.CreateDirectory(_rulesDir);
        Directory.CreateDirectory(_projectRulesDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string ConfigPath => Path.Combine(_tempDir, "config.json");

    private ConfigLoader CreateLoader() => new ConfigLoader(ConfigPath);

    private void WriteRuleFile(string fileName, string json) =>
        File.WriteAllText(Path.Combine(_rulesDir, fileName), json);

    private void WriteProjectRuleFile(string fileName, string json) =>
        File.WriteAllText(Path.Combine(_projectRulesDir, fileName), json);

    // ---- DuplicateSelectedCommand ------------------------------------------

    /// <summary>
    /// DuplicateSelectedCommand.CanExecute is false when no rule is selected
    /// and true once a rule is selected (predicate is <c>SelectedRule != null</c>).
    /// </summary>
    [Fact]
    public void DuplicateSelected_CanExecute_TracksSelectedRule()
    {
        WriteRuleFile("dup.json",
            @"{ ""version"": ""1.0"", ""rules"": [{ ""pathPattern"": "".*\\.a$"" }] }");
        var vm = new ConfigEditorViewModel(CreateLoader());

        vm.DuplicateSelectedCommand.CanExecute(null).Should()
            .BeFalse("no rule is selected yet");

        vm.SelectedRule = vm.RuleFiles[0].Rules[0];
        vm.DuplicateSelectedCommand.CanExecute(null).Should()
            .BeTrue("a rule is now selected");
    }

    /// <summary>
    /// Executing DuplicateSelectedCommand inserts a copy of the selected rule
    /// into the same file directly after the source, copies the field values,
    /// flags the copy as new, and selects the copy.
    /// </summary>
    [Fact]
    public void DuplicateSelected_AddsCopyAfterSourceInSameFile()
    {
        WriteRuleFile("dup.json", @"{
            ""version"": ""1.0"",
            ""rules"": [{
                ""pathPattern"": "".*\\.speed$"", ""datatype"": ""Int"",
                ""tagTableReference"": { ""tableName"": ""Const_Speed"" }
            }]
        }");
        var vm = new ConfigEditorViewModel(CreateLoader());
        var file = vm.RuleFiles[0];
        var source = file.Rules[0];
        vm.SelectedRule = source;

        vm.DuplicateSelectedCommand.Execute(null);

        vm.RuleFiles.Should().HaveCount(1, "duplication stays within the same file");
        file.Rules.Should().HaveCount(2, "a duplicate rule was added");
        var copy = file.Rules[1];
        copy.Should().NotBeSameAs(source, "the duplicate is a distinct instance");
        copy.PathPattern.Should().Be(source.PathPattern, "field values are copied");
        copy.Datatype.Should().Be("Int", "field values are copied");
        copy.TagTableName.Should().Be("Const_Speed", "field values are copied");
        copy.IsNew.Should().BeTrue("the duplicate is an unsaved new rule");
        vm.SelectedRule.Should().BeSameAs(copy, "the new copy becomes the selection");
    }

    // ---- DeleteFileCommand -------------------------------------------------

    /// <summary>
    /// DeleteFileCommand.CanExecute requires the parameter to be a
    /// <see cref="RuleFileViewModel"/>; null and other types return false.
    /// </summary>
    [Fact]
    public void DeleteFile_CanExecute_RequiresRuleFileParameter()
    {
        WriteRuleFile("f.json",
            @"{ ""version"": ""1.0"", ""rules"": [{ ""pathPattern"": "".*\\.a$"" }] }");
        var vm = new ConfigEditorViewModel(CreateLoader());

        vm.DeleteFileCommand.CanExecute(null).Should()
            .BeFalse("null is not a RuleFileViewModel");
        vm.DeleteFileCommand.CanExecute("not-a-file").Should()
            .BeFalse("a string is not a RuleFileViewModel");
        vm.DeleteFileCommand.CanExecute(vm.RuleFiles[0]).Should()
            .BeTrue("a RuleFileViewModel parameter is accepted");
    }

    /// <summary>
    /// Executing DeleteFileCommand with a RuleFileViewModel removes that file
    /// from the editor's <c>RuleFiles</c> collection and deletes it from disk.
    /// No message-box / confirm seam exists on the VM, so the command is
    /// directly testable.
    /// </summary>
    [Fact]
    public void DeleteFile_RemovesFileFromCollectionAndDisk()
    {
        WriteRuleFile("keep.json",
            @"{ ""version"": ""1.0"", ""rules"": [{ ""pathPattern"": "".*\\.keep$"" }] }");
        WriteRuleFile("remove.json",
            @"{ ""version"": ""1.0"", ""rules"": [{ ""pathPattern"": "".*\\.gone$"" }] }");
        var vm = new ConfigEditorViewModel(CreateLoader());

        var target = vm.RuleFiles.Single(f => f.FileName == "remove.json");
        vm.DeleteFileCommand.Execute(target);

        vm.RuleFiles.Should().ContainSingle()
            .Which.FileName.Should().Be("keep.json", "only the targeted file was removed");
        File.Exists(Path.Combine(_rulesDir, "remove.json")).Should()
            .BeFalse("the deleted file is removed from disk");
        File.Exists(Path.Combine(_rulesDir, "keep.json")).Should()
            .BeTrue("the untouched file stays on disk");
    }

    // ---- ResetToBaseCommand ------------------------------------------------

    /// <summary>
    /// ResetToBaseCommand.CanExecute is false for a file with no lower-priority
    /// versions and true for a file that overrides another source's same-named
    /// file (predicate is <c>SelectedFile?.HasOverrides == true</c>).
    /// </summary>
    [Fact]
    public void ResetToBase_CanExecute_TracksHasOverrides()
    {
        // Same filename in Local and TiaProject -> the project copy wins and
        // carries the local copy as an OverriddenVersion (HasOverrides == true).
        const string body = @"{ ""version"": ""1.0"", ""rules"": [{ ""pathPattern"": "".*\\.x$"" }] }";
        WriteRuleFile("shared-name.json", body);
        WriteProjectRuleFile("shared-name.json", body);
        WriteRuleFile("plain.json", body);

        var loader = CreateLoader();
        loader.SetTiaProjectPath(_projectDir);
        var vm = new ConfigEditorViewModel(loader);

        var plain = vm.RuleFiles.Single(f => f.FileName == "plain.json");
        vm.SelectedFile = plain;
        vm.ResetToBaseCommand.CanExecute(null).Should()
            .BeFalse("the plain file has no overridden versions");

        var overriding = vm.RuleFiles.Single(f => f.FileName == "shared-name.json");
        overriding.HasOverrides.Should().BeTrue("same name exists in two sources");
        vm.SelectedFile = overriding;
        vm.ResetToBaseCommand.CanExecute(null).Should()
            .BeTrue("the selected file overrides a lower-priority version");
    }

    /// <summary>
    /// Executing ResetToBaseCommand deletes the higher-priority override file
    /// from disk while leaving the base (lower-priority) version intact, so a
    /// subsequent reload no longer reports the file as an override.
    /// </summary>
    [Fact]
    public void ResetToBase_DeletesOverrideFile_KeepsBase()
    {
        const string body = @"{ ""version"": ""1.0"", ""rules"": [{ ""pathPattern"": "".*\\.x$"" }] }";
        WriteRuleFile("shared-name.json", body);          // Local  = base
        WriteProjectRuleFile("shared-name.json", body);    // Project = override (wins)

        var loader = CreateLoader();
        loader.SetTiaProjectPath(_projectDir);
        var vm = new ConfigEditorViewModel(loader);

        var overriding = vm.RuleFiles.Single(f => f.FileName == "shared-name.json");
        overriding.HasOverrides.Should().BeTrue("precondition: the file is an override");
        vm.SelectedFile = overriding;

        vm.ResetToBaseCommand.Execute(null);

        File.Exists(Path.Combine(_projectRulesDir, "shared-name.json")).Should()
            .BeFalse("the override copy is deleted by reset-to-base");
        File.Exists(Path.Combine(_rulesDir, "shared-name.json")).Should()
            .BeTrue("the lower-priority base copy is preserved");

        // Reload from scratch: with the override gone the surviving file is no
        // longer reported as overriding anything.
        var freshLoader = CreateLoader();
        freshLoader.SetTiaProjectPath(_projectDir);
        var vm2 = new ConfigEditorViewModel(freshLoader);
        vm2.RuleFiles.Single(f => f.FileName == "shared-name.json")
            .HasOverrides.Should().BeFalse("the override was removed, only the base remains");
    }

    // ---- SaveAndCloseCommand -----------------------------------------------

    /// <summary>
    /// SaveAndCloseCommand persists the same observable effect as SaveCommand
    /// (the new file is written to disk with the auto-derived name) AND raises
    /// the <c>RequestClose</c> event so the dialog can close.
    /// </summary>
    [Fact]
    public void SaveAndClose_SavesAndRaisesRequestClose()
    {
        var vm = new ConfigEditorViewModel(CreateLoader());
        var closeRaised = false;
        vm.RequestClose += () => closeRaised = true;

        vm.NewFileCommand.Execute(null);
        vm.SelectedRule!.PathPattern = @".*\.elementId$";

        vm.SaveAndCloseCommand.Execute(null);

        File.Exists(Path.Combine(_rulesDir, "_any_.elementId.json")).Should()
            .BeTrue("save-and-close persists the file just like Save");
        closeRaised.Should().BeTrue("RequestClose fires once the save succeeds");
    }

    /// <summary>
    /// When the save half of SaveAndCloseCommand fails validation (a new file
    /// with no path pattern), the close signal is NOT raised — the dialog must
    /// stay open so the user can fix the error.
    /// </summary>
    [Fact]
    public void SaveAndClose_DoesNotCloseWhenSaveFailsValidation()
    {
        var vm = new ConfigEditorViewModel(CreateLoader());
        var closeRaised = false;
        vm.RequestClose += () => closeRaised = true;

        vm.NewFileCommand.Execute(null); // rule has no PathPattern -> invalid

        vm.SaveAndCloseCommand.Execute(null);

        closeRaised.Should().BeFalse("a failed save must keep the dialog open");
        vm.ValidationMessage.Should().NotBeNullOrEmpty(
            "validation surfaces a message instead of closing");
    }
}
