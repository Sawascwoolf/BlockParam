using FluentAssertions;
using BlockParam.Config;
using BlockParam.Services;
using BlockParam.UI;
using Xunit;

namespace BlockParam.Tests;

/// <summary>
/// Coverage for the ConfigEditor rule import/export commands (#36). The file
/// dialog is faked via <see cref="FakeFileDialogService"/> so the open/save
/// path choices are scripted; file I/O still goes through the real
/// <see cref="RuleFileRepository"/> against per-test temp directories, mirroring
/// the sibling <c>ConfigEditorViewModelCommandTests</c> harness.
/// </summary>
public class ConfigEditorImportExportTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _rulesDir;
    private readonly string _externalDir;

    public ConfigEditorImportExportTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"BlockParamImpExpTest_{Guid.NewGuid():N}");
        _rulesDir = Path.Combine(_tempDir, "rules");
        _externalDir = Path.Combine(_tempDir, "external");
        Directory.CreateDirectory(_rulesDir);
        Directory.CreateDirectory(_externalDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string ConfigPath => Path.Combine(_tempDir, "config.json");
    private ConfigLoader CreateLoader() => new ConfigLoader(ConfigPath);

    private ConfigEditorViewModel CreateVm(FakeFileDialogService dialogs) =>
        new ConfigEditorViewModel(CreateLoader(), null, RuleFileRepository.Default, dialogs);

    private void WriteRuleFile(string fileName, string json) =>
        File.WriteAllText(Path.Combine(_rulesDir, fileName), json);

    private string WriteExternalRuleFile(string fileName, string json)
    {
        var path = Path.Combine(_externalDir, fileName);
        File.WriteAllText(path, json);
        return path;
    }

    private const string SampleRule = @"{
        ""version"": ""1.0"",
        ""rules"": [{
            ""pathPattern"": "".*\\.speed$"", ""datatype"": ""Int"",
            ""commentTemplate"": ""speed setpoint""
        }]
    }";

    // ---- Export ------------------------------------------------------------

    [Fact]
    public void Export_WritesSelectedFileToChosenPath()
    {
        WriteRuleFile("speed.json", SampleRule);
        var target = Path.Combine(_externalDir, "exported.json");
        var dialogs = new FakeFileDialogService { SaveResult = target };
        var vm = CreateVm(dialogs);

        vm.SelectedFile = vm.RuleFiles.Single();
        vm.ExportSelectedCommand.Execute(null);

        File.Exists(target).Should().BeTrue("export writes the file to the chosen path");
        dialogs.LastSaveSuggestedName.Should().Be("speed.json", "the save dialog is seeded with the file's name");
        vm.ValidationMessage.Should().Contain("exported.json");
    }

    [Fact]
    public void Export_RoundTripsToIdenticalConfig()
    {
        WriteRuleFile("speed.json", SampleRule);
        var target = Path.Combine(_externalDir, "exported.json");
        var vm = CreateVm(new FakeFileDialogService { SaveResult = target });

        var original = vm.RuleFiles.Single().ToBulkChangeConfig();
        vm.SelectedFile = vm.RuleFiles.Single();
        vm.ExportSelectedCommand.Execute(null);

        var reloaded = ConfigLoader.Deserialize(File.ReadAllText(target));
        reloaded.Should().NotBeNull();
        ConfigLoader.SerializeRuleFile(reloaded!).Should()
            .Be(ConfigLoader.SerializeRuleFile(original),
                "export -> import on the same machine reproduces an identical file");
    }

    [Fact]
    public void Export_CancelledSaveWritesNothing()
    {
        WriteRuleFile("speed.json", SampleRule);
        var dialogs = new FakeFileDialogService { SaveResult = null }; // user cancels
        var vm = CreateVm(dialogs);

        vm.SelectedFile = vm.RuleFiles.Single();
        vm.ExportSelectedCommand.Execute(null);

        Directory.GetFiles(_externalDir).Should().BeEmpty("a cancelled save writes nothing");
        vm.ValidationMessage.Should().BeEmpty("cancelling is not an error");
    }

    [Fact]
    public void Export_NoSelection_ReportsAndDoesNotPrompt()
    {
        var dialogs = new FakeFileDialogService { SaveResult = Path.Combine(_externalDir, "x.json") };
        var vm = CreateVm(dialogs);

        vm.SelectedFile.Should().BeNull("precondition: nothing selected");
        vm.ExportSelectedCommand.Execute(null);

        dialogs.SaveCalled.Should().BeFalse("export bails before showing a dialog when nothing is selected");
        vm.ValidationMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void ExportSelected_CanExecute_TracksSelection()
    {
        WriteRuleFile("speed.json", SampleRule);
        var vm = CreateVm(new FakeFileDialogService());

        vm.ExportSelectedCommand.CanExecute(null).Should().BeFalse("no file selected yet");
        vm.SelectedFile = vm.RuleFiles.Single();
        vm.ExportSelectedCommand.CanExecute(null).Should().BeTrue("a file is now selected");
    }

    // ---- Import ------------------------------------------------------------

    [Fact]
    public void Import_StagesValidFileButDoesNotWriteUntilSave()
    {
        var src = WriteExternalRuleFile("incoming.json", SampleRule);
        var dialogs = new FakeFileDialogService { OpenResult = new[] { src } };
        var vm = CreateVm(dialogs);

        vm.ImportFilesCommand.Execute(null);

        var staged = vm.RuleFiles.Should().ContainSingle().Subject;
        staged.FileName.Should().Be("incoming.json");
        staged.IsNew.Should().BeTrue("imported file is staged, not yet committed");
        staged.Rules.Should().ContainSingle().Which.PathPattern.Should().Be(@".*\.speed$");
        File.Exists(Path.Combine(_rulesDir, "incoming.json")).Should()
            .BeFalse("import only stages — nothing on disk until Save");
        vm.SelectedFile.Should().BeSameAs(staged, "the imported file is selected for review");

        vm.SaveCommand.Execute(null);
        File.Exists(Path.Combine(_rulesDir, "incoming.json")).Should()
            .BeTrue("Save commits the staged import to the local rules directory");
    }

    [Fact]
    public void Import_UniquifiesNameAgainstExistingFile()
    {
        WriteRuleFile("incoming.json", SampleRule);                  // already on disk
        var src = WriteExternalRuleFile("incoming.json", SampleRule); // same name imported
        var dialogs = new FakeFileDialogService { OpenResult = new[] { src } };
        var vm = CreateVm(dialogs);

        vm.ImportFilesCommand.Execute(null);

        var imported = vm.RuleFiles.Single(f => f.IsNew);
        imported.FileName.Should().Be("incoming-2.json",
            "an import never silently overwrites an existing file");
    }

    [Fact]
    public void Import_SkipsInvalidFilesAndReports()
    {
        var good = WriteExternalRuleFile("good.json", SampleRule);
        var bad = WriteExternalRuleFile("bad.json", "{ not valid json");
        var nullDoc = WriteExternalRuleFile("nulldoc.json", "null");
        var dialogs = new FakeFileDialogService { OpenResult = new[] { good, bad, nullDoc } };
        var vm = CreateVm(dialogs);

        vm.ImportFilesCommand.Execute(null);

        vm.RuleFiles.Should().ContainSingle("only the valid file is staged")
            .Which.FileName.Should().Be("good.json");
        vm.ValidationMessage.Should().Contain("bad.json").And.Contain("nulldoc.json");
    }

    [Fact]
    public void Import_CancelledOpenDoesNothing()
    {
        var dialogs = new FakeFileDialogService { OpenResult = Array.Empty<string>() };
        var vm = CreateVm(dialogs);

        vm.ImportFilesCommand.Execute(null);

        vm.RuleFiles.Should().BeEmpty("a cancelled open imports nothing");
        vm.ValidationMessage.Should().BeEmpty("cancelling is not an error");
    }

    private sealed class FakeFileDialogService : IFileDialogService
    {
        public string[] OpenResult = Array.Empty<string>();
        public string? SaveResult;
        public string? LastSaveSuggestedName;
        public bool SaveCalled;

        public string[] OpenFiles(string title, string filter, bool multiselect) => OpenResult;

        public string? SaveFile(string title, string filter, string suggestedFileName)
        {
            SaveCalled = true;
            LastSaveSuggestedName = suggestedFileName;
            return SaveResult;
        }
    }
}
