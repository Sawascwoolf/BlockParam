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
        vm.Rebuild(new[] { root }, bulkNodes: null);

        vm.PendingEdits.Should().HaveCount(2);
        vm.HasPendingEdits.Should().BeTrue();
        vm.PendingEdits.Select(e => e.Node.Path).Should().Contain(speed.Path, pressure.Path);
    }

    [Fact]
    public void Rebuild_MarksOverwrittenByBulkWhenNodeInBulkSet()
    {
        // The bulk-overwrite flag is keyed by VM reference, not path string,
        // so two DBs sharing a path never alias (#82 / #121).
        var root = BuildTree();
        var speed = FindLeaf(root, "Speed");
        speed.EditableStartValue = "999";

        var vm = new PendingEditsViewModel(() => 1);
        vm.Rebuild(new[] { root },
            bulkNodes: new System.Collections.Generic.HashSet<MemberNodeViewModel> { speed });

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
    // #145 — owning-DB label on each pending row in multi-DB sessions
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Rebuild_MultiDb_SharedPath_ResolvesDistinctDbLabelsPerRow()
    {
        // Two DBs on different PLCs, BOTH exposing the same member path
        // "Config.Speed". Before #145 the two pending-edit rows rendered
        // identically; now each carries its owning-DB label resolved via
        // the shared ActiveDbDisplayName formatter (no path aliasing, #82).
        var (dbA, speedA) = MakeDbWithConfigSpeed("DB_Drive", "PLC_1");
        var (dbB, speedB) = MakeDbWithConfigSpeed("DB_Pump", "PLC_2");
        var tree = BuildTreeVm(dbA, dbB);

        // Stage an inline edit on Config.Speed in each DB.
        SpeedVm(tree, speedA).EditableStartValue = "111";
        SpeedVm(tree, speedB).EditableStartValue = "222";

        var resolver = ActiveDbDisplayName.ResolverFor(
            tree, new[] { dbA, dbB }, anchorPlcFallback: "PLC_1");

        var vm = new PendingEditsViewModel(() => 2);
        vm.Rebuild(tree.RootMembers, bulkNodes: null, dbLabelResolver: resolver);

        vm.PendingEdits.Should().HaveCount(2);
        var labels = vm.PendingEdits.Select(e => e.DbLabel).ToList();
        labels.Should().OnlyHaveUniqueItems("each row must identify its owning DB");
        labels.Should().BeEquivalentTo(new[] { "DB_Drive", "DB_Pump" });
        vm.PendingEdits.Should().OnlyContain(e => e.HasDbLabel);
        vm.PendingEdits.Select(e => e.DbLabelDisplay)
            .Should().BeEquivalentTo(new[] { "DB: DB_Drive", "DB: DB_Pump" });
    }

    [Fact]
    public void Rebuild_MultiDb_NameCollision_PrefixesPlcMatchingTreeHeader()
    {
        // Same DB name on two PLCs → the shared formatter qualifies BOTH
        // with their PLC, EXACTLY as the tree's synthetic group header does.
        var (dbA, speedA) = MakeDbWithConfigSpeed("DB_Foo", "PLC_A");
        var (dbB, speedB) = MakeDbWithConfigSpeed("DB_Foo", "PLC_B");
        var tree = BuildTreeVm(dbA, dbB);

        SpeedVm(tree, speedA).EditableStartValue = "1";
        SpeedVm(tree, speedB).EditableStartValue = "2";

        var resolver = ActiveDbDisplayName.ResolverFor(
            tree, new[] { dbA, dbB }, anchorPlcFallback: "PLC_A");

        var vm = new PendingEditsViewModel(() => 2);
        vm.Rebuild(tree.RootMembers, null, resolver);

        vm.PendingEdits.Select(e => e.DbLabel)
            .Should().BeEquivalentTo(new[] { "PLC_A / DB_Foo", "PLC_B / DB_Foo" });

        // Must match the tree's synthetic group-root header verbatim.
        var headerLabels = tree.RootMembers.Select(r => r.Name).ToList();
        vm.PendingEdits.Select(e => e.DbLabel)
            .Should().BeSubsetOf(headerLabels);
    }

    [Fact]
    public void Rebuild_SingleDb_NoDbLabelRendered()
    {
        // One active DB → the resolver returns "" so single-DB rows stay
        // unlabeled (the recommended #145 conditional-display design).
        var (db, speed) = MakeDbWithConfigSpeed("DB_Solo", "PLC_1");
        var tree = BuildTreeVm(db);
        SpeedVm(tree, speed).EditableStartValue = "999";

        var resolver = ActiveDbDisplayName.ResolverFor(
            tree, new[] { db }, anchorPlcFallback: "PLC_1");

        var vm = new PendingEditsViewModel(() => 1);
        vm.Rebuild(tree.RootMembers, null, resolver);

        vm.PendingEdits.Should().ContainSingle();
        vm.PendingEdits[0].DbLabel.Should().BeEmpty();
        vm.PendingEdits[0].HasDbLabel.Should().BeFalse();
        vm.PendingEdits[0].DbLabelDisplay.Should().BeEmpty();
    }

    [Fact]
    public void Rebuild_NullResolver_LeavesDbLabelEmpty()
    {
        // Back-compat: callers that don't pass a resolver (existing tests /
        // legacy paths) get the prior behavior — no label, no throw.
        var root = BuildTree();
        FindLeaf(root, "Speed").EditableStartValue = "5";

        var vm = new PendingEditsViewModel(() => 1);
        vm.Rebuild(new[] { root }, bulkNodes: null);

        vm.PendingEdits.Should().ContainSingle()
            .Which.DbLabel.Should().BeEmpty();
    }

    private static (ActiveDb db, MemberNode speed) MakeDbWithConfigSpeed(
        string dbName, string plcName)
    {
        var speed = new MemberNode("Speed", "Int", "0", "Config.Speed", null,
            Array.Empty<MemberNode>());
        var config = new MemberNode("Config", "Struct", null, "Config", null,
            new[] { speed });
        var info = new DataBlockInfo(dbName, 1, "Optimized", "GlobalDB",
            new[] { config });
        return (new ActiveDb(info, $"<Block name='{dbName}' />",
            onApply: null, plcName: plcName), speed);
    }

    private static MemberTreeViewModel BuildTreeVm(params ActiveDb[] dbs)
    {
        var tree = new MemberTreeViewModel(
            getActiveDbs: () => dbs,
            getCurrentPlcName: () => "",
            commentLanguagePolicy: new CommentLanguagePolicy(null, null, new[] { "en-GB" }),
            subscribeToVm: _ => { });
        tree.BuildRootMembersFromActiveDbs();
        return tree;
    }

    private static MemberNodeViewModel SpeedVm(MemberTreeViewModel tree, MemberNode speedModel) =>
        tree.FindVmByModel(speedModel)
            ?? throw new InvalidOperationException("Speed VM not found in tree");

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
