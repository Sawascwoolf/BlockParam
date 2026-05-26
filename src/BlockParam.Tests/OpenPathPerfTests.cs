using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using BlockParam.Models;
using BlockParam.SimaticML;
using BlockParam.UI;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace BlockParam.Tests;

/// <summary>
/// #154 quantified before/after for the DB-open path, analogous to the #159
/// benchmark (PR #161). Unlike #159 — where the pre-fix code path was a
/// retained overload — #154's old algorithms were replaced in place, so the
/// "before" for H1/H4 is a FAITHFUL in-test reproduction of the removed code
/// run against identical inputs, while the "after" is the real product code.
/// H3 needs no reproduction: "before" is stock <see cref="ObservableCollection{T}"/>
/// and the metric is an exact, deterministic CollectionChanged count (the
/// fan-out #154 H3 is about), not a flaky wall-time.
/// Each test prints its numbers to the CI test output and asserts a
/// conservative floor so a revert trips it without runner-jitter flake.
/// </summary>
public class OpenPathPerfTests
{
    private readonly ITestOutputHelper _out;
    private readonly SimaticMLParser _parser = new();

    public OpenPathPerfTests(ITestOutputHelper output) => _out = output;

    // ───────────────────────── H1: parser subelement read ─────────────────

    /// <summary>
    /// #154 H1. Isolates the fixed dimension on identical input: "before" is
    /// the removed pattern (linear &lt;Subelement&gt; scan per element, O(n²)),
    /// "after" is the new strategy (index the children once, then O(1) lookups
    /// — exactly what <see cref="SimaticMLParser"/> now does internally). Both
    /// are reproduced so the comparison isolates the algorithm rather than
    /// being muddied by the rest of Parse; correctness is tied back to the
    /// real product by asserting both reproductions equal the values
    /// <see cref="SimaticMLParser.Parse"/> actually produced.
    /// </summary>
    [Fact]
    public void H1_ParserSubelementRead_BeforeAfter()
    {
        const int n = 10_000;
        var xml = BuildLargeArrayDbXml(n);

        var doc = XDocument.Parse(xml);
        var arrEl = doc.Descendants()
            .First(e => e.Name.LocalName == "Member"
                        && e.Attribute("Name")?.Value == "BigArray");

        // before: ReadSubelementValue-per-element linear scan (O(n²)).
        var swOld = Stopwatch.StartNew();
        var oldValues = new string?[n];
        for (int i = 1; i <= n; i++)
        {
            string? val = null;
            foreach (var sub in arrEl.Elements().Where(e => e.Name.LocalName == "Subelement"))
            {
                if (sub.Attribute("Path")?.Value != i.ToString()) continue;
                val = sub.Elements().FirstOrDefault(e => e.Name.LocalName == "StartValue")?.Value;
                break;
            }
            oldValues[i - 1] = val;
        }
        swOld.Stop();

        // after: index the Subelements once, then O(1) per element (O(n)).
        var swNew = Stopwatch.StartNew();
        var index = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var sub in arrEl.Elements().Where(e => e.Name.LocalName == "Subelement"))
        {
            var p = sub.Attribute("Path")?.Value;
            if (p != null && !index.ContainsKey(p)) index[p] = sub;
        }
        var newValues = new string?[n];
        for (int i = 1; i <= n; i++)
            newValues[i - 1] = index.TryGetValue(i.ToString(), out var s)
                ? s.Elements().FirstOrDefault(e => e.Name.LocalName == "StartValue")?.Value
                : null;
        swNew.Stop();

        // Equivalence + tie to real product code: both reproductions must
        // agree with each other AND with SimaticMLParser.Parse's output.
        var arr = _parser.Parse(xml).Members.First(m => m.Name == "BigArray");
        arr.Children.Should().HaveCount(n);
        for (int i = 0; i < n; i++)
        {
            newValues[i].Should().Be(oldValues[i]);
            arr.Children[i].StartValue.Should().Be(oldValues[i]);
        }

        var oldMs = swOld.Elapsed.TotalMilliseconds;
        var newMs = swNew.Elapsed.TotalMilliseconds;
        _out.WriteLine(
            $"[#154 H1] N={n}  before/per-element-scan={oldMs:F0} ms  "
            + $"after/indexed={newMs:F1} ms  "
            + $"speedup≈{oldMs / System.Math.Max(newMs, 0.001):F1}x");

