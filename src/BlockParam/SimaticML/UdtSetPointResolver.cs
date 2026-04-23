using System.Xml.Linq;
using static BlockParam.SimaticML.SimaticMLElements;
using static BlockParam.SimaticML.XmlHelpers;

namespace BlockParam.SimaticML;

/// <summary>
/// Parses TIA Portal UDT type-definition XMLs and resolves SetPoint attributes
/// for members that have none on the DB instance side (typical for leaves inside
/// arrays-of-UDT or nested UDT refs).
///
/// A UDT definition contains three kinds of members:
///   1. Leaf members with their own SetPoint boolean (e.g. <c>moduleId: Int</c>).
///   2. Nested Struct members whose children sit inline in the same type,
///      each with their own SetPoint.
///   3. UDT-reference members (Datatype="\"OtherUdt\"" or Array[..] of \"OtherUdt\")
///      whose AttributeList carries the UDT-instance-level SetPoint, while the
///      child members shown in the type def are structural-only (no SetPoint) —
///      their real values live in the referenced UDT.
/// </summary>
public class UdtSetPointResolver : UdtTypeResolverBase
{
    private readonly Dictionary<string, UdtTypeInfo> _types
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Number of UDT types loaded.</summary>
    public int TypeCount => _types.Count;

    /// <summary>True if at least one UDT type has been loaded.</summary>
    public bool HasTypes => _types.Count > 0;

    /// <summary>Parse a single UDT type-definition XML.</summary>
    public override void LoadFromXml(string xml)
    {
        var doc = XDocument.Parse(xml);
        var typeEl = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == PlcStruct);
        if (typeEl == null) return;

        var attrList = typeEl.Element(AttributeList);
        var name = attrList?.Element(Name)?.Value;
        if (string.IsNullOrEmpty(name)) return;

        var interfaceEl = attrList?.Element(Interface);
        var sections = interfaceEl?.Descendants().FirstOrDefault(e => e.Name.LocalName == Sections);
        if (sections == null) return;

        var rootMembers = new Dictionary<string, UdtMemberInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in LocalElements(sections, Section))
            CollectMembers(section, rootMembers);

        _types[name!] = new UdtTypeInfo(name!, rootMembers);
    }

    private static void CollectMembers(XElement parent, Dictionary<string, UdtMemberInfo> into)
    {
        foreach (var memberEl in LocalElements(parent, Member))
        {
            var memberName = memberEl.Attribute(Name)?.Value;
            var datatype = memberEl.Attribute(Datatype)?.Value ?? "";
            if (string.IsNullOrEmpty(memberName)) continue;

            var attrListEl = LocalElement(memberEl, AttributeList);
            bool? setPoint = (attrListEl is null ? null : LocalElements(attrListEl, BooleanAttribute)
                .FirstOrDefault(e => e.Attribute(Name)?.Value == SetPoint)
                ?.Value) switch
            {
                "true" => true,
                "false" => false,
                _ => null
            };

            var refUdt = ExtractUdtName(datatype);
            var children = new Dictionary<string, UdtMemberInfo>(StringComparer.OrdinalIgnoreCase);

            // Only recurse into inline Struct children (they own their SetPoint attributes).
            // UDT-ref inline expansions are ignored — their real SetPoint lives in the referenced type.
            // Struct children appear as direct <Member> elements (no <Sections> wrapper).
            if (refUdt == null && IsStruct(datatype))
            {
                CollectMembers(memberEl, children);
            }

            into[memberName!] = new UdtMemberInfo(memberName!, setPoint, refUdt, children);
        }
    }

    /// <summary>
    /// Extract the UDT type name from a datatype string like <c>"UDT_Foo"</c> or
    /// <c>Array[1..5] of "UDT_Foo"</c>. Returns null for non-UDT-ref datatypes.
    /// </summary>
    public static string? ExtractUdtName(string datatype)
    {
        if (string.IsNullOrEmpty(datatype)) return null;
        // Array[..] of "UDT_Name"
        var ofIdx = datatype.IndexOf(" of ", StringComparison.OrdinalIgnoreCase);
        var candidate = ofIdx >= 0 ? datatype.Substring(ofIdx + 4).Trim() : datatype.Trim();
        if (candidate.Length >= 2 && candidate[0] == '"' && candidate[candidate.Length - 1] == '"')
            return candidate.Substring(1, candidate.Length - 2);
        return null;
    }

    private static bool IsStruct(string datatype)
        => datatype.Equals("Struct", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolve the SetPoint flag for a member directly inside the given UDT type.
    /// <paramref name="pathWithinType"/> is a dot-path into the type's Struct hierarchy
    /// (empty for direct members). Returns null if the type or member is unknown.
    /// </summary>
    public bool? TryGetSetPoint(string udtTypeName, string pathWithinType, string memberName)
    {
        if (!_types.TryGetValue(udtTypeName, out var type)) return null;
        var parent = ResolveStructPath(type.Members, pathWithinType);
        if (parent == null) return null;
        return parent.TryGetValue(memberName, out var info) ? info.SetPoint : null;
    }

    /// <summary>True if the given UDT name has been loaded.</summary>
    public bool HasType(string udtTypeName) => _types.ContainsKey(udtTypeName);

    private static Dictionary<string, UdtMemberInfo>? ResolveStructPath(
        Dictionary<string, UdtMemberInfo> root,
        string pathWithinType)
    {
        if (string.IsNullOrEmpty(pathWithinType)) return root;
        var current = root;
        foreach (var segment in pathWithinType.Split('.'))
        {
            if (!current.TryGetValue(segment, out var info)) return null;
            current = info.Children;
        }
        return current;
    }

    private class UdtTypeInfo
    {
        public UdtTypeInfo(string name, Dictionary<string, UdtMemberInfo> members)
        {
            Name = name;
            Members = members;
        }
        public string Name { get; }
        public Dictionary<string, UdtMemberInfo> Members { get; }
    }

    private class UdtMemberInfo
    {
        public UdtMemberInfo(string name, bool? setPoint, string? refUdtName, Dictionary<string, UdtMemberInfo> children)
        {
            Name = name;
            SetPoint = setPoint;
            RefUdtName = refUdtName;
            Children = children;
        }
        public string Name { get; }
        public bool? SetPoint { get; }
        public string? RefUdtName { get; }
        public Dictionary<string, UdtMemberInfo> Children { get; }
    }
}
