using System.Xml.Linq;
using BlockParam.Models;
using static BlockParam.SimaticML.XmlHelpers;
using SE = BlockParam.SimaticML.SimaticMLElements;

namespace BlockParam.SimaticML;

/// <summary>
/// Modifies StartValues and Comments in SimaticML XML documents.
/// </summary>
public class SimaticMLWriter
{
    /// <summary>
    /// Changes the StartValue of all specified members in the XML document.
    /// Returns the modified XML as a string.
    /// </summary>
    public WriteResult ModifyStartValues(
        string xml,
        IReadOnlyList<MemberNode> targetMembers,
        string newValue)
    {
        var doc = XDocument.Parse(xml);
        var ns = SimaticMLNamespaces.Detect(doc);
        var changes = new List<ValueChange>();
        var errors = new List<string>();

        foreach (var target in targetMembers)
        {
            var location = FindStartValueLocation(doc, ns, target.Path);
            if (location == null)
            {
                errors.Add($"Member not found in XML: {target.Path}");
                continue;
            }

            var startValueElement = location.GetOrCreateStartValue(ns);

            // Read old value (from ConstantName attribute or text)
            var oldValue = startValueElement.Attribute(SE.ConstantName)?.Value?.Trim('"')
                        ?? startValueElement.Value;

            // Determine if newValue is a constant name or a literal value.
            // Constants start with a letter but are NOT: bool literals, TIA typed
            // prefixes (T#, D#, TOD#, etc.), or single-quoted strings.
            var isConstant = !string.IsNullOrEmpty(newValue)
                          && char.IsLetter(newValue[0])
                          && !IsKnownLiteral(newValue);

            // Clear previous content and attributes
            startValueElement.Value = "";
            startValueElement.Attribute(SE.ConstantName)?.Remove();

            if (isConstant)
                startValueElement.SetAttributeValue(SE.ConstantName, $"\"{newValue}\"");
            else
                startValueElement.Value = newValue;

            changes.Add(new ValueChange(target.Path, target.Datatype, oldValue, newValue));
        }

        return new WriteResult(doc.ToString(), changes, errors);
    }

    /// <summary>
    /// Updates the Comment of specified members in the XML document.
    /// </summary>
    public string ModifyComments(
        string xml,
        IReadOnlyList<MemberNode> targetMembers,
        IReadOnlyList<string> comments,
        string? language = null)
    {
        if (targetMembers.Count != comments.Count)
            throw new ArgumentException("targetMembers and comments must have the same length.");

        var doc = XDocument.Parse(xml);
        var ns = SimaticMLNamespaces.Detect(doc);

        // Auto-detect language from existing comments in the XML
        var effectiveLanguage = language ?? DetectLanguage(doc, ns);

        for (int i = 0; i < targetMembers.Count; i++)
        {
            var memberElement = FindMemberElement(doc, ns, targetMembers[i].Path);
            if (memberElement == null) continue;

            SetComment(memberElement, ns, comments[i], effectiveLanguage);
        }

        return doc.ToString();
    }

    /// <summary>
    /// Modifies the comment of a single member.
    /// </summary>
    public string ModifyComment(string xml, MemberNode target, string comment, string? language = null)
    {
        return ModifyComments(xml, new[] { target }, new[] { comment }, language);
    }

    /// <summary>
    /// Detects the language used in existing member comments.
    /// Falls back to "en-US" if no comments exist.
    /// </summary>
    private static string DetectLanguage(XDocument doc, XNamespace ns)
    {
        var firstLang = doc.Descendants(ns + SE.Comment)
            .Elements(ns + SE.MultiLanguageText)
            .Select(e => e.Attribute(SE.Lang)?.Value)
            .FirstOrDefault(l => l != null);
        return firstLang ?? "en-GB";
    }

    /// <summary>
    /// Removes specified members from the XML document (for struct cleanup).
    /// </summary>
    public string RemoveMembers(string xml, IReadOnlyList<MemberNode> membersToRemove)
    {
        var doc = XDocument.Parse(xml);
        var ns = SimaticMLNamespaces.Detect(doc);

        foreach (var member in membersToRemove)
        {
            var element = FindMemberElement(doc, ns, member.Path);
            element?.Remove();
        }

        return doc.ToString();
    }

