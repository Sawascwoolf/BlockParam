using FluentAssertions;
using NSubstitute;
using BlockParam.Config;
using BlockParam.Models;
using BlockParam.Services;
using BlockParam.SimaticML;
using Xunit;

namespace BlockParam.Tests;

public class BulkChangeServiceTests : IDisposable
{
    private readonly SimaticMLParser _parser = new();
    private readonly ChangeLogger _logger = new();
    private readonly List<string> _tempDirs = new();

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private BulkChangeService CreateService(string? configJson = null)
    {
        var configLoader = new ConfigLoader(null);
        if (configJson != null)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"test_bulk_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            _tempDirs.Add(tempDir);

            // Write minimal config.json (rules come from rule files now)
            var configPath = Path.Combine(tempDir, "config.json");
            File.WriteAllText(configPath, @"{ ""version"": ""1.0"" }");

            // Write rules as a rule file in rules/ subdirectory
            var rulesDir = Path.Combine(tempDir, "rules");
            Directory.CreateDirectory(rulesDir);
            File.WriteAllText(Path.Combine(rulesDir, "test-rules.json"), configJson);

            configLoader = new ConfigLoader(configPath);
        }
        return new BulkChangeService(_logger, configLoader);
    }

    [Fact]
    public void ApplyViaXml_ValidChange_ModifiesAllMembers()
    {
        var xml = TestFixtures.LoadXml("udt-instances-db.xml");
        var db = _parser.Parse(xml);
        var analyzer = new HierarchyAnalyzer();
        var moduleId = db.AllMembers().First(m => m.Name == "ModuleId");
        var analysis = analyzer.Analyze(db, moduleId);
        var scope = analysis.Scopes.Last(); // broadest scope (all 4)

        var service = CreateService();
        var changeSet = new ChangeSet("UdtInstancesDB", "ModuleId", "Int", scope, "99");

        var result = service.ApplyViaXml(xml, changeSet);

        result.IsSuccess.Should().BeTrue();
        result.AffectedCount.Should().Be(4);
        var newDb = _parser.Parse(result.ModifiedXml);
        newDb.AllMembers().Where(m => m.Name == "ModuleId")
            .Should().OnlyContain(m => m.StartValue == "99");
    }

    [Fact]
    public void ApplyViaXml_WithConstraint_ValidValue_Accepted()
    {
        var config = @"{ ""rules"": [{ ""pathPattern"": ""ModuleId"", ""datatype"": ""Int"",
            ""constraints"": { ""min"": 0, ""max"": 100 } }] }";
        var xml = TestFixtures.LoadXml("udt-instances-db.xml");
        var db = _parser.Parse(xml);
        var analyzer = new HierarchyAnalyzer();
        var moduleId = db.AllMembers().First(m => m.Name == "ModuleId");
        var scope = analyzer.Analyze(db, moduleId).Scopes.Last();

        var service = CreateService(config);
        var changeSet = new ChangeSet("UdtInstancesDB", "ModuleId", "Int", scope, "50");

        var result = service.ApplyViaXml(xml, changeSet);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void ApplyViaXml_WithConstraint_InvalidValue_Rejected()
    {
        var config = @"{ ""rules"": [{ ""pathPattern"": ""ModuleId"", ""datatype"": ""Int"",
            ""constraints"": { ""min"": 0, ""max"": 100 } }] }";
        var xml = TestFixtures.LoadXml("udt-instances-db.xml");
        var db = _parser.Parse(xml);
        var analyzer = new HierarchyAnalyzer();
        var moduleId = db.AllMembers().First(m => m.Name == "ModuleId");
        var scope = analyzer.Analyze(db, moduleId).Scopes.Last();

        var service = CreateService(config);
        var changeSet = new ChangeSet("UdtInstancesDB", "ModuleId", "Int", scope, "999");

        var result = service.ApplyViaXml(xml, changeSet);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainSingle();
        result.AffectedCount.Should().Be(0);
    }

    [Fact]
    public void ApplyViaXml_LogsAllChanges()
    {
        var xml = TestFixtures.LoadXml("udt-instances-db.xml");
        var db = _parser.Parse(xml);
        var analyzer = new HierarchyAnalyzer();
        var moduleId = db.AllMembers().First(m => m.Name == "ModuleId");
        var scope = analyzer.Analyze(db, moduleId).Scopes.Last();

        _logger.Clear();
        var service = CreateService();
        var changeSet = new ChangeSet("UdtInstancesDB", "ModuleId", "Int", scope, "77");

        service.ApplyViaXml(xml, changeSet);

        _logger.Entries.Should().HaveCount(4);
        _logger.Entries.Should().OnlyContain(e => e.NewValue == "77");
        _logger.Entries.Should().OnlyContain(e => e.DbName == "UdtInstancesDB");
    }

    [Fact]
    public void ApplyViaXml_NoConstraint_AnythingAccepted()
    {
        var xml = TestFixtures.LoadXml("flat-db.xml");
        var db = _parser.Parse(xml);
        var speed = db.Members.First(m => m.Name == "Speed");
        // Single member → create a minimal scope
        var scope = new ScopeLevel("FlatDB", "", -1, new[] { speed });

        var service = CreateService();
        var changeSet = new ChangeSet("FlatDB", "Speed", "Int", scope, "9999");

        var result = service.ApplyViaXml(xml, changeSet);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void RecommendStrategy_SmallScope_DirectApi()
    {
        var xml = TestFixtures.LoadXml("udt-instances-db.xml");
        var db = _parser.Parse(xml);
        var analyzer = new HierarchyAnalyzer();
        var moduleId = db.AllMembers().First(m => m.Name == "ModuleId");
        var analysis = analyzer.Analyze(db, moduleId);
        // 4 members → under threshold
        var scope = analysis.Scopes.Last();

        var service = CreateService();
        var changeSet = new ChangeSet("Test", "ModuleId", "Int", scope, "1");

        service.RecommendStrategy(changeSet).Should().Be(BulkStrategy.DirectApi);
    }

    [Fact]
    public void RecommendStrategy_LargeScope_XmlExportImport()
    {
        // Create a scope with >10 members
        var members = Enumerable.Range(0, 15)
            .Select(i => new MemberNode($"Var{i}", "Int", "0", $"Var{i}", null, Array.Empty<MemberNode>()))
            .ToList();
        var scope = new ScopeLevel("BigDB", "", -1, members);

        var service = CreateService();
        var changeSet = new ChangeSet("BigDB", "Var0", "Int", scope, "1");

        service.RecommendStrategy(changeSet).Should().Be(BulkStrategy.XmlExportImport);
    }

    [Fact]
    public void Apply_BackwardCompatible_DelegatesToApplyViaXml()
    {
        var xml = TestFixtures.LoadXml("flat-db.xml");
        var db = _parser.Parse(xml);
        var speed = db.Members.First(m => m.Name == "Speed");
        var scope = new ScopeLevel("FlatDB", "", -1, new[] { speed });

        var service = CreateService();
        var changeSet = new ChangeSet("FlatDB", "Speed", "Int", scope, "2000");

        var result = service.Apply(xml, changeSet);

        result.IsSuccess.Should().BeTrue();
        var newDb = _parser.Parse(result.ModifiedXml);
        newDb.Members.First(m => m.Name == "Speed").StartValue.Should().Be("2000");
    }
}
