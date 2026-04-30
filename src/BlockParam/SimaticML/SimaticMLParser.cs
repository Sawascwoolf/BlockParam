using System.Xml.Linq;
using BlockParam.Models;
using BlockParam.Services;
using static BlockParam.SimaticML.SimaticMLElements;
using static BlockParam.SimaticML.XmlHelpers;

namespace BlockParam.SimaticML;

/// <summary>
/// Parses SimaticML XML (exported Data Blocks) into a DataBlockInfo tree.
/// </summary>
public class SimaticMLParser
{
    private static readonly HashSet<string> SupportedBlockTypes = new()
    {
        "SW.Blocks.GlobalDB",
        "SW.Blocks.InstanceDB"
    };

    /// <summary>
    /// Defensive cap on eager array expansion. Above this total element count
    /// the array stays collapsed with <see cref="MemberNode.UnresolvedBound"/>
    /// set to a size marker. Prevents OOM on DBs with very large or deeply
    /// multi-dimensional arrays (e.g. Array[0..999, 0..999] of Int = 1M nodes).
    /// </summary>
    private const int MaxExpandedElements = 100_000;

    private readonly IConstantResolver? _constantResolver;
    private readonly UdtSetPointResolver? _udtResolver;
    private readonly UdtCommentResolver? _commentResolver;

    public SimaticMLParser() : this(null, null, null) { }

    /// <summary>
    /// Creates a parser with optional resolvers. The constant resolver expands
    /// symbolic array bounds like <c>Array[1..MAX_VALVES]</c>; unresolved bounds
    /// collapse the array into a leaf with <see cref="MemberNode.UnresolvedBound"/>
    /// set. The UDT resolver fills in SetPoint flags for members whose DB XML
    /// does not carry the attribute (typical for UDT-instance children).
    /// The comment resolver does the same for <c>&lt;Comment&gt;</c> text.
    /// </summary>
    public SimaticMLParser(
        IConstantResolver? constantResolver,
        UdtSetPointResolver? udtResolver = null,
        UdtCommentResolver? commentResolver = null)
    {
        _constantResolver = constantResolver;
        _udtResolver = udtResolver;
        _commentResolver = commentResolver;
    }

    public DataBlockInfo Parse(string xml)
    {
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (Exception ex) { throw new SimaticMLParseException("Failed to parse XML document.", ex); }

        return ParseDocument(doc);
    }

    public DataBlockInfo ParseFile(string filePath)
    {
        XDocument doc;
        try { doc = XDocument.Load(filePath); }
        catch (Exception ex) { throw new SimaticMLParseException($"Failed to load XML file: {filePath}", ex); }

        return ParseDocument(doc);
    }

    private DataBlockInfo ParseDocument(XDocument doc)
    {
        var blockElement = doc.Descendants()
            .FirstOrDefault(e => SupportedBlockTypes.Contains(e.Name.LocalName));

        if (blockElement == null)
        {
            var rootElements = doc.Descendants()
                .Where(e => e.Name.LocalName.StartsWith("SW.Blocks."))
                .Select(e => e.Name.LocalName)
                .Distinct();

            var found = string.Join(", ", rootElements);
            throw new SimaticMLParseException(
                string.IsNullOrEmpty(found)
                    ? "No SW.Blocks.* element found in XML document."
                    : $"Unsupported block type(s): {found}. Only GlobalDB and InstanceDB are supported.");
        }

        var blockType = blockElement.Name.LocalName.Replace("SW.Blocks.", "");

        var attrList = blockElement.Element(AttributeList)
            ?? throw new SimaticMLParseException("No <AttributeList> found in block element.");

        var name = attrList.Element(Name)?.Value
            ?? throw new SimaticMLParseException("No <Name> found in AttributeList.");

        var numberStr = attrList.Element(Number)?.Value ?? "0";
        if (!int.TryParse(numberStr, out var number))
            number = 0;

        var memoryLayout = attrList.Element(MemoryLayout)?.Value ?? "Optimized";

        var interfaceElement = attrList.Element(Interface)
            ?? throw new SimaticMLParseException("No <Interface> found in AttributeList.");
        var sectionsElement = interfaceElement.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == Sections);