    private XElement? FindMemberElement(XDocument doc, XNamespace ns, string path)
    {
        var location = FindStartValueLocation(doc, ns, path);
        return location?.Member;
    }

    /// <summary>
    /// Resolves a member path to the XML location that should hold its StartValue.
    /// Walks all name tokens down the Member hierarchy; any index tokens
    /// encountered along the way are collected into a comma-joined Subelement
    /// path attached to the final Member (which is the leaf field, not the
    /// enclosing array). This matches TIA's on-disk SimaticML format:
    /// <c>units[1].modules[3].moduleId</c> becomes
    /// <c>&lt;Subelement Path="1,3"&gt;</c> on the <c>moduleId</c> Member.
    /// </summary>
    private StartValueLocation? FindStartValueLocation(XDocument doc, XNamespace ns, string path)
    {
        var tokens = TokenizePath(path);
        if (tokens.Count == 0) return null;

        var sections = doc.Descendants(ns + SE.Section)
            .FirstOrDefault(s => s.Attribute(SE.Name)?.Value == "Static");
        if (sections == null) return null;

        XElement? current = sections;
        var indices = new List<string>();

        foreach (var token in tokens)
        {
            if (TryUnwrapIndexToken(token, out var inner))
            {
                // TIA index paths use comma-joined multi-dim indices, so
                // [3,1] becomes "3,1" — preserved as-is.
                indices.Add(inner);
                continue;
            }

            if (current == null) return null;

            var next = current.Elements(ns + SE.Member)
                .FirstOrDefault(m => m.Attribute(SE.Name)?.Value == token);

            if (next == null)
            {
                var nestedSections = LocalElement(current, SE.Sections);
                if (nestedSections != null)
                {
                    foreach (var section in LocalElements(nestedSections, SE.Section))
                    {
                        var sectionNs = section.Name.Namespace;
                        if (sectionNs == XNamespace.None) sectionNs = ns;
                        next = section.Elements(sectionNs + SE.Member)
                            .FirstOrDefault(m => m.Attribute(SE.Name)?.Value == token);
                        if (next != null) break;
                    }
                }
            }

            current = next;
        }

        if (current == null) return null;

        var subPath = indices.Count > 0 ? string.Join(",", indices) : null;
        return new StartValueLocation(current, subPath);
    }

    /// <summary>
    /// Splits a member path like <c>"Foo.Bar[3].Speed"</c> or
    /// <c>"Matrix[0,1]"</c> into ordered tokens:
    /// <c>["Foo", "Bar", "[3]", "Speed"]</c>, <c>["Matrix", "[0,1]"]</c>.
    /// </summary>
    public static List<string> TokenizePath(string path)
    {
        var tokens = new List<string>();
        int i = 0;
        while (i < path.Length)
        {
            if (path[i] == '[')
            {
                int end = path.IndexOf(']', i);
                if (end < 0) break;
                tokens.Add(path.Substring(i, end - i + 1));
                i = end + 1;
                if (i < path.Length && path[i] == '.') i++;
            }
            else
            {
                int next = path.IndexOfAny(new[] { '.', '[' }, i);
                if (next < 0)
                {
                    tokens.Add(path.Substring(i));
                    break;
                }
                tokens.Add(path.Substring(i, next - i));
                i = next;
                if (path[i] == '.') i++;
            }
        }
        return tokens;
    }

    /// <summary>
    /// Recognises a bracketed index token (e.g. <c>[3]</c>, <c>[0,1]</c>) and
    /// returns the inner comma-joined index list. The <c>Length &gt;= 2</c>
    /// precondition — paired with the <c>[</c>/<c>]</c> sentinels — is what
    /// makes the caller's substring safe; returning the inner string here
    /// keeps that invariant encapsulated instead of leaving it at the call site.
    /// </summary>
    private static bool TryUnwrapIndexToken(string token, out string inner)
    {
        if (token.Length >= 2 && token[0] == '[' && token[token.Length - 1] == ']')
        {
            inner = token.Substring(1, token.Length - 2);
            return true;
        }
        inner = "";
        return false;
    }

