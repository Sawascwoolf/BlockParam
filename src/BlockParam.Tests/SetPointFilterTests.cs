using FluentAssertions;
using BlockParam.Config;
using BlockParam.Models;
using BlockParam.UI;
using Xunit;

namespace BlockParam.Tests;

public class RuleFilterTests
{
    private static MemberNode MakeLeaf(string name, string datatype, MemberNode? parent = null)
    {
        var path = parent != null ? $"{parent.Path}.{name}" : name;
        return new MemberNode(name, datatype, "0", path, parent, new List<MemberNode>(), false);
    }

    private static MemberNodeViewModel MakeTree()
    {
        var parentChildren = new List<MemberNode>();
        var parent = new MemberNode("commError", "\"messageConfig_UDT\"", null, "commError",
            null, parentChildren, false);

        var moduleId = MakeLeaf("moduleId", "Int", parent);
        var elementId = MakeLeaf("elementId", "Int", parent);
        var actualValue = MakeLeaf("actualValue", "Int", parent);
        parentChildren.Add(moduleId);
        parentChildren.Add(elementId);
        parentChildren.Add(actualValue);

        return new MemberNodeViewModel(parent, null);
    }

    [Fact]
    public void Filter_ExcludeByPath_HiddenWhenFilterActive()
    {
        var vm = MakeTree();
        var exclude = new HashSet<string> { "commError.actualValue" };

        vm.ApplyFilter(ruleFilterActive: true, excludedByRules: exclude);

        vm.Children[0].IsVisible.Should().BeTrue();  // moduleId
        vm.Children[1].IsVisible.Should().BeTrue();  // elementId
        vm.Children[2].IsVisible.Should().BeFalse(); // actualValue excluded
    }

    [Fact]
    public void Filter_ExcludeList_Empty_NoEffect()
    {
        var vm = MakeTree();

        vm.ApplyFilter(ruleFilterActive: true, excludedByRules: null);

        vm.Children[2].IsVisible.Should().BeTrue(); // actualValue visible
    }

    [Fact]
    public void Filter_ExcludeMultipleMembers()
    {
        var vm = MakeTree();
        var exclude = new HashSet<string> { "commError.actualValue", "commError.elementId" };

        vm.ApplyFilter(ruleFilterActive: true, excludedByRules: exclude);

        vm.Children[0].IsVisible.Should().BeTrue();  // moduleId
        vm.Children[1].IsVisible.Should().BeFalse(); // elementId excluded
        vm.Children[2].IsVisible.Should().BeFalse(); // actualValue excluded
    }

    [Fact]
    public void Filter_FilterOff_ExcludeIgnored()
    {
        var vm = MakeTree();
        var exclude = new HashSet<string> { "commError.actualValue" };

        vm.ApplyFilter(ruleFilterActive: false, excludedByRules: exclude);

        vm.Children[2].IsVisible.Should().BeTrue();
    }

    private static MemberNodeViewModel MakeTreeWithSetPoints(
        bool parentIsSetPoint, bool moduleIdSetPoint, bool actualValueSetPoint)
    {
        var parentChildren = new List<MemberNode>();
        var parent = new MemberNode("commError", "\"messageConfig_UDT\"", null, "commError",
            null, parentChildren, parentIsSetPoint);

        parentChildren.Add(new MemberNode("moduleId", "Int", "0", "commError.moduleId",
            parent, new List<MemberNode>(), moduleIdSetPoint));
        parentChildren.Add(new MemberNode("actualValue", "Int", "0", "commError.actualValue",
            parent, new List<MemberNode>(), actualValueSetPoint));

        return new MemberNodeViewModel(parent, null);
    }

    [Fact]
    public void ShowSetpointsOnly_hides_leaves_with_effective_setpoint_false()
    {
        var vm = MakeTreeWithSetPoints(parentIsSetPoint: true, moduleIdSetPoint: true, actualValueSetPoint: false);
        vm.ApplyFilter(ruleFilterActive: false, showSetpointsOnly: true);

        vm.Children[0].IsVisible.Should().BeTrue();   // moduleId: true & parent true
        vm.Children[1].IsVisible.Should().BeFalse();  // actualValue: false
        vm.IsVisible.Should().BeTrue();               // container has visible child
    }

