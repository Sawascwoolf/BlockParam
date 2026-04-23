using FluentAssertions;
using BlockParam.Config;
using BlockParam.UI;
using Xunit;

namespace BlockParam.Tests;

public class ConfigEditorViewModelTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _rulesDir;

    public ConfigEditorViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"BlockParamConfigTest_{Guid.NewGuid():N}");
        _rulesDir = Path.Combine(_tempDir, "rules");
        Directory.CreateDirectory(_rulesDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string ConfigPath => Path.Combine(_tempDir, "config.json");

    private ConfigLoader CreateLoader()
    {
        return new ConfigLoader(ConfigPath);
    }

    private void WriteRuleFile(string fileName, string json)
    {
        File.WriteAllText(Path.Combine(_rulesDir, fileName), json);
    }

    [Fact]
    public void Load_RuleFilesFromLocalDirectory_PopulatesList()
    {
        WriteRuleFile("moduleId.json", @"{
            ""version"": ""1.0"",
            ""rules"": [{ ""pathPattern"": "".*\\.moduleId$"", ""datatype"": ""Int"",
                ""constraints"": { ""min"": 0, ""max"": 9999 } }]
        }");

        var loader = CreateLoader();
        var vm = new ConfigEditorViewModel(loader);

        vm.RuleFiles.Should().HaveCount(1);
        vm.RuleFiles[0].FileName.Should().Be("moduleId.json");
        vm.RuleFiles[0].FileType.Should().Be("Rule");
        vm.RuleFiles[0].PathPattern.Should().Be(@".*\.moduleId$");
        vm.RuleFiles[0].Min.Should().Be("0");
        vm.RuleFiles[0].Max.Should().Be("9999");
    }

    [Fact]
    public void Load_RuleWithCommentTemplate_PopulatesField()
    {
        WriteRuleFile("comment-rule.json", @"{
            ""version"": ""1.0"",
            ""rules"": [{
                ""pathPattern"": "".*{udt:messageConfig_UDT}$"",
                ""commentTemplate"": ""{db}.{parent}"",
                ""commentLanguage"": ""en-GB""
            }]
        }");

        var loader = CreateLoader();
        var vm = new ConfigEditorViewModel(loader);

        vm.RuleFiles.Should().HaveCount(1);
        vm.RuleFiles[0].FileType.Should().Be("Rule");
        vm.RuleFiles[0].CommentTemplate.Should().Be("{db}.{parent}");
    }

    [Fact]
    public void Load_RuleWithExclude_PopulatesCheckbox()
    {
        WriteRuleFile("exclude-actual.json", @"{
            ""version"": ""1.0"",
            ""rules"": [{
                ""pathPattern"": "".*\\.actualValue$"",
                ""excludeFromSetpoints"": true
            }]
        }");

        var loader = CreateLoader();
        var vm = new ConfigEditorViewModel(loader);

        vm.RuleFiles.Should().HaveCount(1);
        vm.RuleFiles[0].FileType.Should().Be("Rule");
        vm.RuleFiles[0].ExcludeFromSetpoints.Should().BeTrue();
    }

    [Fact]
    public void Save_RuleFile_WritesToDisk()
    {
        var loader = CreateLoader();
        var vm = new ConfigEditorViewModel(loader);

        // Create a new rule via command (no longer prompts for filename)
        vm.NewRuleCommand.Execute(null);
        vm.SelectedFile.Should().NotBeNull();
        vm.SelectedFile!.PathPattern = @".*\.Speed$";
        vm.SelectedFile.Datatype = "Int";
        vm.SelectedFile.Min = "0";
        vm.SelectedFile.Max = "3000";

        // Filename is auto-derived from path pattern
        vm.SelectedFile.FileName.Should().Be("_any_.Speed.json");

        vm.SaveCommand.Execute(null);

        File.Exists(Path.Combine(_rulesDir, "_any_.Speed.json")).Should().BeTrue();
    }

    [Fact]
    public void Save_RoundTrip_PreservesAllFields()
    {
        WriteRuleFile("moduleId.json", @"{
            ""version"": ""1.0"",
            ""rules"": [{
                ""pathPattern"": "".*\\.moduleId$"", ""datatype"": ""Int"",
                ""constraints"": { ""min"": 0, ""max"": 100, ""allowedValues"": [1, 2, 3] },
                ""tagTableReference"": { ""tableName"": ""Const_Modules"" }
            }]
        }");
        var loader = CreateLoader();
        var vm = new ConfigEditorViewModel(loader);

        // Save without changes
        vm.SaveCommand.Execute(null);

        var reloaded = CreateLoader();
        var vm2 = new ConfigEditorViewModel(reloaded);
        vm2.RuleFiles.Should().HaveCount(1);
        vm2.RuleFiles[0].PathPattern.Should().Be(@".*\.moduleId$");
        vm2.RuleFiles[0].Min.Should().Be("0");
        vm2.RuleFiles[0].TagTableName.Should().Be("Const_Modules");
    }

    [Fact]
    public void NewRule_CreatesEntryWithAutoName()
    {
        var loader = CreateLoader();
        var vm = new ConfigEditorViewModel(loader);

        vm.NewRuleCommand.Execute(null);

        vm.RuleFiles.Should().HaveCount(1);
        vm.RuleFiles[0].FileName.Should().Be("new-rule.json");
        vm.RuleFiles[0].FileType.Should().Be("Rule");
        vm.RuleFiles[0].IsNew.Should().BeTrue();
        vm.SelectedFile.Should().Be(vm.RuleFiles[0]);
    }

    [Fact]
    public void NewRule_PathPatternAutoSyncsFileName()
    {
        var loader = CreateLoader();
        var vm = new ConfigEditorViewModel(loader);

        vm.NewRuleCommand.Execute(null);
        vm.SelectedFile!.PathPattern = @".*\.elementId$";

        vm.SelectedFile.FileName.Should().Be("_any_.elementId.json");
    }

    [Fact]
    public void DeleteSelected_RemovesFileAndDeletesFromDisk()
    {
        WriteRuleFile("toDelete.json", @"{ ""version"": ""1.0"", ""rules"": [{ ""pathPattern"": "".*\\.x$"" }] }");
        var loader = CreateLoader();
        var vm = new ConfigEditorViewModel(loader);

        vm.SelectedFile = vm.RuleFiles[0];
        vm.DeleteSelectedCommand.Execute(null);

        vm.RuleFiles.Should().BeEmpty();
        File.Exists(Path.Combine(_rulesDir, "toDelete.json")).Should().BeFalse();
    }

    [Fact]
    public void Validation_RuleMissingPathPattern_ShowsWarning()
    {
        var loader = CreateLoader();
        var vm = new ConfigEditorViewModel(loader);

        vm.NewRuleCommand.Execute(null);
        // PathPattern is empty — validation should fail
        vm.SaveCommand.Execute(null);

        vm.ValidationMessage.Should().Contain("path pattern");
    }

    [Fact]
    public void Validation_MinGreaterThanMax_ShowsWarning()
    {
        var loader = CreateLoader();
        var vm = new ConfigEditorViewModel(loader);

        vm.NewRuleCommand.Execute(null);
        vm.SelectedFile!.PathPattern = @".*\.Test$";
        vm.SelectedFile.Min = "100";
        vm.SelectedFile.Max = "50";

        vm.SaveCommand.Execute(null);

        vm.ValidationMessage.Should().Contain("Min");
    }

    [Fact]
    public void Load_MultipleFiles_SortedAlphabetically()
    {
        WriteRuleFile("b-rule.json", @"{ ""version"": ""1.0"", ""rules"": [{ ""pathPattern"": "".*\\.b$"" }] }");
        WriteRuleFile("a-rule.json", @"{ ""version"": ""1.0"", ""rules"": [{ ""pathPattern"": "".*\\.a$"" }] }");

        var loader = CreateLoader();
        var vm = new ConfigEditorViewModel(loader);

        vm.RuleFiles.Should().HaveCount(2);
        vm.RuleFiles[0].FileName.Should().Be("a-rule.json");
        vm.RuleFiles[1].FileName.Should().Be("b-rule.json");
    }

    [Fact]
    public void Save_RuleWithCommentTemplate_RoundTrip()
    {
        var loader = CreateLoader();
        var vm = new ConfigEditorViewModel(loader);

        vm.NewRuleCommand.Execute(null);
        vm.SelectedFile!.PathPattern = @".*{udt:messageConfig_UDT}$";
        vm.SelectedFile.CommentTemplate = "{db}.{parent}";

        vm.SaveCommand.Execute(null);

        var loader2 = CreateLoader();
        var vm2 = new ConfigEditorViewModel(loader2);
        vm2.RuleFiles.Should().HaveCount(1);
        vm2.RuleFiles[0].CommentTemplate.Should().Be("{db}.{parent}");
    }

    [Fact]
    public void Save_RuleWithoutCommentTemplate_OmitsField()
    {
        var loader = CreateLoader();
        var vm = new ConfigEditorViewModel(loader);

        vm.NewRuleCommand.Execute(null);
        vm.SelectedFile!.PathPattern = @".*\.moduleId$";

        vm.SaveCommand.Execute(null);

        // Filename is auto-derived from pattern
        var expectedFile = Path.Combine(_rulesDir, vm.SelectedFile.FileName);
        var json = File.ReadAllText(expectedFile);
        json.Should().NotContain("commentTemplate");
    }

    [Fact]
    public void DirtyTracking_NewRule_IsDirty()
    {
        var loader = CreateLoader();
        var vm = new ConfigEditorViewModel(loader);

        vm.NewRuleCommand.Execute(null);
        vm.SelectedFile!.IsDirty.Should().BeTrue();
    }

    [Fact]
    public void DirtyTracking_LoadedRule_IsClean()
    {
        WriteRuleFile("clean.json", @"{ ""version"": ""1.0"", ""rules"": [{ ""pathPattern"": "".*\\.x$"" }] }");
        var loader = CreateLoader();
        var vm = new ConfigEditorViewModel(loader);

        vm.RuleFiles[0].IsDirty.Should().BeFalse();
    }

    [Fact]
    public void DirtyTracking_ModifiedRule_IsDirty()
    {
        WriteRuleFile("clean.json", @"{ ""version"": ""1.0"", ""rules"": [{ ""pathPattern"": "".*\\.x$"" }] }");
        var loader = CreateLoader();
        var vm = new ConfigEditorViewModel(loader);

        vm.RuleFiles[0].Datatype = "Int";
        vm.RuleFiles[0].IsDirty.Should().BeTrue();
    }

    [Fact]
    public void DirtyTracking_SaveCleansState()
    {
        var loader = CreateLoader();
        var vm = new ConfigEditorViewModel(loader);

        vm.NewRuleCommand.Execute(null);
        vm.SelectedFile!.PathPattern = @".*\.test$";
        vm.SelectedFile.IsDirty.Should().BeTrue();

        vm.SaveCommand.Execute(null);
        vm.SelectedFile.IsDirty.Should().BeFalse();
    }

    [Fact]
    public void PatternToFileName_BasicPattern()
    {
        RuleFileViewModel.PatternToFileName(@".*\.messageId$")
            .Should().Be("_any_.messageId.json");
    }

    [Fact]
    public void PatternToFileName_UdtToken()
    {
        RuleFileViewModel.PatternToFileName(@".*{udt:messageConfig_UDT}\.messageId$")
            .Should().Be("_any_messageConfig_UDT.messageId.json");
    }

    [Fact]
    public void PatternToFileName_EmptyPattern()
    {
        RuleFileViewModel.PatternToFileName("")
            .Should().Be("new-rule.json");
    }

}
