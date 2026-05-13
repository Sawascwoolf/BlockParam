using System.Linq;
using BlockParam.Config;
using BlockParam.Models;
using BlockParam.Services;
using BlockParam.UI;
using FluentAssertions;
using Xunit;

namespace BlockParam.Tests;

/// <summary>
/// Focused tests for the pending-edits slice (#80 slice 4).
/// </summary>
public class PendingEditsViewModelTests
{
    [Fact]
    public void Defaults_AreEmpty()
    {
        var vm = new PendingEditsViewModel(() => 0);

        vm.PendingEdits.Should().BeEmpty();
        vm.ExistingIssues.Should().BeEmpty();
        vm.HasPendingEdits.Should().BeFalse();
        vm.HasExistingIssues.Should().BeFalse();
        vm.PendingInlineEditCount.Should().Be(0);
        vm.PendingStatusText.Should().BeNull();
        vm.InvalidPendingCount.Should().Be(0);
        vm.HasInvalidPending.Should().BeFalse();
        vm.InvalidPendingBadge.Should().BeEmpty();
    }

    [Fact]
    public void PendingInlineEditCount_DelegatesToCallback()
    {
        int count = 7;
        var vm = new PendingEditsViewModel(() => count);

        vm.PendingInlineEditCount.Should().Be(7);

        count = 3;
        vm.PendingInlineEditCount.Should().Be(3);
    }

    [Fact]
    public void PendingStatusText_FormatsWithCount()
    {
        int count = 0;
        var vm = new PendingEditsViewModel(() => count);

        count = 1;
        vm.PendingStatusText.Should().Be("1 pending inline edit");

        count = 5;
        vm.PendingStatusText.Should().Be("5 pending inline edits");
    }

    [Fact]
    public void Rebuild_CollectsPendingNodesFromTree()
    {
        var root = BuildTree();
        // Stage two pending edits.
        var speed = FindLeaf(root, "Speed");
        var pressure = FindLeaf(root, "Pressure");
        speed.EditableStartValue = "999";
        pressure.EditableStartValue = "42";

        var vm = new PendingEditsViewModel(() => 2);
        vm.Rebuild(new[] { root }, bulkPaths: null);

        vm.PendingEdits.Should().HaveCount(2);
        vm.HasPendingEdits.Should().BeTrue();
        vm.PendingEdits.Select(e => e.Node.Path).Should().Contain(speed.Path, pressure.Path);
    }

    [Fact]
    public void Rebuild_MarksOverwrittenByBulkWhenPathInBulkSet()
    {
        var root = BuildTree();
        var speed = FindLeaf(root, "Speed");
        speed.EditableStartValue = "999";

        var vm = new PendingEditsViewModel(() => 1);
        vm.Rebuild(new[] { root }, bulkPaths: new System.Collections.Generic.HashSet<string> { speed.Path });

        vm.PendingEdits.Should().ContainSingle()
            .Which.WillBeOverwrittenByBulk.Should().BeTrue();
    }

    [Fact]
    public void Rebuild_ClearsBeforeRepopulating()
    {
        var root = BuildTree();
        var speed = FindLeaf(root, "Speed");
        speed.EditableStartValue = "999";

        var vm = new PendingEditsViewModel(() => 1);
        vm.Rebuild(new[] { root }, null);
        vm.PendingEdits.Should().HaveCount(1);

        // Drop the pending edit and rebuild — collection must reflect it.
        speed.ClearPending();
        vm.Rebuild(new[] { root }, null);

        vm.PendingEdits.Should().BeEmpty();
        vm.HasPendingEdits.Should().BeFalse();
    }

    [Fact]
    public void RebuildExistingIssues_FlagsLeavesViolatingValidator()
    {
        var root = BuildTreeForValidation();
        var validator = BuildValidatorWithSpeedMax();

        var vm = new PendingEditsViewModel(() => 0);
        vm.RebuildExistingIssues(new[] { root }, validator);

        vm.ExistingIssues.Should().HaveCount(1);
        vm.HasExistingIssues.Should().BeTrue();
        vm.ExistingIssuesCount.Should().Be(1);
        vm.ExistingIssues.Single().Node.Name.Should().Be("Speed");
        vm.ExistingIssues.Single().Node.HasExistingViolation.Should().BeTrue();
    }

