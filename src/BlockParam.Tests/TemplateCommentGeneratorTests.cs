using FluentAssertions;
using NSubstitute;
using BlockParam.Config;
using BlockParam.Models;
using BlockParam.Services;
using Xunit;

namespace BlockParam.Tests;

public class TemplateCommentGeneratorTests
{
    private static MemberNode MakeUdtInstance(string name, string parentName,
        params (string Name, string Value)[] children)
    {
        var childList = new List<MemberNode>();
        var parent = new MemberNode(parentName, "Struct", null, parentName,
            null, new List<MemberNode>(), false);
        var instance = new MemberNode(name, "\"messageConfig_UDT\"", null,
            $"{parentName}.{name}", parent, childList, true);

        foreach (var (cName, cValue) in children)
        {
            var child = new MemberNode(cName, "Int", cValue,
                $"{parentName}.{name}.{cName}", instance, new List<MemberNode>(), true);
            childList.Add(child);
        }

        return instance;
    }

    private static DataBlockInfo MakeDb(string name, params MemberNode[] topMembers)
    {
        return new DataBlockInfo(name, 1, "Optimized", "GlobalDB", topMembers.ToList());
    }

    private static TagTableCache CreateCache()
    {
        var reader = Substitute.For<ITagTableReader>();
        reader.GetTagTableNames().Returns(new[] { "MOD_Constants", "ELE_Constants", "MES_Constants" });
        reader.ReadTagTable("MOD_Constants").Returns(new[]
        {
            new TagTableEntry("MOD_FOERDERER_1", "5", "Int", "Förderer Halle 1"),
        });
        reader.ReadTagTable("ELE_Constants").Returns(new[]
        {
            new TagTableEntry("ELE_DRIVE", "1", "Int", "Antrieb"),
        });
        reader.ReadTagTable("MES_Constants").Returns(new[]
        {
            new TagTableEntry("MES_COMM_ERROR", "101", "Int", "Kommunikationsfehler"),
        });
        return new TagTableCache(reader);
    }

    private static BulkChangeConfig CreateConfig()
    {
        return new BulkChangeConfig
        {
            Rules = new List<MemberRule>
            {
                new() { PathPattern = @".*\.moduleId$", TagTableReference = new TagTableReference { TableName = "MOD_*" } },
                new() { PathPattern = @".*\.elementId$", TagTableReference = new TagTableReference { TableName = "ELE_*" } },
                new() { PathPattern = @".*\.messageId$", TagTableReference = new TagTableReference { TableName = "MES_*" } },
            }
        };
    }

    [Fact]
    public void Template_SimpleDbParent()
    {
        var inst = MakeUdtInstance("communicationError", "drive1");
        var db = MakeDb("TP307");
        var gen = new TemplateCommentGenerator(new BulkChangeConfig());

        var result = gen.Generate(db, inst, "{db}.{parent}");

        result.Should().Be("TP307.drive1");
    }

    [Fact]
    public void Template_MemberValue()
    {
        var inst = MakeUdtInstance("comm", "drive1",
            ("moduleId", "5"), ("elementId", "1"));
        var db = MakeDb("TP307");
        var gen = new TemplateCommentGenerator(new BulkChangeConfig());

        var result = gen.Generate(db, inst, "{moduleId}");

        result.Should().Be("5");
    }

    [Fact]
    public void Template_MemberComment_FromTagTable()
    {
        var inst = MakeUdtInstance("comm", "drive1",
            ("moduleId", "5"), ("elementId", "1"));
        var db = MakeDb("TP307");
        var gen = new TemplateCommentGenerator(CreateConfig(), CreateCache());

        var result = gen.Generate(db, inst, "{moduleId.comment}");

        result.Should().Be("Förderer Halle 1");
    }

    [Fact]
    public void Template_MemberName_FromTagTable()
    {
        var inst = MakeUdtInstance("comm", "drive1",
            ("moduleId", "5"));
        var db = MakeDb("TP307");
        var gen = new TemplateCommentGenerator(CreateConfig(), CreateCache());

        var result = gen.Generate(db, inst, "{moduleId.name}");

        result.Should().Be("MOD_FOERDERER_1");
    }

    [Fact]
    public void Template_FullTP307Example()
    {
        var inst = MakeUdtInstance("communicationError", "drive1",
            ("moduleId", "5"), ("elementId", "1"), ("messageId", "101"));
        var db = MakeDb("TP307");
        var gen = new TemplateCommentGenerator(CreateConfig(), CreateCache());

        var template = "{db}.{parent} ({moduleId}, {elementId}, {messageId}) : {moduleId.comment} - {elementId.comment} - {messageId.comment}";
        var result = gen.Generate(db, inst, template);

        result.Should().Be("TP307.drive1 (5, 1, 101) : Förderer Halle 1 - Antrieb - Kommunikationsfehler");
    }

    [Fact]
    public void Template_MissingChild_EmptyString()
    {
        var inst = MakeUdtInstance("comm", "drive1", ("moduleId", "5"));
        var db = MakeDb("TP307");
        var gen = new TemplateCommentGenerator(new BulkChangeConfig());

        var result = gen.Generate(db, inst, "{bmkId}");

        result.Should().Be("");
    }

    [Fact]
    public void Template_MissingTagEntry_FallbackToValue()
    {
        var inst = MakeUdtInstance("comm", "drive1", ("moduleId", "999"));
        var db = MakeDb("TP307");
        var gen = new TemplateCommentGenerator(CreateConfig(), CreateCache());

        var result = gen.Generate(db, inst, "{moduleId.comment}");

        result.Should().Be("999"); // Value 999 not in tag table → fallback
    }

