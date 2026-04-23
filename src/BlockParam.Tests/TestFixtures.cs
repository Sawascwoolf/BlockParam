using System.Reflection;

namespace BlockParam.Tests;

/// <summary>
/// Helper to load embedded XML/JSON test fixtures.
/// </summary>
public static class TestFixtures
{
    private static readonly Assembly Assembly = typeof(TestFixtures).Assembly;

    public static string LoadXml(string fixtureName)
    {
        var resourceName = $"BlockParam.Tests.Fixtures.{fixtureName}";
        using var stream = Assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Fixture not found: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Load all UDT fixtures shipped under Fixtures/udts/ as (name, xml) pairs.
    /// </summary>
    public static IEnumerable<(string Name, string Xml)> LoadUdtFixtures()
    {
        const string prefix = "BlockParam.Tests.Fixtures.udts.";
        foreach (var resourceName in Assembly.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(prefix) || !resourceName.EndsWith(".xml"))
                continue;
            using var stream = Assembly.GetManifestResourceStream(resourceName)!;
            using var reader = new StreamReader(stream);
            yield return (resourceName.Substring(prefix.Length), reader.ReadToEnd());
        }
    }
}