    [Fact]
    public void RebuildExistingIssues_ClearsPriorViolationsWhenValueNowValid()
    {
        var root = BuildTreeForValidation();
        var leaf = FindLeaf(root, "Speed");

        // Initial scan with strict validator: Speed=1500 violates max=1000.
        var strict = BuildValidatorWithSpeedMax();
        var vm = new PendingEditsViewModel(() => 0);
        vm.RebuildExistingIssues(new[] { root }, strict);
        leaf.HasExistingViolation.Should().BeTrue();

        // Permissive validator: no rule → no violation.
        var lenient = new MemberValidator(null, null);
        vm.RebuildExistingIssues(new[] { root }, lenient);

        leaf.HasExistingViolation.Should().BeFalse();
        vm.ExistingIssues.Should().BeEmpty();
    }

    [Fact]
    public void RaisePendingCountChanged_NotifiesCountAndStatus()
    {
        int count = 0;
        var vm = new PendingEditsViewModel(() => count);
        var raised = new System.Collections.Generic.List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        count = 4;
        vm.RaisePendingCountChanged();

        raised.Should().Contain(nameof(PendingEditsViewModel.PendingInlineEditCount));
        raised.Should().Contain(nameof(PendingEditsViewModel.PendingStatusText));
    }

    [Fact]
    public void RaiseInvalidPendingChanged_NotifiesBadgeAndCount()
    {
        var vm = new PendingEditsViewModel(() => 0);
        var raised = new System.Collections.Generic.List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.RaiseInvalidPendingChanged();

        raised.Should().Contain(nameof(PendingEditsViewModel.InvalidPendingCount));
        raised.Should().Contain(nameof(PendingEditsViewModel.HasInvalidPending));
        raised.Should().Contain(nameof(PendingEditsViewModel.InvalidPendingBadge));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Fixtures
    // ─────────────────────────────────────────────────────────────────────

    private static MemberNodeViewModel BuildTree()
    {
        var speed = new MemberNode("Speed", "Int", "100", "Root.Speed", null, Array.Empty<MemberNode>());
        var pressure = new MemberNode("Pressure", "Int", "10", "Root.Pressure", null, Array.Empty<MemberNode>());
        var root = new MemberNode("Root", "Struct", null, "Root", null, new[] { speed, pressure });
        return new MemberNodeViewModel(root, null);
    }

    private static MemberNodeViewModel BuildTreeForValidation()
    {
        // Speed exceeds the rule's max (see BuildValidatorWithSpeedMax).
        var speed = new MemberNode("Speed", "Int", "1500", "Root.Speed", null, Array.Empty<MemberNode>());
        var ok = new MemberNode("Within", "Int", "50", "Root.Within", null, Array.Empty<MemberNode>());
        var root = new MemberNode("Root", "Struct", null, "Root", null, new[] { speed, ok });
        return new MemberNodeViewModel(root, null);
    }

    private static MemberValidator BuildValidatorWithSpeedMax()
    {
        // Build a config with a rule capping Speed to 1000.
        var config = new BulkChangeConfig
        {
            Rules = new System.Collections.Generic.List<MemberRule>
            {
                new MemberRule
                {
                    PathPattern = @".*\.Speed$",
                    Constraints = new ValueConstraint { Min = 0, Max = 1000 },
                }
            }
        };
        return new MemberValidator(config, null);
    }

    private static MemberNodeViewModel FindLeaf(MemberNodeViewModel node, string name) =>
        TryFindLeaf(node, name)
            ?? throw new InvalidOperationException($"Leaf '{name}' not found");

    private static MemberNodeViewModel? TryFindLeaf(MemberNodeViewModel node, string name)
    {
        if (node.Name == name && node.IsLeaf) return node;
        foreach (var c in node.Children)
        {
            var found = TryFindLeaf(c, name);
            if (found != null) return found;
        }
        return null;
    }
}