    [Fact]
    public void GenerateForScope_MatchingRule_UsesRuleTemplate()
    {
        var inst = MakeUdtInstance("comm", "drive1",
            ("moduleId", "5"), ("elementId", "1"));
        var db = MakeDb("TP307");
        var leaf = inst.Children[0]; // moduleId leaf

        var config = new BulkChangeConfig
        {
            Rules = new List<MemberRule>
            {
                new() { PathPattern = @".*{udt:messageConfig_UDT}$",
                        CommentTemplate = "{db}.{self}" }
            }
        };
        var gen = new TemplateCommentGenerator(config);

        var results = gen.GenerateForScope(db, new[] { leaf });

        results.Should().HaveCount(1);
        results[0].Comment.Should().Be("TP307.comm");
    }

    [Fact]
    public void GenerateForScope_NoMatchingRule_ReturnsEmpty()
    {
        var inst = MakeUdtInstance("comm", "drive1", ("moduleId", "5"));
        var db = MakeDb("TP307");
        var leaf = inst.Children[0];

        // Rule has no commentTemplate
        var config = new BulkChangeConfig
        {
            Rules = new List<MemberRule>
            {
                new() { PathPattern = @".*\.moduleId$" }
            }
        };
        var gen = new TemplateCommentGenerator(config);

        var results = gen.GenerateForScope(db, new[] { leaf });

        results.Should().BeEmpty();
    }

    [Fact]
    public void GenerateForScope_DifferentUDTs_DifferentTemplates()
    {
        // Two different UDT types with different templates
        var parent1 = new MemberNode("comm1", "\"messageConfig_UDT\"", null, "comm1",
            null, new List<MemberNode>(), true);
        var child1 = new MemberNode("moduleId", "Int", "5", "comm1.moduleId",
            parent1, new List<MemberNode>(), true);
        ((List<MemberNode>)parent1.Children).Add(child1);

        var parent2 = new MemberNode("alarm1", "\"alarmConfig_UDT\"", null, "alarm1",
            null, new List<MemberNode>(), true);
        var child2 = new MemberNode("alarmId", "Int", "10", "alarm1.alarmId",
            parent2, new List<MemberNode>(), true);
        ((List<MemberNode>)parent2.Children).Add(child2);

        var config = new BulkChangeConfig
        {
            Rules = new List<MemberRule>
            {
                new() { PathPattern = @".*{udt:messageConfig_UDT}$",
                        CommentTemplate = "MSG: {self}" },
                new() { PathPattern = @".*{udt:alarmConfig_UDT}$",
                        CommentTemplate = "ALM: {self}" }
            }
        };
        var gen = new TemplateCommentGenerator(config);
        var db = MakeDb("TestDB");

        var results = gen.GenerateForScope(db, new[] { child1, child2 });

        results.Should().HaveCount(2);
        results.Should().Contain(r => r.Comment == "MSG: comm1");
        results.Should().Contain(r => r.Comment == "ALM: alarm1");
    }

    [Fact]
    public void GenerateForScope_RuleWithoutTemplate_Ignored()
    {
        var inst = MakeUdtInstance("comm", "drive1", ("moduleId", "5"));
        var db = MakeDb("TP307");
        var leaf = inst.Children[0];

        var config = new BulkChangeConfig
        {
            Rules = new List<MemberRule>
            {
                // Rule WITHOUT commentTemplate — should be ignored by GenerateForScope
                new() { PathPattern = @".*{udt:messageConfig_UDT}$" },
                // Rule WITH commentTemplate but wrong type — no match
                new() { PathPattern = @".*{udt:otherType}$",
                        CommentTemplate = "WRONG" }
            }
        };
        var gen = new TemplateCommentGenerator(config);

        var results = gen.GenerateForScope(db, new[] { leaf });

        results.Should().BeEmpty();
    }

    [Fact]
    public void GenerateForScope_DeduplicatesUdtInstances()
    {
        // 4 leaf members sharing 2 UDT parents — both with matching comment rules
        var childList1 = new List<MemberNode>();
        var parent1 = new MemberNode("comm1", "\"msgCfg\"", null, "comm1",
            null, childList1, true);
        var m1 = new MemberNode("moduleId", "Int", "1", "comm1.moduleId", parent1, new List<MemberNode>(), true);
        var e1 = new MemberNode("elementId", "Int", "2", "comm1.elementId", parent1, new List<MemberNode>(), true);
        childList1.Add(m1);
        childList1.Add(e1);

        var childList2 = new List<MemberNode>();
        var parent2 = new MemberNode("comm2", "\"msgCfg\"", null, "comm2",
            null, childList2, true);
        var m2 = new MemberNode("moduleId", "Int", "3", "comm2.moduleId", parent2, new List<MemberNode>(), true);
        var e2 = new MemberNode("elementId", "Int", "4", "comm2.elementId", parent2, new List<MemberNode>(), true);
        childList2.Add(m2);
        childList2.Add(e2);

        var scope = new List<MemberNode> { m1, e1, m2, e2 };
        var db = MakeDb("TestDB");

        var config = new BulkChangeConfig
        {
            Rules = new List<MemberRule>
            {
                new() { PathPattern = @".*{udt:msgCfg}$",
                        CommentTemplate = "{self}" }
            }
        };
        var gen = new TemplateCommentGenerator(config);

        var results = gen.GenerateForScope(db, scope);

        results.Should().HaveCount(2); // 2 unique parents, not 4 leaves
        results.Should().Contain(r => r.Comment == "comm1");
        results.Should().Contain(r => r.Comment == "comm2");
    }
}
