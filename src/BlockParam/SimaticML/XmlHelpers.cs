using System.Xml.Linq;

namespace BlockParam.SimaticML;

internal static class XmlHelpers
{
    /// <summary>
    /// Namespace-agnostic first-child lookup. SimaticML documents sometimes omit
    /// the Interface namespace on inner elements (varies by TIA version), so
    /// looking up by local name covers both namespaced and bare children.
    /// </summary>
    public static XElement? LocalElement(XElement parent, string localName)
        => parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

    /// <summary>Namespace-agnostic children lookup; see <see cref="LocalElement"/>.</summary>
    public static IEnumerable<XElement> LocalElements(XElement parent, string localName)
        => parent.Elements().Where(e => e.Name.LocalName == localName);
}
