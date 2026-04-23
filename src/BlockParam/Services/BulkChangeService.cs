using BlockParam.Config;
using BlockParam.Models;
using BlockParam.SimaticML;

namespace BlockParam.Services;

/// <summary>
/// Central service for bulk change operations.
/// Orchestrates: validation → modification → logging.
///
/// Supports two execution strategies:
/// - **Direct API** (small scopes, ≤ DirectApiThreshold members): preserves TIA Undo stack
/// - **XML Export/Import** (large scopes): uses ExclusiveAccess for performance, disables Undo
/// </summary>
public class BulkChangeService
{
    /// <summary>
    /// Maximum number of members to use the Direct API (SetAttribute) approach.
    /// Below this threshold, changes preserve TIA Portal's undo stack.
    /// Above this threshold, XML export/import with ExclusiveAccess is used.
    /// </summary>
    public const int DirectApiThreshold = 10;

    private readonly SimaticMLWriter _writer = new();
    private readonly ChangeLogger _logger;
    private readonly ConfigLoader _configLoader;

    public BulkChangeService(ChangeLogger logger, ConfigLoader configLoader)
    {
        _logger = logger;
        _configLoader = configLoader;
    }

    /// <summary>
    /// Determines the recommended execution strategy for a change set.
    /// </summary>
    public BulkStrategy RecommendStrategy(ChangeSet changeSet)
    {
        return changeSet.Scope.MatchCount <= DirectApiThreshold
            ? BulkStrategy.DirectApi
            : BulkStrategy.XmlExportImport;
    }

    /// <summary>
    /// Applies a bulk change to the given XML document (XML strategy).
    /// Returns modified XML for reimport.
    /// </summary>
    public ChangeResult ApplyViaXml(string xml, ChangeSet changeSet)
    {
        var validationError = ValidateChangeSet(changeSet);
        if (validationError != null)
        {
            return new ChangeResult(
                changeSet, xml, Array.Empty<ValueChange>(), new[] { validationError });
        }

        var writeResult = _writer.ModifyStartValues(
            xml, changeSet.Scope.MatchingMembers, changeSet.NewValue);

        LogChanges(changeSet, writeResult.Changes);

        return new ChangeResult(
            changeSet, writeResult.ModifiedXml, writeResult.Changes, writeResult.Errors);
    }

    /// <summary>
    /// Applies a bulk change via the Direct API (preserves TIA Undo).
    /// The caller must provide the adapter and member objects.
    /// Returns change records for logging (no modified XML — changes are applied directly).
    /// </summary>
    public DirectApiResult ApplyViaDirect(
        ChangeSet changeSet,
        ITiaPortalAdapter adapter,
        IReadOnlyList<object> memberObjects)
    {
        var validationError = ValidateChangeSet(changeSet);
        if (validationError != null)
        {
            return new DirectApiResult(new[] { validationError }, Array.Empty<ValueChange>());
        }

        var changes = new List<ValueChange>();
        var errors = new List<string>();

        for (int i = 0; i < memberObjects.Count && i < changeSet.Scope.MatchingMembers.Count; i++)
        {
            var member = changeSet.Scope.MatchingMembers[i];
            try
            {
                adapter.SetStartValueDirect(memberObjects[i], changeSet.NewValue);
                changes.Add(new ValueChange(
                    member.Path, member.Datatype,
                    member.StartValue ?? "", changeSet.NewValue));
            }
            catch (Exception ex)
            {
                errors.Add($"Failed to set {member.Path}: {ex.Message}");
            }
        }

        LogChanges(changeSet, changes);

        return new DirectApiResult(errors, changes);
    }

    /// <summary>
    /// Legacy method for backward compatibility. Uses XML strategy.
    /// </summary>
    public ChangeResult Apply(string xml, ChangeSet changeSet) => ApplyViaXml(xml, changeSet);

    private string? ValidateChangeSet(ChangeSet changeSet)
    {
        if (changeSet.Scope.MatchingMembers.Count == 0)
            return null;

        var firstMember = changeSet.Scope.MatchingMembers[0];

        // Type validation even without a rule (using member's datatype)
        var typeError = TiaDataTypeValidator.Validate(
            changeSet.NewValue, firstMember.Datatype);
        if (typeError != null) return typeError;

        // Rule-based validation (constraints with datatype passed through)
        var config = _configLoader.GetConfig();
        if (config == null) return null;

        var rule = config.GetRule(firstMember);
        return rule?.Constraints?.Validate(changeSet.NewValue, firstMember.Datatype);
    }

    private void LogChanges(ChangeSet changeSet, IEnumerable<ValueChange> changes)
    {
        foreach (var change in changes)
        {
            _logger.Log(new ChangeLogEntry(
                DateTime.UtcNow,
                changeSet.DbName,
                change.MemberPath,
                change.Datatype,
                change.OldValue,
                change.NewValue,
                changeSet.Scope.AncestorName));
        }
    }
}

/// <summary>
/// Execution strategy for a bulk operation.
/// </summary>
public enum BulkStrategy
{
    /// <summary>
    /// Use SetAttribute on each member. Slower but preserves TIA Undo stack.
    /// Recommended for ≤ 10 members.
    /// </summary>
    DirectApi,

    /// <summary>
    /// Export DB to XML, modify, reimport. Faster for large scopes but
    /// requires ExclusiveAccess which disables TIA Undo stack.
    /// </summary>
    XmlExportImport
}

/// <summary>
/// Result of a Direct API bulk operation.
/// </summary>
public class DirectApiResult
{
    public DirectApiResult(IReadOnlyList<string> errors, IReadOnlyList<ValueChange> changes)
    {
        Errors = errors;
        Changes = changes;
    }

    public IReadOnlyList<string> Errors { get; }
    public IReadOnlyList<ValueChange> Changes { get; }
    public bool IsSuccess => Errors.Count == 0;
    public int AffectedCount => Changes.Count;
}