        newMs.Should().BeLessThan(oldMs);
        (oldMs / System.Math.Max(newMs, 0.001)).Should().BeGreaterThan(5,
            "#154 H1 turns an O(n²) per-element scan into an O(n) indexed read "
            + "— at N=10000 the gap is enormous; a <5x result means the index "
            + "regressed");
    }

    // ───────────────────────── H3: CollectionChanged fan-out ──────────────

    /// <summary>
    /// #154 H3. Exact, deterministic before/after — no timing. The old flat
    /// list rebuilt with Clear() + N×Add (N+1 CollectionChanged events through
    /// WPF's binding engine on every open/filter/keystroke/edit);
    /// <see cref="BulkObservableCollection{T}.ReplaceAll"/> raises exactly 1.
    /// </summary>
    [Fact]
    public void H3_FlatListNotifications_BeforeAfter()
    {
        const int n = 5000;
        var items = Enumerable.Range(0, n).ToArray();

        // before: stock ObservableCollection, the exact old Clear()+N×Add.
        var old = new ObservableCollection<int>(Enumerable.Range(-3, 3));
        int oldEvents = 0;
        old.CollectionChanged += (_, __) => oldEvents++;
        old.Clear();
        foreach (var x in items) old.Add(x);

        // after: BulkObservableCollection.ReplaceAll — one Reset.
        var @new = new BulkObservableCollection<int>();
        foreach (var s in Enumerable.Range(-3, 3)) @new.Add(s);
        int newEvents = 0;
        NotifyCollectionChangedAction lastAction = default;
        @new.CollectionChanged += (_, e) => { newEvents++; lastAction = e.Action; };
        @new.ReplaceAll(items);

        @new.Should().Equal(items);
        _out.WriteLine(
            $"[#154 H3] N={n}  before/CollectionChanged={oldEvents} events  "
            + $"after/CollectionChanged={newEvents} event  reduction={oldEvents}→{newEvents}");

        oldEvents.Should().Be(n + 1, "Clear() + N×Add fires N+1 events");
        newEvents.Should().Be(1, "ReplaceAll must collapse to a single notification");
        lastAction.Should().Be(NotifyCollectionChangedAction.Reset);
    }

    // ───────────────────────── H4: smart-expand flat build ────────────────

    /// <summary>
    /// #154 H4. "Before": the removed recursive HasHighlightedDescendant
    /// called per visible node under a smart-expanded parent — quadratic in
    /// the depth of every visible chain because HHD re-scans the subtree
    /// from each level. "After": the real <see cref="FlatTreeManager.Refresh"/>,
    /// which fills the highlight cache in one O(n) post-order pass.
    ///
    /// #179: the prior workload (single flat array, one mid-array highlight)
    /// did not exercise the quadratic path — leaves have no children, so HHD
    /// returns in O(1) and the new code's unconditional O(n) cache fill ended
    /// up doing more total work than the old short-circuiting walk, flipping
    /// the assertion on Windows runners with shifted constants. The workload
    /// below replaces it with 50 "sticks" of depth 100 (single-child chains)
    /// where the only highlight sits at each stick's bottom leaf: HHD at level
    /// k must walk (depth-k-1) descendants down the chain before finding it,
    /// summing to depth*(depth-1)/2 ≈ 5,000 ops per stick × 50 sticks ≈ 247k
    /// ops for old vs. ~10k for new (2× walk over 5,001 nodes). That gives a
    /// stable 20×+ ratio and matches the regression #154 H4 actually defends.
    /// </summary>
    [Fact]
    public void H4_SmartExpandFlatBuild_BeforeAfter()
    {
        const int sticks = 50;
        const int depth = 100;
        // depth must be ≥ 2: with depth==1 the wrapper loop body never runs,
        // each "stick" collapses to a bare leaf, and HHD returns in O(1) per
        // node — the degenerate #179 shape this test was rewritten to avoid.
        System.Diagnostics.Debug.Assert(depth >= 2,
            "H4 needs nested chains; depth<2 re-creates the #179 shape");
        var stickRoots = new MemberNode[sticks];
        for (int s = 0; s < sticks; s++)
        {
            // Build bottom leaf first, then wrap upwards into a chain.
            MemberNode node = new MemberNode($"S{s}_Leaf", "DInt", "0",
                $"S{s}.Leaf", null, Array.Empty<MemberNode>());
            for (int d = depth - 2; d >= 0; d--)
                node = new MemberNode($"S{s}_L{d}", "Struct", null,
                    $"S{s}.L{d}", null, new[] { node });
            stickRoots[s] = node;
        }
        var rootModel = new MemberNode("Root", "Struct", null,
            "Root", null, stickRoots);
        var rootVm = new MemberNodeViewModel(rootModel, null);
        SetExpandedRecursive(rootVm);

        // Highlight every stick's bottom leaf so each chain stays visible and
        // every visible internal node's HHD has to walk to the bottom.
        foreach (var stickTop in rootVm.Children)
        {
            var cur = stickTop;
            while (cur.Children.Count > 0) cur = cur.Children[0];
            cur.IsAffected = true;
        }

        // JIT warmup: prime both code paths so the timed sections don't pay
        // first-call compilation cost. swNew goes through four previously-
        // uncalled product methods (BuildFlatList, RefreshHighlightCache,
        // AddNodeToFlatList, BulkObservableCollection.ReplaceAll) — at the
        // sub-millisecond absolute scale this test runs at, JIT alone can
        // swamp the algorithmic gap and re-flip the assertion (#179).
        var warmupOldFlat = new List<MemberNodeViewModel>();
        OldAddNodeToFlatList(rootVm, warmupOldFlat);
        new FlatTreeManager().Refresh(new[] { rootVm });

        // ---- before: reproduce old per-node recursive scan (O(depth²) per stick) ----
        var swOld = Stopwatch.StartNew();
        var oldFlat = new List<MemberNodeViewModel>();
        OldAddNodeToFlatList(rootVm, oldFlat);
        swOld.Stop();

        // ---- after: real product flat-list build (O(n) cached) ----
        var mgr = new FlatTreeManager();
        var swNew = Stopwatch.StartNew();
        mgr.Refresh(new[] { rootVm });
        swNew.Stop();

        // Equivalence: identical visible sequence. Every node on every chain
        // is visible (each is on the path to a highlighted leaf).
        var totalNodes = 1 + sticks * depth;
        mgr.FlatList.Select(v => v.Name)
            .Should().Equal(oldFlat.Select(v => v.Name));
        oldFlat.Should().HaveCount(totalNodes);
        oldFlat[0].Name.Should().Be("Root");
        oldFlat[oldFlat.Count - 1].Name.Should().Be($"S{sticks - 1}_Leaf");

        var oldMs = swOld.Elapsed.TotalMilliseconds;
        var newMs = swNew.Elapsed.TotalMilliseconds;
        _out.WriteLine(
            $"[#154 H4] sticks={sticks} depth={depth} N={totalNodes}  "
            + $"before/recursive-scan={oldMs:F1} ms  after/cached={newMs:F1} ms  "
            + $"speedup≈{oldMs / System.Math.Max(newMs, 0.001):F1}x");

        newMs.Should().BeLessThan(oldMs,
            "the cached O(n) highlight pass must beat the old O(depth²) per-node recursion");
        (oldMs / System.Math.Max(newMs, 0.001)).Should().BeGreaterThan(3,
            "expected ratio ~20× (depth² vs depth) — a result below 3× means the "
            + "cache regressed or the workload's quadratic structure was broken");
    }

    private static void SetExpandedRecursive(MemberNodeViewModel node)
    {
        // Match production ExpandRecursive's HasChildren guard — IsExpanded /
        // IsSmartExpanded on a leaf is meaningless and the symmetry keeps any
        // future "IsSmartExpanded ⇒ HasChildren" invariant from tripping here.
        if (node.HasChildren)
        {
            node.IsExpanded = true;
            node.IsSmartExpanded = true;
        }
        foreach (var child in node.Children)
            SetExpandedRecursive(child);
    }

    // Faithful reproduction of the REMOVED FlatTreeManager.AddNodeToFlatList +
    // HasHighlightedDescendant (pre-#154-H4): the highlighted-descendant check
    // recursed the whole subtree for every visible node.
    private static void OldAddNodeToFlatList(
        MemberNodeViewModel node, List<MemberNodeViewModel> flat,
        bool parentIsSmartExpanded = false)
    {
        if (!node.IsVisible) return;
        if (parentIsSmartExpanded && !OldIsHighlighted(node) && !OldHasHighlightedDescendant(node))
            return;
        flat.Add(node);
        if (node.IsExpanded)
            foreach (var child in node.Children)
                OldAddNodeToFlatList(child, flat, node.IsSmartExpanded);
    }

    private static bool OldIsHighlighted(MemberNodeViewModel n)
        => n.IsAffected || n.IsAlreadyMatching || n.IsSearchMatch
           || n.IsPendingInlineEdit || n.HasInlineError;

    private static bool OldHasHighlightedDescendant(MemberNodeViewModel node)
    {
        foreach (var child in node.Children)
            if (OldIsHighlighted(child) || OldHasHighlightedDescendant(child))
                return true;
        return false;
    }

    private static string BuildLargeArrayDbXml(int n)
    {
        var sb = new StringBuilder(n * 64);
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Document>\n");
        sb.Append("  <SW.Blocks.GlobalDB ID=\"0\">\n    <AttributeList>\n      <Interface>\n");
        sb.Append("        <Sections xmlns=\"http://www.siemens.com/automation/Openness/SW/Interface/v5\">\n");
        sb.Append("          <Section Name=\"Static\">\n");
        sb.Append($"            <Member Name=\"BigArray\" Datatype=\"Array[1..{n}] of DInt\" Accessibility=\"Public\">\n");
        for (int i = 1; i <= n; i++)
            sb.Append($"              <Subelement Path=\"{i}\"><StartValue>{i}</StartValue></Subelement>\n");
        sb.Append("            </Member>\n          </Section>\n        </Sections>\n");
        sb.Append("      </Interface>\n      <Name>BigArrayDb</Name>\n      <Number>1</Number>\n");
        sb.Append("    </AttributeList>\n  </SW.Blocks.GlobalDB>\n</Document>\n");
        return sb.ToString();
    }
}