        if (sectionsElement == null)
            throw new SimaticMLParseException("No <Sections> element found in Interface.");

        var ns = sectionsElement.Name.Namespace;
        if (ns == XNamespace.None)
            throw new SimaticMLParseException("The <Sections> element has no namespace defined.");
        var staticSection = sectionsElement.Elements(ns + Section)
            .FirstOrDefault(s => s.Attribute(Name)?.Value == "Static");

        if (staticSection == null)
            throw new SimaticMLParseException("No <Section Name=\"Static\"> found in Interface.");

        var unresolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var members = ParseMembers(
            staticSection, ns,
            parentNode: null, pathPrefix: "",
            indexStack: Array.Empty<string>(),
            enclosingUdt: null, pathWithinUdt: "",
            unresolvedUdts: unresolved);

        return new DataBlockInfo(name, number, memoryLayout, blockType, members, unresolved.ToArray());
    }

    /// <summary>Walks <c>&lt;Member&gt;</c> children and builds a MemberNode tree.</summary>
    private IReadOnlyList<MemberNode> ParseMembers(
        XElement parent,
        XNamespace ns,
        MemberNode? parentNode,
        string pathPrefix,
        IReadOnlyList<string> indexStack,
        string? enclosingUdt,
        string pathWithinUdt,
        HashSet<string> unresolvedUdts)
    {
        var result = new List<MemberNode>();

        foreach (var memberElement in parent.Elements(ns + Member))
        {
            var name = memberElement.Attribute(Name)?.Value ?? "";
            var datatype = memberElement.Attribute(Datatype)?.Value ?? "";
            var path = string.IsNullOrEmpty(pathPrefix) ? name : $"{pathPrefix}.{name}";

            var attrListElement = LocalElement(memberElement, AttributeList);
            var setPointAttrValue = attrListElement is null ? null : LocalElements(attrListElement, BooleanAttribute)
                .FirstOrDefault(e => e.Attribute(Name)?.Value == SetPoint)
                ?.Value;

            bool isSetPoint = ResolveSetPoint(
                setPointAttrValue, enclosingUdt, pathWithinUdt, name, unresolvedUdts);

            // Per-instance overrides on members inside enclosing arrays are stored as
            // <Subelement Path="i,j,..."><Comment>...</Comment></Subelement> on the member
            // template, mirroring how StartValue overrides work. Falls through to the
            // direct <Comment> child when no per-instance override exists.
            var commentElement = indexStack.Count > 0
                ? ReadSubelementComment(memberElement, ns, string.Join(",", indexStack))
                : null;
            commentElement ??= LocalElement(memberElement, Comment);
            var dbComments = ReadMultiLanguageComments(commentElement);

            // Fall back to the UDT-type definition only when the DB instance carries no
            // <Comment> element at all. A present-but-blank element is treated as an
            // intentional empty override, mirroring ResolveSetPoint's "attribute missing
            // vs value set" distinction. We merge in UDT-only languages so inline rule
            // tokens defined on the UDT still reach rule extraction.
            var udtComments = commentElement == null && enclosingUdt != null
                ? _commentResolver?.TryGetComments(enclosingUdt, pathWithinUdt, name)
                : null;
            var commentsByLang = MergeComments(dbComments, udtComments);
            var comment = commentsByLang.Values.FirstOrDefault(v => !string.IsNullOrEmpty(v));

            // Array members expand into per-index children. Bounds get resolved
            // against the optional constant resolver; unresolved bounds collapse
            // the array into a leaf with UnresolvedBound set.
            if (ArrayTypeParser.TryParse(datatype, out var arrayInfo))
            {
                var arrayNode = ExpandArrayMember(
                    memberElement, ns, parentNode, name, datatype, path,
                    isSetPoint, comment, commentsByLang, arrayInfo, indexStack,
                    enclosingUdt, pathWithinUdt, unresolvedUdts);
                result.Add(arrayNode);
                continue;
            }

            // Plain member — read StartValue. Outside any array context this
            // comes from the direct <StartValue> child; inside array context
            // from <Subelement Path="joined-indices"> on this Member.
            string? startValue;
            if (indexStack.Count == 0)
            {
                var sv = memberElement.Element(ns + StartValue);
                startValue = sv?.Attribute(ConstantName)?.Value?.Trim('"') ?? sv?.Value;
            }
            else
            {
                startValue = ReadSubelementValue(memberElement, ns, string.Join(",", indexStack));
            }

            var childrenList = new List<MemberNode>();
            var node = new MemberNode(name, datatype, startValue, path, parentNode, childrenList,
                isSetPoint, comment, comments: commentsByLang);

            // Determine child UDT context: UDT ref → new UDT, reset path.
            // Struct inside a UDT → same UDT, append name to path. Otherwise pass through.
            var (childUdt, childPathWithinUdt) = DeriveChildUdtContext(
                datatype, name, enclosingUdt, pathWithinUdt);

            // Direct child Members (inline Structs) inherit indexStack and UDT context.
            var children = ParseMembers(
                memberElement, ns, node, path, indexStack,
                childUdt, childPathWithinUdt, unresolvedUdts);
            childrenList.AddRange(children);

            // UDT instances have children in <Sections><Section Name="None"><Member .../>
            var nestedSections = LocalElement(memberElement, Sections);
            if (nestedSections != null)
            {
                var nestedNs = nestedSections.Name.Namespace;
                if (nestedNs == XNamespace.None) nestedNs = ns;
                foreach (var section in LocalElements(nestedSections, Section))
                {
                    var sectionChildren = ParseMembers(
                        section, nestedNs, node, path, indexStack,
                        childUdt, childPathWithinUdt, unresolvedUdts);
                    childrenList.AddRange(sectionChildren);
                }
            }

            result.Add(node);
        }

        return result;
    }

    private bool ResolveSetPoint(
        string? setPointAttrValue,
        string? enclosingUdt,
        string pathWithinUdt,
        string name,
        HashSet<string> unresolvedUdts)
    {
        if (setPointAttrValue != null) return setPointAttrValue == "true";
        if (enclosingUdt == null || _udtResolver == null) return false;

        if (_udtResolver.HasType(enclosingUdt))
            return _udtResolver.TryGetSetPoint(enclosingUdt, pathWithinUdt, name) ?? false;

        unresolvedUdts.Add(enclosingUdt);
        return false;
    }

    private static (string? childUdt, string childPathWithinUdt) DeriveChildUdtContext(
        string datatype, string name, string? enclosingUdt, string pathWithinUdt)
    {
        var refUdt = UdtSetPointResolver.ExtractUdtName(datatype);
        if (refUdt != null) return (refUdt, "");
        if (enclosingUdt != null && datatype.Equals("Struct", StringComparison.OrdinalIgnoreCase))
        {
            return (enclosingUdt,
                string.IsNullOrEmpty(pathWithinUdt) ? name : $"{pathWithinUdt}.{name}");
        }
        return (enclosingUdt, pathWithinUdt);
    }

    private MemberNode ExpandArrayMember(
        XElement memberElement,
        XNamespace ns,
        MemberNode? parentNode,
        string name,
        string datatype,
        string path,
        bool isSetPoint,
        string? comment,
        IReadOnlyDictionary<string, string> commentsByLang,
        ArrayTypeInfo arrayInfo,
        IReadOnlyList<string> indexStack,
        string? enclosingUdt,
        string pathWithinUdt,
        HashSet<string> unresolvedUdts)
    {
        var resolvedRanges = new List<(int low, int high)>();
        string? firstUnresolved = null;
        foreach (var dim in arrayInfo.Dimensions)
        {
            if (!TryResolveBound(dim.LowerBoundToken, out var low))
            {
                firstUnresolved = dim.LowerBoundToken;
                break;
            }
            if (!TryResolveBound(dim.UpperBoundToken, out var high))
            {
                firstUnresolved = dim.UpperBoundToken;
                break;
            }
            resolvedRanges.Add((low, high));
        }

        if (firstUnresolved != null)
        {
            return new MemberNode(
                name, datatype, startValue: null, path, parentNode,
                Array.Empty<MemberNode>(), isSetPoint, comment,
                isArrayElement: false, unresolvedBound: firstUnresolved,
                comments: commentsByLang);
        }

        // Defensive cap: refuse to materialise absurd numbers of MemberNodes.
        long totalElements = 1;
        foreach (var (low, high) in resolvedRanges)
        {
            var span = (long)high - low + 1;
            if (span <= 0) { totalElements = 0; break; }
            totalElements *= span;
            if (totalElements > MaxExpandedElements) break;
        }
        if (totalElements > MaxExpandedElements)
        {
            return new MemberNode(
                name, datatype, startValue: null, path, parentNode,
                Array.Empty<MemberNode>(), isSetPoint, comment,
                isArrayElement: false,
                unresolvedBound: $"(too large: {totalElements:N0} elements)",
                comments: commentsByLang);
        }

        var childrenList = new List<MemberNode>();
        var arrayNode = new MemberNode(
            name, datatype, startValue: null, path, parentNode,
            childrenList, isSetPoint, comment, comments: commentsByLang);

        var elementType = arrayInfo.ElementType;
        var elementIsStruct = elementType.Equals("Struct", StringComparison.OrdinalIgnoreCase);
        var elementIsUdt = elementType.StartsWith("\"") && elementType.EndsWith("\"");
        var elementIsPrimitive = !elementIsStruct && !elementIsUdt;

        // UDT context for array elements:
        //   Array of UDT → element opens a new UDT context (reset path).
        //   Array of Struct → inherit current UDT, no path change (Struct members append their own name).
        //   Array of primitive → inherited context is irrelevant for leaves.
        string? elementUdt;
        string elementPathWithinUdt;
        if (elementIsUdt)
        {
            elementUdt = UdtSetPointResolver.ExtractUdtName(elementType);
            elementPathWithinUdt = "";
        }
        else
        {
            elementUdt = enclosingUdt;
            elementPathWithinUdt = pathWithinUdt;
        }

        foreach (var indexTuple in EnumerateIndexTuples(resolvedRanges))
        {
            var indexLabel = $"[{string.Join(",", indexTuple)}]";
            var childPath = path + indexLabel;

            var childIndexStack = new List<string>(indexStack.Count + indexTuple.Length);
            childIndexStack.AddRange(indexStack);
            foreach (var i in indexTuple) childIndexStack.Add(i.ToString());

            if (elementIsPrimitive)
            {
                var sv = ReadSubelementValue(memberElement, ns, string.Join(",", childIndexStack));
                var leaf = new MemberNode(
                    name: indexLabel,
                    datatype: elementType,
                    startValue: sv,
                    path: childPath,
                    parent: arrayNode,
                    children: Array.Empty<MemberNode>(),
                    isSetPoint: false,
                    comment: null,
                    isArrayElement: true);
                childrenList.Add(leaf);
                continue;
            }

            var elementChildrenList = new List<MemberNode>();
            var elementNode = new MemberNode(
                name: indexLabel,
                datatype: elementType,
                startValue: null,
                path: childPath,
                parent: arrayNode,
                children: elementChildrenList,
                isSetPoint: false,
                comment: null,
                isArrayElement: true);

            if (elementIsStruct)
            {
                var tmpl = ParseMembers(
                    memberElement, ns, elementNode, childPath, childIndexStack,
                    elementUdt, elementPathWithinUdt, unresolvedUdts);
                elementChildrenList.AddRange(tmpl);
            }
            else // UDT
            {
                var nestedSections = LocalElement(memberElement, Sections);
                if (nestedSections != null)
                {
                    var nestedNs = nestedSections.Name.Namespace;
                    if (nestedNs == XNamespace.None) nestedNs = ns;
                    foreach (var section in LocalElements(nestedSections, Section))
                    {
                        var tmpl = ParseMembers(
                            section, nestedNs, elementNode, childPath, childIndexStack,
                            elementUdt, elementPathWithinUdt, unresolvedUdts);
                        elementChildrenList.AddRange(tmpl);
                    }
                }
                else if (elementUdt != null && _udtResolver != null && !_udtResolver.HasType(elementUdt))
                {
                    // No nested sections AND UDT is unknown: flag it so the UI
                    // can prompt the user to export UDTs.
                    unresolvedUdts.Add(elementUdt);
                }
            }

            childrenList.Add(elementNode);
        }

        return arrayNode;
    }

    private bool TryResolveBound(string token, out int value)
    {
        if (int.TryParse(token, out value)) return true;
        if (_constantResolver != null && _constantResolver.TryResolve(token, out value)) return true;
        value = 0;
        return false;
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyComments
        = new Dictionary<string, string>(0);

    /// <summary>
    /// Merges UDT-definition comments into the DB-instance comments. DB entries
    /// win per language key (an instance may override a UDT comment), but
    /// languages that exist only in the UDT definition are added so inline
    /// rule extraction still sees them.
    /// </summary>
    private static IReadOnlyDictionary<string, string> MergeComments(
        IReadOnlyDictionary<string, string> dbComments,
        IReadOnlyDictionary<string, string>? udtComments)
    {
        if (udtComments == null || udtComments.Count == 0) return dbComments;
        if (dbComments.Count == 0) return udtComments;

        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in dbComments)
            merged[kv.Key] = kv.Value;
        foreach (var kv in udtComments)
        {
            if (!merged.ContainsKey(kv.Key))
                merged[kv.Key] = kv.Value;
        }
        return merged;
    }

    private static IReadOnlyDictionary<string, string> ReadMultiLanguageComments(XElement? commentElement)
        => MultiLanguageCommentReader.Read(commentElement) ?? EmptyComments;

    private static string? ReadSubelementValue(XElement memberElement, XNamespace ns, string subelementPath)
    {
        foreach (var sub in LocalElements(memberElement, Subelement))
        {
            if (sub.Attribute(Path)?.Value != subelementPath) continue;
            var sv = LocalElement(sub, StartValue);
            return sv?.Attribute(ConstantName)?.Value?.Trim('"') ?? sv?.Value;
        }
        return null;
    }

    private static XElement? ReadSubelementComment(XElement memberElement, XNamespace ns, string subelementPath)
    {
        foreach (var sub in LocalElements(memberElement, Subelement))
        {
            if (sub.Attribute(Path)?.Value != subelementPath) continue;
            return LocalElement(sub, Comment);
        }
        return null;
    }

    private static IEnumerable<int[]> EnumerateIndexTuples(List<(int low, int high)> ranges)
    {
        var dims = ranges.Count;
        var indices = new int[dims];
        for (int i = 0; i < dims; i++) indices[i] = ranges[i].low;

        while (true)
        {
            yield return (int[])indices.Clone();

            int d = dims - 1;
            while (d >= 0)
            {
                indices[d]++;
                if (indices[d] <= ranges[d].high) break;
                indices[d] = ranges[d].low;
                d--;
            }
            if (d < 0) yield break;
        }
    }
}
