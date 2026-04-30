using Newtonsoft.Json;
using BlockParam.Models;
using BlockParam.Services;

namespace BlockParam.Config;

/// <summary>
/// Root configuration model, deserialized from the optional JSON config file.
/// </summary>
public class BulkChangeConfig
{
    [JsonProperty("version")]
    public string Version { get; set; } = "1.0";

    [JsonProperty("rules")]
    public List<MemberRule> Rules { get; set; } = new();

    [JsonProperty("rulesDirectory")]
    public string? RulesDirectory { get; set; }

    [JsonProperty("licenseServerUrl")]
    public string? LicenseServerUrl { get; set; }

    /// <summary>
    /// User override for the addin's UI language (#50). Accepts "en" / "de" /
    /// any specific culture name (e.g. "de-DE"). When unset/empty the addin
    /// follows the OS culture, which is the same default TIA itself uses for
    /// non-localized addin assemblies. We don't try to follow TIA's own UI
    /// language because Openness has no documented hook that reflects it
    /// reliably at runtime.
    /// </summary>
    [JsonProperty("language")]
    public string? Language { get; set; }

    /// <summary>Copy-on-write metadata: tracks where this file was copied from.</summary>
    [JsonProperty("_copiedFrom")]
    public CopiedFromMetadata? CopiedFrom { get; set; }

    /// <summary>
    /// Finds the most specific matching rule for a MemberNode (leaf matching).
    /// When multiple rules match, the one with the highest specificity score wins.
    /// </summary>
    public MemberRule? GetRule(MemberNode member)
    {
        MemberRule? bestRule = null;
        int bestScore = -1;

        foreach (var r in Rules)
        {
            // Datatype filter
            if (!string.IsNullOrEmpty(r.Datatype)
                && !string.Equals(r.Datatype!.Trim('"'), member.Datatype.Trim('"'),
                    StringComparison.OrdinalIgnoreCase))
                continue;

            // PathPattern required
            if (string.IsNullOrEmpty(r.PathPattern))
                continue;
            if (!PathPatternMatcher.IsMatch(member, r.PathPattern!))
                continue;

            // Calculate specificity — most specific wins, source bonus as tiebreaker
            var score = PathPatternMatcher.CalculateSpecificity(
                r.PathPattern, null, r.Datatype, (int)r.Source);

            if (score > bestScore)
            {
                bestScore = score;
                bestRule = r;
            }
        }

        return bestRule;
    }

    /// <summary>
    /// Finds the most specific rule with a commentTemplate for a UDT instance node.
    /// Uses includeSelf=true so {udt:TypeName}$ matches the node itself.
    /// </summary>
    public MemberRule? GetCommentRule(MemberNode udtInstance)
    {
        MemberRule? bestRule = null;
        int bestScore = -1;

        foreach (var r in Rules)
        {
            if (string.IsNullOrEmpty(r.CommentTemplate))
                continue;
            if (string.IsNullOrEmpty(r.PathPattern))
                continue;
            if (!PathPatternMatcher.IsMatch(udtInstance, r.PathPattern!, includeSelf: true))
                continue;

            var score = PathPatternMatcher.CalculateSpecificity(
                r.PathPattern, null, r.Datatype, (int)r.Source);

            if (score > bestScore)
            {
                bestScore = score;
                bestRule = r;
            }
        }

        return bestRule;
    }
}

public class MemberRule
{
    [JsonProperty("pathPattern")]
    public string? PathPattern { get; set; }

    [JsonProperty("datatype")]
    public string? Datatype { get; set; }

    [JsonProperty("constraints")]
    public ValueConstraint? Constraints { get; set; }

    [JsonProperty("tagTableReference")]
    public TagTableReference? TagTableReference { get; set; }

    [JsonProperty("commentTemplate")]
    public string? CommentTemplate { get; set; }

    /// <summary>When true, members matching this rule are hidden in the SetPoint filter.</summary>
    [JsonProperty("excludeFromSetpoints")]
    public bool ExcludeFromSetpoints { get; set; }

    /// <summary>
    /// Source of this rule (set at load time, not serialized).
    /// Used for specificity tiebreaking: Shared=0, Local=50, TiaProject=200.
    /// </summary>
    [JsonIgnore]
    public RuleSource Source { get; set; } = RuleSource.Shared;
}

public enum RuleSource
{
    Shared = 0,
    Local = 50,
    TiaProject = 200,
    /// <summary>
    /// Rule parsed from an inline <c>{bp_*=*}</c> token in a UDT/DB member comment.
    /// Per issue #6, inline rules win over config-file rules.
    /// </summary>
    Inline = 500
}

/// <summary>
/// Reference to a TIA Tag Table (metadata only in V1, active in V2).
/// </summary>
public class TagTableReference
{
    [JsonProperty("tableName")]
    public string TableName { get; set; } = "";

    [JsonProperty("description")]
    public string? Description { get; set; }
}

/// <summary>
/// Metadata tracking where a local rule file was copied from (shared source).
/// Enables future "update available" detection.
/// </summary>
public class CopiedFromMetadata
{
    [JsonProperty("source")]
    public string Source { get; set; } = "";

    [JsonProperty("copiedAt")]
    public DateTime CopiedAt { get; set; }

    [JsonProperty("sourceModifiedAt")]
    public DateTime SourceModifiedAt { get; set; }
}
