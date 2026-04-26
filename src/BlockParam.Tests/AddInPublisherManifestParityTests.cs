using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace BlockParam.Tests;

/// <summary>
/// Guards against drift between the V20 and V21 publisher manifests. The two
/// files must stay byte-identical except for the publisher-namespace xmlns
/// and the <c>AddInVersion</c> token — when permissions/assemblies change in
/// one, the other has to follow. Without this guard, a future PR can add a
/// permission to the V20 manifest, ship, and silently break V21.
/// </summary>
public class AddInPublisherManifestParityTests
{
    private const string V20Xmlns = "http://www.siemens.com/automation/Openness/AddIn/Publisher/V20";
    private const string V21Xmlns = "http://www.siemens.com/automation/Openness/AddIn/Publisher/V21";

    [Fact]
    public void Manifests_DifferOnlyInXmlnsAndAddInVersion()
    {
        var v20 = XDocument.Parse(LoadManifest("addin-publisher-v20.xml"));
        var v21 = XDocument.Parse(LoadManifest("addin-publisher-v21.xml"));

        v20.Root!.Name.NamespaceName.Should().Be(V20Xmlns);
        v21.Root!.Name.NamespaceName.Should().Be(V21Xmlns);

        Normalize(v20, V20Xmlns);
        Normalize(v21, V21Xmlns);

        XNode.DeepEquals(v20, v21).Should().BeTrue(
            "V20 and V21 manifests must stay in sync apart from xmlns and <AddInVersion>; " +
            "if you intentionally diverge them, update this test with the allowed delta.");
    }

    [Fact]
    public void V21Manifest_UsesV21AddInVersionToken()
    {
        var v21 = XDocument.Parse(LoadManifest("addin-publisher-v21.xml"));
        var ns = (XNamespace)V21Xmlns;
        v21.Root!.Element(ns + "AddInVersion")!.Value.Should().Be(
            "V21",
            "Siemens's V21 sample uses the literal token 'V21' — stay aligned with the convention.");
    }

    private static void Normalize(XDocument doc, string xmlns)
    {
        var ns = (XNamespace)xmlns;

        // Strip xmlns by rewriting every element into the empty namespace, so
        // DeepEquals compares structure + content only.
        foreach (var e in doc.Descendants().ToList())
            e.Name = XNamespace.None + e.Name.LocalName;

        var addInVersion = doc.Root!.Element("AddInVersion");
        addInVersion?.Remove();
    }

    private static string LoadManifest(string fileName)
    {
        var resource = $"BlockParam.Tests.Manifests.{fileName}";
        var asm = typeof(AddInPublisherManifestParityTests).Assembly;
        using var stream = asm.GetManifestResourceStream(resource)
            ?? throw new System.IO.FileNotFoundException(
                $"Manifest resource not found: {resource}. " +
                $"Available: {string.Join(", ", asm.GetManifestResourceNames())}");
        using var reader = new System.IO.StreamReader(stream);
        return reader.ReadToEnd();
    }
}
