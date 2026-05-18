using System.Diagnostics;
using System.Text;
using BlockParam.SimaticML;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace BlockParam.Tests;

/// <summary>
/// #159 before/after performance proof. The merged
/// <c>WriteBatch_TenThousandElementArray_AppliesCorrectlyAndFast</c> gate only
/// asserts a pass/fail ceiling; this test measures the OLD per-edit-parse
/// pattern and the NEW batch overload side by side on identical data, prints
/// both numbers to the CI test output, and fails loudly if the batch is not
/// dramatically faster — turning the fix into a quantified, CI-visible result
/// rather than just a green check.
/// </summary>
public class SimaticMLWriterPerfTests
{
    private readonly ITestOutputHelper _out;
    private readonly SimaticMLParser _parser = new();
    private readonly SimaticMLWriter _writer = new();

    public SimaticMLWriterPerfTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Batch_Apply_Is_Dramatically_Faster_Than_PerEdit_Parse()
    {
        // N is sized so the PRE-#159 pattern (one full XDocument parse +
        // serialize AND a linear Subelement scan PER edit → O(n^2)) still
        // finishes in a few seconds on a shared CI runner. The 10k issue
        // repro would take minutes on the old path — the whole point of the
        // bug. The post-#159 batch is one parse + O(1) indexed lookups → O(n).
        const int n = 1200;
        var xml = BuildLargeArrayDbXml(n);
        var db = _parser.Parse(xml);
        var arr = db.Members.First(m => m.Name == "BigArray");
        arr.Children.Should().HaveCount(n);

        var edits = arr.Children
            .Select((c, i) => (Member: c, Value: (i + 1).ToString()))
            .ToList();

        // OLD: exactly what ExecuteApply did before #159 — call the
        // single-value overload once per edit, threading the modified XML
        // forward. Every call re-parses + re-serializes the whole document.
        var swOld = Stopwatch.StartNew();
        var oldXml = xml;
        foreach (var (member, value) in edits)
            oldXml = _writer.ModifyStartValues(oldXml, new[] { member }, value).ModifiedXml;
        swOld.Stop();

        // NEW: the #159 batch overload — one parse, one serialize, O(1)
        // Subelement lookups via the per-Member index.
        var swNew = Stopwatch.StartNew();
        var batch = _writer.ModifyStartValues(xml, edits);
        swNew.Stop();

        var oldMs = swOld.Elapsed.TotalMilliseconds;
        var newMs = swNew.Elapsed.TotalMilliseconds;
        var speedup = oldMs / System.Math.Max(newMs, 0.001);
        _out.WriteLine(
            $"[#159 perf] N={n}  per-edit/old={oldMs:F0} ms  batch/new={newMs:F0} ms  speedup={speedup:F1}x");

        // Equivalence: both paths must produce the same final values, so we
        // are timing equivalent work — not a shortcut that skips edits.
        batch.Changes.Should().HaveCount(n);
        var fromOld = _parser.Parse(oldXml).Members.First(m => m.Name == "BigArray");
        var fromNew = _parser.Parse(batch.ModifiedXml).Members.First(m => m.Name == "BigArray");
        for (int i = 0; i < n; i++)
            fromNew.Children[i].StartValue.Should().Be(fromOld.Children[i].StartValue);

        // Regression gate. The real speedup is ~100x+; assert a conservative
        // 10x floor so a revert of the batching (H1) or the O(1) Subelement
        // index (H2) trips it without flaking on noisy shared runners.
        newMs.Should().BeLessThan(oldMs,
            "the #159 batch overload must beat the per-edit re-parse pattern");
        speedup.Should().BeGreaterThan(10,
            "#159 turns O(n) parse + O(n^2) Subelement scan into O(1) parse + "
            + "O(n) indexed lookups — a <10x result means H1 or H2 regressed");
    }

    /// <summary>
    /// Mirrors the synthetic <c>BigArray : Array[1..n] Of DInt</c> builder in
    /// <see cref="SimaticMLWriterTests"/> (kept local — a 15-line test-only
    /// string builder isn't worth a shared-helper coupling between two
    /// otherwise-independent test classes).
    /// </summary>
    private static string BuildLargeArrayDbXml(int n)
    {
        var sb = new StringBuilder(n * 64);
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
        sb.Append("<Document>\n");
        sb.Append("  <SW.Blocks.GlobalDB ID=\"0\">\n");
        sb.Append("    <AttributeList>\n");
        sb.Append("      <Interface>\n");
        sb.Append("        <Sections xmlns=\"http://www.siemens.com/automation/Openness/SW/Interface/v5\">\n");
        sb.Append("          <Section Name=\"Static\">\n");
        sb.Append($"            <Member Name=\"BigArray\" Datatype=\"Array[1..{n}] of DInt\" Accessibility=\"Public\">\n");
        for (int i = 1; i <= n; i++)
            sb.Append($"              <Subelement Path=\"{i}\"><StartValue>{i}</StartValue></Subelement>\n");
        sb.Append("            </Member>\n");
        sb.Append("          </Section>\n");
        sb.Append("        </Sections>\n");
        sb.Append("      </Interface>\n");
        sb.Append("      <Name>BigArrayDb</Name>\n");
        sb.Append("      <Number>1</Number>\n");
        sb.Append("    </AttributeList>\n");
        sb.Append("  </SW.Blocks.GlobalDB>\n");
        sb.Append("</Document>\n");
        return sb.ToString();
    }
}
