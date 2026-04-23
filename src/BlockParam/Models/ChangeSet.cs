using BlockParam.Services;
using BlockParam.SimaticML;

namespace BlockParam.Models;

/// <summary>
/// Describes a bulk change operation to be applied.
/// </summary>
public class ChangeSet
{
    public ChangeSet(
        string dbName,
        string memberName,
        string memberDatatype,
        ScopeLevel scope,
        string newValue)
    {
        DbName = dbName;
        MemberName = memberName;
        MemberDatatype = memberDatatype;
        Scope = scope;
        NewValue = newValue;
    }

    public string DbName { get; }
    public string MemberName { get; }
    public string MemberDatatype { get; }
    public ScopeLevel Scope { get; }
    public string NewValue { get; }
}

/// <summary>
/// Result of a completed bulk change operation.
/// </summary>
public class ChangeResult
{
    public ChangeResult(
        ChangeSet changeSet,
        string modifiedXml,
        IReadOnlyList<ValueChange> changes,
        IReadOnlyList<string> errors)
    {
        ChangeSet = changeSet;
        ModifiedXml = modifiedXml;
        Changes = changes;
        Errors = errors;
    }

    public ChangeSet ChangeSet { get; }
    public string ModifiedXml { get; }
    public IReadOnlyList<ValueChange> Changes { get; }
    public IReadOnlyList<string> Errors { get; }
    public bool IsSuccess => Errors.Count == 0;
    public int AffectedCount => Changes.Count;
}
