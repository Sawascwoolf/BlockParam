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
    /// Changes the StartValue of all specified members to the same value.
    /// Parses + serializes the document exactly once regardless of how many
    /// members are targeted. Returns the modified XML as a string.
    /// </summary>
    public WriteResult ModifyStartValues(
        string xml,
        IReadOnlyList<MemberNode> targetMembers,
        string newValue)
    {
        var doc = XDocument.Parse(xml);
        var ctx = new WriteContext(doc, SimaticMLNamespaces.Detect(doc));
        var changes = new List<ValueChange>();
        var errors = new List<string>();

        foreach (var target in targetMembers)
            ApplyOne(ctx, target, newValue, changes, errors);

        return new WriteResult(doc.ToString(), changes, errors);
    }

    /// <summary>
    /// Batch overload (#159 H1): applies a distinct value per member while
    /// parsing and serializing the (potentially multi-MB) document exactly
    /// once. The Apply loop used to call <see cref="ModifyStartValues(string,
    /// IReadOnlyList{MemberNode}, string)"/> once per pending edit — an
    /// O(n) full parse + serialize per edit, O(n) total for n edits. Routing
    /// the whole pending batch through one call drops that to a single
    /// parse/serialize pair (O(1) document cycles).
    /// </summary>
    public WriteResult ModifyStartValues(
        string xml,
        IReadOnlyList<(MemberNode Member, string Value)> edits)
    {
        var doc = XDocument.Parse(xml);
        var ctx = new WriteContext(doc, SimaticMLNamespaces.Detect(doc));
        var changes = new List<ValueChange>();
        var errors = new List<string>();

        foreach (var (member, value) in edits)
            ApplyOne(ctx, member, value, changes, errors);

        return new WriteResult(doc.ToString(), changes, errors);
    }

    /// <summary>
    /// Applies a single member's start-value change against an already-parsed
    /// document. Shared by both <see cref="ModifyStartValues(string,
    /// IReadOnlyList{MemberNode}, string)"/> and the batch overload so the
    /// parse/serialize cost is paid by the caller, not per member.
    /// </summary>
    private static void ApplyOne(
        WriteContext ctx,
        MemberNode target,
        string newValue,
        List<ValueChange> changes,
        List<string> errors)
    {
        var location = FindStartValueLocation(ctx, target.Path);
        if (location == null)
        {
            errors.Add($"Member not found in XML: {target.Path}");
            return;
        }

        if (string.IsNullOrWhiteSpace(newValue))
        {
            var oldCleared = location.ClearStartValue(ctx);
            // ClearStartValue returns null only when there was no
            // <StartValue> element to remove → a genuine no-op. Don't
            // record a phantom ValueChange: it would be counted toward
            // the daily quota and written to the audit log for a write
            // that changed nothing (bulk-clear over a scope where some
            // members have no explicit start value hits this).
            if (oldCleared != null)
                changes.Add(new ValueChange(target.Path, target.Datatype, oldCleared, ""));
            return;
        }

        var startValueElement = location.GetOrCreateStartValue(ctx);

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
        var ctx = new WriteContext(doc, ns);

        // Auto-detect language from existing comments in the XML
        var effectiveLanguage = language ?? DetectLanguage(doc, ns);

        for (int i = 0; i < targetMembers.Count; i++)
        {
            var memberElement = FindMemberElement(ctx, targetMembers[i].Path);
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
        var ctx = new WriteContext(doc, SimaticMLNamespaces.Detect(doc));

        foreach (var member in membersToRemove)
        {
            var element = FindMemberElement(ctx, member.Path);
            element?.Remove();
        }

        return doc.ToString();
    }

    private static XElement? FindMemberElement(WriteContext ctx, string path)
    {
        var location = FindStartValueLocation(ctx, path);
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
    private static StartValueLocation? FindStartValueLocation(WriteContext ctx, string path)
    {
        var ns = ctx.Ns;
        var tokens = TokenizePath(path);
        if (tokens.Count == 0) return null;

        // Cached once per document (#159 H1): the Static-section lookup used
        // to re-run doc.Descendants(...) on every edit — O(n) descendant
        // walks for n edits. WriteContext memoises it so the batch pays the
        // traversal once.
        var sections = ctx.StaticSection;
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
    /// Per-document write state shared across every member in one
    /// <see cref="ModifyStartValues(string, IReadOnlyList{MemberNode}, string)"/>
    /// / batch call. Memoises the two lookups that used to be re-paid per
    /// edit and turned bulk Apply quadratic (#159 H1 + H2):
    /// the Static-section element, and a per-Member <c>Path → Subelement</c>
    /// index. Lives only for the duration of one call against one parsed
    /// <see cref="XDocument"/>.
    /// </summary>
    private sealed class WriteContext
    {
        private readonly Dictionary<XElement, Dictionary<string, XElement>> _subIndex = new();
        private XElement? _staticSection;
        private bool _staticResolved;

        public WriteContext(XDocument doc, XNamespace ns)
        {
            Doc = doc;
            Ns = ns;
        }

        public XDocument Doc { get; }
        public XNamespace Ns { get; }

        /// <summary>
        /// The first <c>Section Name="Static"</c> in the document, resolved
        /// once. Mirrors the original
        /// <c>doc.Descendants(ns + Section).FirstOrDefault(...)</c> but pays
        /// the descendant walk a single time per batch instead of per edit.
        /// </summary>
        public XElement? StaticSection
        {
            get
            {
                if (!_staticResolved)
                {
                    _staticSection = Doc.Descendants(Ns + SE.Section)
                        .FirstOrDefault(s => s.Attribute(SE.Name)?.Value == "Static");
                    _staticResolved = true;
                }
                return _staticSection;
            }
        }

        /// <summary>
        /// Lazily-built <c>Path attribute → &lt;Subelement&gt;</c> map for a
        /// given array Member element. Built once per Member (O(k)); every
        /// subsequent index access on the same Member is O(1). Writers that
        /// add or prune a Subelement keep this map in sync.
        /// </summary>
        public Dictionary<string, XElement> SubelementIndex(XElement member)
        {
            if (!_subIndex.TryGetValue(member, out var index))
            {
                index = new Dictionary<string, XElement>(StringComparer.Ordinal);
                foreach (var sub in LocalElements(member, SE.Subelement))
                {
                    var path = sub.Attribute(SE.Path)?.Value;
                    // First wins, matching the original FirstOrDefault scan.
                    if (path != null && !index.ContainsKey(path))
                        index[path] = sub;
                }
                _subIndex[member] = index;
            }
            return index;
        }
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

        public XElement GetOrCreateStartValue(WriteContext ctx)
        {
            var ns = ctx.Ns;
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
            // #159 H2: a linear FirstOrDefault scan over the Member's
            // Subelements was O(k) per edit, so writing all k elements of a
            // k-element array cost O(k^2). WriteContext pre-indexes the
            // Subelements by Path once per Member; this is now an O(1) lookup.
            var index = ctx.SubelementIndex(Member);
            if (!index.TryGetValue(SubelementPath, out var sub))
            {
                sub = new XElement(ns + SE.Subelement, new XAttribute(SE.Path, SubelementPath));
                Member.Add(sub);
                index[SubelementPath] = sub;
            }

            var inner = LocalElement(sub, SE.StartValue);
            if (inner == null)
            {
                inner = new XElement(ns + SE.StartValue);
                sub.Add(inner);
            }
            return inner;
        }

        /// <summary>
        /// Clears a member's start value by REMOVING the &lt;StartValue&gt; element
        /// (the SimaticML representation of "no explicit start value → revert to
        /// default"). Emitting an empty &lt;StartValue&gt;&lt;/StartValue&gt; is
        /// invalid for re-import (issue #142). For a directly-declared DB member
        /// TIA then falls back to the type default; for a member inside a UDT
        /// instance it falls back to the UDT-defined default — both are expressed
        /// by the same "element absent" XML, so no per-member-kind branching is
        /// needed here. When the value lived under a &lt;Subelement&gt; (array /
        /// per-instance override) the now-childless &lt;Subelement&gt; is pruned
        /// too. Returns the prior value, or null if there was nothing to clear.
        /// </summary>
        public string? ClearStartValue(WriteContext ctx)
        {
            var ns = ctx.Ns;
            XElement? sv;
            XElement? owningSubelement = null;
            Dictionary<string, XElement>? index = null;

            if (SubelementPath == null)
            {
                sv = Member.Element(ns + SE.StartValue);
            }
            else
            {
                // #159 H2: O(1) Subelement lookup via the per-Member index
                // instead of a linear FirstOrDefault scan per cleared edit.
                index = ctx.SubelementIndex(Member);
                index.TryGetValue(SubelementPath, out owningSubelement);
                sv = owningSubelement == null ? null : LocalElement(owningSubelement, SE.StartValue);
            }

            if (sv == null) return null;

            var prior = sv.Attribute(SE.ConstantName)?.Value?.Trim('"') ?? sv.Value;
            sv.Remove();

            // Prune a Subelement that only existed to carry this StartValue. Keep
            // it if it still holds other content (e.g. a per-instance <Comment>).
            if (owningSubelement != null && !owningSubelement.Elements().Any())
            {
                owningSubelement.Remove();
                // Keep the index consistent so a later create for the same
                // path in this batch re-creates the Subelement instead of
                // returning the now-detached element.
                index?.Remove(SubelementPath!);
            }

            return prior;
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
