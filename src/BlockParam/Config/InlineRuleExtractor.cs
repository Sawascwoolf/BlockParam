using BlockParam.Diagnostics;
using BlockParam.Models;

namespace BlockParam.Config;

/// <summary>
/// Walks a parsed <see cref="DataBlockInfo"/> tree, extracts inline
/// <c>{bp_*=*}</c> rules from every member's comment, and injects them into a
/// <see cref="BulkChangeConfig"/> as synthetic <see cref="RuleSource.Inline"/>
/// rules.
///
/// Per issue #6, inline rules override config-file rules — this is achieved via
/// the <see cref="RuleSource.Inline"/> source bonus in
/// <see cref="Services.PathPatternMatcher.CalculateSpecificity"/>.
///
/// Rules from a previous DataBlockInfo are cleared from the config before new
/// ones are added, so the shared <see cref="ConfigLoader"/> cache stays reusable
/// across DBs opened in the same session.
/// </summary>
public static class InlineRuleExtractor
{
    /// <summary>
    /// Replaces all inline rules on <paramref name="config"/> with rules freshly
    /// extracted from <paramref name="db"/>. No-op if either argument is null.
    /// </summary>
    public static int ApplyTo(BulkChangeConfig? config, DataBlockInfo? db)
    {
        if (config == null || db == null) return 0;

        config.Rules.RemoveAll(r => r.Source == RuleSource.Inline);

        int added = 0;
        foreach (var root in db.Members)
            added += Walk(root, config.Rules);

        if (added > 0)
            Log.Information("InlineRuleExtractor: {Count} inline rules extracted from DB {Db}",
                added, db.Name);

        return added;
    }

    private static int Walk(MemberNode node, List<MemberRule> sink)
    {
        int added = 0;
        var inline = InlineRuleParser.Parse(node.Comments);
        if (inline != null)
        {
            sink.Add(InlineRuleParser.ToMemberRule(inline, node.Path, isArrayMember: node.IsArray));
            added++;
        }
        foreach (var child in node.Children)
            added += Walk(child, sink);
        return added;
    }
}