    /// <summary>
    /// Points at either a Member's own StartValue or a Subelement's StartValue
    /// within an array Member.
    /// </summary>
    private sealed class StartValueLocation
    {
        public StartValueLocation(XElement member, string? subelementPath)
        {
            Member = member;
            SubelementPath = subelementPath;
        }

        public XElement Member { get; }
        public string? SubelementPath { get; }

        public XElement GetOrCreateStartValue(XNamespace ns)
        {
            if (SubelementPath == null)
            {
                var sv = Member.Element(ns + SE.StartValue);
                if (sv == null)
                {
                    sv = new XElement(ns + SE.StartValue);
                    Member.Add(sv);
                }
                return sv;
            }

            // Find or create the matching <Subelement Path="..."> under Member.
            var sub = LocalElements(Member, SE.Subelement)
                .FirstOrDefault(e => e.Attribute(SE.Path)?.Value == SubelementPath);
            if (sub == null)
            {
                sub = new XElement(ns + SE.Subelement, new XAttribute(SE.Path, SubelementPath));
                Member.Add(sub);
            }

            var inner = LocalElement(sub, SE.StartValue);
            if (inner == null)
            {
                inner = new XElement(ns + SE.StartValue);
                sub.Add(inner);
            }
            return inner;
        }
    }

    private static void SetComment(XElement memberElement, XNamespace ns, string comment, string language)
    {
        var commentElement = memberElement.Element(ns + SE.Comment);
        if (commentElement == null)
        {
            commentElement = new XElement(ns + SE.Comment);
            // SimaticML element order: AttributeList → Comment → Sections/Member/StartValue
            var attributeList = memberElement.Element(ns + SE.AttributeList);
            if (attributeList != null)
                attributeList.AddAfterSelf(commentElement);
            else
                memberElement.AddFirst(commentElement);
        }

        var textElement = commentElement.Elements(ns + SE.MultiLanguageText)
            .FirstOrDefault(e => e.Attribute(SE.Lang)?.Value == language);

        if (textElement == null)
        {
            textElement = new XElement(ns + SE.MultiLanguageText,
                new XAttribute(SE.Lang, language));
            commentElement.Add(textElement);
        }

        textElement.Value = comment;
    }

    /// <summary>
    /// Returns true if the value is a known TIA literal (not a constant name),
    /// even though it starts with a letter. Prevents false-positive ConstantName
    /// attributes for values like "true", "false", "T#1s", "TOD#12:00:00", etc.
    /// </summary>
    private static bool IsKnownLiteral(string value)
    {
        // Bool literals
        if (value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("false", StringComparison.OrdinalIgnoreCase))
            return true;

        // TIA typed literal prefixes: T#, LT#, S5T#, D#, TOD#, LTOD#, DT#, LDT#
        var upper = value.ToUpperInvariant();
        if (upper.StartsWith("T#") || upper.StartsWith("LT#") || upper.StartsWith("S5T#")
            || upper.StartsWith("D#") || upper.StartsWith("TOD#") || upper.StartsWith("LTOD#")
            || upper.StartsWith("DT#") || upper.StartsWith("LDT#"))
            return true;

        return false;
    }
}

public class WriteResult
{
    public WriteResult(string modifiedXml, List<ValueChange> changes, List<string> errors)
    {
        ModifiedXml = modifiedXml;
        Changes = changes;
        Errors = errors;
    }

    public string ModifiedXml { get; }
    public List<ValueChange> Changes { get; }
    public List<string> Errors { get; }
    public bool HasErrors => Errors.Count > 0;
    public bool IsSuccess => !HasErrors;
}

public class ValueChange
{
    public ValueChange(string memberPath, string datatype, string oldValue, string newValue)
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

    public override string ToString() => $"{MemberPath}: '{OldValue}' → '{NewValue}'";
}
