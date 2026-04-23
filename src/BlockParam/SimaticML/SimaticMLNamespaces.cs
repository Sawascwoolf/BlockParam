using System.Xml.Linq;

namespace BlockParam.SimaticML;

/// <summary>
/// SimaticML namespace constants and auto-detection.
/// The Interface/Sections namespace version varies by TIA Portal version.
/// </summary>
public static class SimaticMLNamespaces
{
    private const string BaseUri = "http://www.siemens.com/automation/Openness/SW/Interface/";

    public static readonly XNamespace V2 = BaseUri + "v2";
    public static readonly XNamespace V3 = BaseUri + "v3";
    public static readonly XNamespace V4 = BaseUri + "v4";
    public static readonly XNamespace V5 = BaseUri + "v5";

    private static readonly XNamespace[] KnownVersions = { V5, V4, V3, V2 };

    /// <summary>
    /// Detects the Interface namespace used in a SimaticML document.
    /// Searches for the Sections element and reads its xmlns attribute.
    /// </summary>
    public static XNamespace Detect(XDocument doc)
    {
        var sectionsElement = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == SimaticMLElements.Sections);

        if (sectionsElement == null)
            throw new SimaticMLParseException("No <Sections> element found in XML document.");

        var ns = sectionsElement.Name.Namespace;

        if (ns == XNamespace.None)
            throw new SimaticMLParseException("The <Sections> element has no namespace defined.");

        // Validate it's a known version
        if (!KnownVersions.Contains(ns))
            throw new SimaticMLParseException(
                $"Unknown SimaticML Interface namespace: '{ns}'. " +
                $"Known versions: {string.Join(", ", KnownVersions.Select(n => n.NamespaceName))}");

        return ns;
    }
}
