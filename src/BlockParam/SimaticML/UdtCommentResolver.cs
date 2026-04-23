using System.Xml.Linq;
using static BlockParam.SimaticML.SimaticMLElements;
using static BlockParam.SimaticML.XmlHelpers;

namespace BlockParam.SimaticML;

/// <summary>
/// Parses TIA Portal UDT type-definition XMLs and resolves member <c>&lt;Comment&gt;</c>
/// text for members that have no instance-level override on the DB side.
/// Mirrors <see cref="UdtSetPointResolver"/>: the DB-instance comment wins when present;
/// this resolver supplies the UDT-type fallback.
///
/// Struct transparency + UDT-ref handling match the SetPoint resolver — inline Struct
/// children own their own comment; UDT-ref inline expansions do not (their real comment
/// lives inside the referenced UDT).
/// </summary>
public class UdtCommentResolver : UdtTypeResolverBase
{
    private readonly Dictionary<string, UdtTypeInfo> _types
        = new(StringComparer.OrdinalIgnoreCase);

    public int TypeCount => _types.Count;
    public bool HasTypes => _types.Count > 0;

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

            var commentEl = LocalElement(memberEl, Comment);
            var comments = MultiLanguageCommentReader.Read(commentEl);

            var refUdt = UdtSetPointResolver.ExtractUdtName(datatype);
            var children = new Dictionary<string, UdtMemberInfo>(StringComparer.OrdinalIgnoreCase);

            // Only recurse into inline Struct children. UDT-ref inline expansions carry no
            // real comment here — they live in the referenced type.
            if (refUdt == null && datatype.Equals("Struct", StringComparison.OrdinalIgnoreCase))
                CollectMembers(memberEl, children);

            into[memberName!] = new UdtMemberInfo(memberName!, comments, children);
        }
    }

    /// <summary>
    /// Resolve the comment for <paramref name="memberName"/> at <paramref name="pathWithinType"/>
    /// inside <paramref name="udtTypeName"/>. Returns null when the type or member is unknown,
    /// or when the member has no comment in the UDT type definition.
    /// First non-empty language variant is returned, matching legacy callers.
    /// </summary>
    public string? TryGetComment(string udtTypeName, string pathWithinType, string memberName)
    {
        var comments = TryGetComments(udtTypeName, pathWithinType, memberName);
        return comments?.Values.FirstOrDefault(v => !string.IsNullOrEmpty(v));
    }

    /// <summary>
    /// Resolve the multilingual comment dict for a member inside the given UDT
    /// type. Returns null if the type, path or member is unknown, or the member
    /// has no comment. Keys are TIA culture names (e.g. "de-DE", "en-GB").
    /// </summary>
    public IReadOnlyDictionary<string, string>? TryGetComments(
        string udtTypeName, string pathWithinType, string memberName)
    {
        if (!_types.TryGetValue(udtTypeName, out var type)) return null;
        var parent = ResolveStructPath(type.Members, pathWithinType);
        if (parent == null) return null;
        return parent.TryGetValue(memberName, out var info) ? info.Comments : null;
    }

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
        public UdtMemberInfo(string name, IReadOnlyDictionary<string, string>? comments,
            Dictionary<string, UdtMemberInfo> children)
        {
            Name = name;
            Comments = comments;
            Children = children;
        }
        public string Name { get; }
        public IReadOnlyDictionary<string, string>? Comments { get; }
        public Dictionary<string, UdtMemberInfo> Children { get; }
    }
}