    [Fact]
    public void ShowSetpointsOnly_hides_all_when_parent_setpoint_is_false()
    {
        // Even leaves marked as SetPoint in the UDT type must be hidden when the
        // UDT instance itself is disabled (parent.IsSetPoint = false).
        var vm = MakeTreeWithSetPoints(parentIsSetPoint: false, moduleIdSetPoint: true, actualValueSetPoint: true);
        vm.ApplyFilter(ruleFilterActive: false, showSetpointsOnly: true);

        vm.Children[0].IsVisible.Should().BeFalse();  // moduleId blocked by parent
        vm.Children[1].IsVisible.Should().BeFalse();  // actualValue blocked by parent
        vm.IsVisible.Should().BeFalse();              // container collapses with no visible kids
    }

    [Fact]
    public void Rule_and_SetPoint_filters_AND_combine()
    {
        var vm = MakeTreeWithSetPoints(parentIsSetPoint: true, moduleIdSetPoint: true, actualValueSetPoint: true);
        var exclude = new HashSet<string> { "commError.moduleId" };

        vm.ApplyFilter(ruleFilterActive: true, excludedByRules: exclude, showSetpointsOnly: true);

        // moduleId: passes setpoint, fails rule → hidden
        vm.Children[0].IsVisible.Should().BeFalse();
        // actualValue: passes both → visible
        vm.Children[1].IsVisible.Should().BeTrue();
    }

    [Fact]
    public void Struct_ancestor_is_transparent_in_setpoint_chain()
    {
        // TP307 pattern: drive2 (Struct, no SetPoint checkbox in TIA) contains
        // blocked (UDT instance, SetPoint=true) whose moduleId (Int, SetPoint=true
        // in the UDT type) should remain visible with the filter on.
        var drive2Children = new List<MemberNode>();
        var drive2 = new MemberNode("drive2", "Struct", null, "drive2",
            null, drive2Children, isSetPoint: false); // Struct default — meaningless

        var blockedChildren = new List<MemberNode>();
        var blocked = new MemberNode("blocked", "\"messageConfig_UDT\"", null, "drive2.blocked",
            drive2, blockedChildren, isSetPoint: true);
        drive2Children.Add(blocked);

        blockedChildren.Add(new MemberNode("moduleId", "Int", "0", "drive2.blocked.moduleId",
            blocked, new List<MemberNode>(), isSetPoint: true));

        var rootVm = new MemberNodeViewModel(drive2, null);
        rootVm.ApplyFilter(ruleFilterActive: false, showSetpointsOnly: true);

        var moduleIdVm = rootVm.Children[0].Children[0];
        moduleIdVm.EffectiveSetPoint.Should().BeTrue(
            "Struct ancestor must not block the SetPoint chain");
        moduleIdVm.IsVisible.Should().BeTrue();
    }

    [Fact]
    public void ShowSetpointsOnly_off_preserves_legacy_visibility()
    {
        var vm = MakeTreeWithSetPoints(parentIsSetPoint: false, moduleIdSetPoint: false, actualValueSetPoint: false);
        vm.ApplyFilter(ruleFilterActive: false, showSetpointsOnly: false);

        vm.Children[0].IsVisible.Should().BeTrue();
        vm.Children[1].IsVisible.Should().BeTrue();
    }

    [Fact]
    public void RuleWithExclude_Deserializes()
    {
        var json = @"{
            ""version"": ""1.0"",
            ""rules"": [{
                ""pathPattern"": "".*\\.actualValue$"",
                ""excludeFromSetpoints"": true
            }]
        }";
        var config = ConfigLoader.Deserialize(json);

        config.Should().NotBeNull();
        config!.Rules.Should().HaveCount(1);
        config.Rules[0].ExcludeFromSetpoints.Should().BeTrue();
        config.Rules[0].PathPattern.Should().Be(@".*\.actualValue$");
    }
}
