using BlockParam.SimaticML;

namespace BlockParam.Models;

/// <summary>
/// Represents one member's value change in a diff preview.
/// </summary>
public class DiffEntry
{
    public DiffEntry(string memberPath, string datatype, string oldValue, string newValue)
    {
        MemberPath = memberPath;
        Datatype = datatype;
        OldValue = oldValue;
        NewValue = newValue;
    }

    public string MemberPath { get; }
    public string Datatype { get; }
    public string OldValue { get; }
    public string NewValue { get; }
    public bool IsChanged => OldValue != NewValue;

    public static DiffEntry FromValueChange(ValueChange vc) =>
        new(vc.MemberPath, vc.Datatype, vc.OldValue, vc.NewValue);
}
