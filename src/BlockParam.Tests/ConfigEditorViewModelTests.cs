using System.Globalization;
using System.Threading;
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
        vm.RuleFiles[0].Rules.Should().HaveCount(1);
        vm.RuleFiles[0].Rules[0].PathPattern.Should().Be(@".*\.moduleId$");
        vm.RuleFiles[0].Rules[0].Min.Should().Be("0");
        vm.RuleFiles[0].Rules[0].Max.Should().Be("9999");
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
        vm.RuleFiles[0].Rules[0].CommentTemplate.Should().Be("{db}.{parent}");
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
        vm.RuleFiles[0].Rules[0].ExcludeFromSetpoints.Should().BeTrue();
    }

    [Fact]
    public void Save_RuleFile_WritesToDisk()
    {
        var loader = CreateLoader();
        var vm = new ConfigEditorViewModel(loader);

        vm.NewFileCommand.Execute(null);
        vm.SelectedFile.Should().NotBeNull();
        vm.SelectedRule.Should().NotBeNull();
        vm.SelectedRule!.PathPattern = @".*\.Speed$";
        vm.SelectedRule.Datatype = "Int";
        vm.SelectedRule.Min = "0";
        vm.SelectedRule.Max = "3000";

        vm.SaveCommand.Execute(null);

        // Filename is auto-derived from path pattern at save time for new files.
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

        vm.SaveCommand.Execute(null);

        var reloaded = CreateLoader();
        var vm2 = new ConfigEditorViewModel(reloaded);
        vm2.RuleFiles.Should().HaveCount(1);
        vm2.RuleFiles[0].Rules[0].PathPattern.Should().Be(@".*\.moduleId$");
        vm2.RuleFiles[0].Rules[0].Min.Should().Be("0");
        vm2.RuleFiles[0].Rules[0].TagTableName.Should().Be("Const_Modules");
    }

    /// <summary>
    /// Issue #70: a multi-rule file must round-trip through the editor without
    /// losing rules 2..N. Previously RuleFileViewModel.FromFile read only
    /// config.Rules[0] and ToBulkChangeConfig emitted a single rule, so editing
    /// any field and saving silently destroyed sibling rules.
    /// </summary>
    [Fact]
    public void Save_MultiRuleFile_PreservesAllRules()
    {
        WriteRuleFile("udt-family.json", @"{
            ""version"": ""1.0"",
            ""rules"": [
                { ""pathPattern"": "".*{udt:LibTBP_msg_UDT}\\.moduleId$"",  ""tagTableReference"": { ""tableName"": ""CcModule"" } },
                { ""pathPattern"": "".*{udt:LibTBP_msg_UDT}\\.elementId$"", ""tagTableReference"": { ""tableName"": ""CcElement"" } },
                { ""pathPattern"": "".*{udt:LibTBP_msg_UDT}\\.messageId$"", ""tagTableReference"": { ""tableName"": ""CcMessage"" } }
            ]
        }");

        var loader = CreateLoader();
        var vm = new ConfigEditorViewModel(loader);

        vm.RuleFiles.Should().HaveCount(1);
        vm.RuleFiles[0].Rules.Should().HaveCount(3, "all three rules must surface in the editor");

        vm.RuleFiles[0].Rules[0].TagTableName = "CcModule_Renamed";
        vm.SaveCommand.Execute(null);

        var reloaded = CreateLoader();
        var vm2 = new ConfigEditorViewModel(reloaded);
        vm2.RuleFiles.Should().HaveCount(1);
        vm2.RuleFiles[0].Rules.Should().HaveCount(3, "siblings must not be dropped on save");
        vm2.RuleFiles[0].Rules[0].TagTableName.Should().Be("CcModule_Renamed");
        vm2.RuleFiles[0].Rules[1].TagTableName.Should().Be("CcElement");
        vm2.RuleFiles[0].Rules[2].TagTableName.Should().Be("CcMessage");
    }

    [Fact]
    public void NewRule_AddsToSelectedFile()
    {
        WriteRuleFile("existing.json", @"{
            ""version"": ""1.0"",
            ""rules"": [{ ""pathPattern"": "".*\\.first$"" }]
        }");
        var loader = CreateLoader();
        var vm = new ConfigEditorViewModel(loader);

        vm.SelectedFile = vm.RuleFiles[0];
        vm.NewRuleCommand.Execute(null);

        vm.RuleFiles.Should().HaveCount(1, "the file count stays at 1");
        vm.RuleFiles[0].Rules.Should().HaveCount(2, "a new rule was appended to the existing file");
        vm.SelectedRule.Should().Be(vm.RuleFiles[0].Rules[1]);
        vm.SelectedRule!.IsNew.Should().BeTrue();
    }

    [Fact]
    public void NewRule_WithNoFileSelected_FallsBackToNewFile()
    {
        var loader = CreateLoader();
        var vm = new ConfigEditorViewModel(loader);

        vm.NewRuleCommand.Execute(null);

        vm.RuleFiles.Should().HaveCount(1);
        vm.RuleFiles[0].Rules.Should().HaveCount(1);
        vm.RuleFiles[0].IsNew.Should().BeTrue();
    }

    [Fact]
    public void NewFile_CreatesEntryWithAutoName()
    {
        var loader = CreateLoader();
        var vm = new ConfigEditorViewModel(loader);

        vm.NewFileCommand.Execute(null);

        vm.RuleFiles.Should().HaveCount(1);
        vm.RuleFiles[0].FileName.Should().Be("new-rule.json");
        vm.RuleFiles[0].FileType.Should().Be("Rule");
        vm.RuleFiles[0].IsNew.Should().BeTrue();
        vm.RuleFiles[0].Rules.Should().HaveCount(1);
        vm.SelectedRule.Should().Be(vm.RuleFiles[0].Rules[0]);
    }

    [Fact]
    public void NewFile_PathPatternAutoDerivesFileNameOnSave()
    {
        var loader = CreateLoader();
        var vm = new ConfigEditorViewModel(loader);

        vm.NewFileCommand.Execute(null);
        vm.SelectedRule!.PathPattern = @".*\.elementId$";
        vm.SaveCommand.Execute(null);

        File.Exists(Path.Combine(_rulesDir, "_any_.elementId.json")).Should().BeTrue();
    }

    [Fact]
    public void NewFile_TwoNewFilesWithSameAutoName_BothPersisted()
    {
        // #72: two "+ File" entries with identical first-rule PathPattern used
        // to derive the same filename and the second silently overwrote the
        // first. SaveAll must now suffix the second to keep both on disk.
        var loader = CreateLoader();
        var vm = new ConfigEditorViewModel(loader);

        vm.NewFileCommand.Execute(null);
        var first = vm.RuleFiles[0];
        first.Rules[0].PathPattern = @".*\.Speed$";

        vm.NewFileCommand.Execute(null);
        var second = vm.RuleFiles[1];
        second.Rules[0].PathPattern = @".*\.Speed$";

        vm.SaveCommand.Execute(null);

        File.Exists(Path.Combine(_rulesDir, "_any_.Speed.json")).Should().BeTrue();
        File.Exists(Path.Combine(_rulesDir, "_any_.Speed-2.json")).Should().BeTrue();
        vm.RuleFiles.Should().HaveCount(2);
    }

    [Fact]
    public void NewFile_AutoNameCollidesWithExistingOnDisk_Suffixed()
    {
        // #72 sibling case: an on-disk file that loaded as a fixed-name entry
        // already claims the auto-derived name. The newly added file must
        // suffix rather than overwrite.
        WriteRuleFile("_any_.Speed.json",
            @"{ ""version"": ""1.0"", ""rules"": [{ ""pathPattern"": "".*\\.Speed$"" }] }");

        var loader = CreateLoader();
        var vm = new ConfigEditorViewModel(loader);

        vm.NewFileCommand.Execute(null);
        vm.RuleFiles.Last().Rules[0].PathPattern = @".*\.Speed$";

        vm.SaveCommand.Execute(null);

        File.Exists(Path.Combine(_rulesDir, "_any_.Speed.json")).Should().BeTrue();
        File.Exists(Path.Combine(_rulesDir, "_any_.Speed-2.json")).Should().BeTrue();
    }

    [Fact]
    public void DeleteSelected_RemovesRuleAndFileWhenLast()
    {
        WriteRuleFile("toDelete.json",
            @"{ ""version"": ""1.0"", ""rules"": [{ ""pathPattern"": "".*\\.x$"" }] }");
        var loader = CreateLoader();
        var vm = new ConfigEditorViewModel(loader);

        vm.SelectedRule = vm.RuleFiles[0].Rules[0];
        vm.DeleteSelectedCommand.Execute(null);

        vm.RuleFiles.Should().BeEmpty();
        File.Exists(Path.Combine(_rulesDir, "toDelete.json")).Should().BeFalse();
    }

    [Fact]
    public void DeleteSelected_OnMultiRuleFile_RemovesOnlyRule()
    {
        WriteRuleFile("two-rules.json", @"{
            ""version"": ""1.0"",
            ""rules"": [
                { ""pathPattern"": "".*\\.a$"" },
                { ""pathPattern"": "".*\\.b$"" }
            ]
        }");
        var loader = CreateLoader();
        var vm = new ConfigEditorViewModel(loader);

        vm.SelectedRule = vm.RuleFiles[0].Rules[0];
        vm.DeleteSelectedCommand.Execute(null);

        vm.RuleFiles.Should().HaveCount(1, "the file remains because the second rule still lives in it");
        vm.RuleFiles[0].Rules.Should().HaveCount(1);
        vm.RuleFiles[0].Rules[0].PathPattern.Should().Be(@".*\.b$");
    }

    [Fact]
    public void Validation_RuleMissingPathPattern_ShowsWarning() => WithEnglishUICulture(() =>
    {
        var loader = CreateLoader();
        var vm = new ConfigEditorViewModel(loader);

        vm.NewFileCommand.Execute(null);
        vm.SaveCommand.Execute(null);

        vm.ValidationMessage.Should().Contain("path pattern");
    });

    [Fact]
    public void Validation_MinGreaterThanMax_ShowsWarning() => WithEnglishUICulture(() =>
    {
        var loader = CreateLoader();
        var vm = new ConfigEditorViewModel(loader);

        vm.NewFileCommand.Execute(null);
        vm.SelectedRule!.PathPattern = @".*\.Test$";
        vm.SelectedRule.Min = "100";
        vm.SelectedRule.Max = "50";

        vm.SaveCommand.Execute(null);

        vm.ValidationMessage.Should().Contain("Min");
    });

    [Fact]
    public void Load_MultipleFiles_SortedAlphabetically()
    {
        WriteRuleFile("b-rule.json",
            @"{ ""version"": ""1.0"", ""rules"": [{ ""pathPattern"": "".*\\.b$"" }] }");
        WriteRuleFile("a-rule.json",
            @"{ ""version"": ""1.0"", ""rules"": [{ ""pathPattern"": "".*\\.a$"" }] }");

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

        vm.NewFileCommand.Execute(null);
        vm.SelectedRule!.PathPattern = @".*{udt:messageConfig_UDT}$";
        vm.SelectedRule.CommentTemplate = "{db}.{parent}";

        vm.SaveCommand.Execute(null);

        var loader2 = CreateLoader();
        var vm2 = new ConfigEditorViewModel(loader2);
        vm2.RuleFiles.Should().HaveCount(1);
        vm2.RuleFiles[0].Rules[0].CommentTemplate.Should().Be("{db}.{parent}");
    }

    [Fact]
    public void Save_RuleWithoutCommentTemplate_OmitsField()
    {
        var loader = CreateLoader();
        var vm = new ConfigEditorViewModel(loader);

        vm.NewFileCommand.Execute(null);
        vm.SelectedRule!.PathPattern = @".*\.moduleId$";

        vm.SaveCommand.Execute(null);

        var expectedFile = Path.Combine(_rulesDir, vm.SelectedFile!.FileName);
        var json = File.ReadAllText(expectedFile);
        json.Should().NotContain("commentTemplate");
    }

    [Fact]
    public void DirtyTracking_NewFile_IsDirty()
    {
        var loader = CreateLoader();
        var vm = new ConfigEditorViewModel(loader);

        vm.NewFileCommand.Execute(null);
        vm.SelectedFile!.IsDirty.Should().BeTrue();
        vm.SelectedRule!.IsDirty.Should().BeTrue();
    }

    [Fact]
    public void DirtyTracking_LoadedFile_IsClean()
    {
        WriteRuleFile("clean.json",
            @"{ ""version"": ""1.0"", ""rules"": [{ ""pathPattern"": "".*\\.x$"" }] }");
        var loader = CreateLoader();
        var vm = new ConfigEditorViewModel(loader);

        vm.RuleFiles[0].IsDirty.Should().BeFalse();
        vm.RuleFiles[0].Rules[0].IsDirty.Should().BeFalse();
    }

    [Fact]
    public void DirtyTracking_ModifiedRule_FileIsAlsoDirty()
    {
        WriteRuleFile("clean.json",
            @"{ ""version"": ""1.0"", ""rules"": [{ ""pathPattern"": "".*\\.x$"" }] }");
        var loader = CreateLoader();
        var vm = new ConfigEditorViewModel(loader);

        vm.RuleFiles[0].Rules[0].Datatype = "Int";

        vm.RuleFiles[0].Rules[0].IsDirty.Should().BeTrue();
        vm.RuleFiles[0].IsDirty.Should().BeTrue("the file aggregates dirtiness from its rules");
    }

    [Fact]
    public void DirtyTracking_SaveCleansState()
    {
        var loader = CreateLoader();
        var vm = new ConfigEditorViewModel(loader);

        vm.NewFileCommand.Execute(null);
        vm.SelectedRule!.PathPattern = @".*\.test$";
        vm.SelectedRule.IsDirty.Should().BeTrue();

        vm.SaveCommand.Execute(null);
        vm.SelectedRule.IsDirty.Should().BeFalse();
        vm.SelectedFile!.IsDirty.Should().BeFalse();
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

    [Fact]
    public void HeaderSummary_ReflectsRuleCount() => WithEnglishUICulture(() =>
    {
        var loader = CreateLoader();
        var vm = new ConfigEditorViewModel(loader);

        vm.NewFileCommand.Execute(null);
        vm.SelectedFile!.HeaderSummary.Should().Contain("1 rule");

        vm.NewRuleCommand.Execute(null);
        vm.SelectedFile.HeaderSummary.Should().Contain("2 rules");
    });

    private static void WithEnglishUICulture(Action action)
    {
        var prevUI = Thread.CurrentThread.CurrentUICulture;
        var prev = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentUICulture = new CultureInfo("en-US");
        Thread.CurrentThread.CurrentCulture = new CultureInfo("en-US");
        try { action(); }
        finally
        {
            Thread.CurrentThread.CurrentUICulture = prevUI;
            Thread.CurrentThread.CurrentCulture = prev;
        }
    }
}
