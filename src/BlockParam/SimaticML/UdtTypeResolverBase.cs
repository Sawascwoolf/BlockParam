using System.IO;

namespace BlockParam.SimaticML;

/// <summary>
/// Shared directory walker for UDT type-definition resolvers. Subclasses
/// implement <see cref="LoadFromXml"/>; per-file parse failures are swallowed
/// so an unreadable export does not abort the whole load. Missing types are
/// surfaced upstream via the parser's UnresolvedUdts set.
/// </summary>
public abstract class UdtTypeResolverBase
{
    /// <summary>Parse a single UDT type-definition XML.</summary>
    public abstract void LoadFromXml(string xml);

    /// <summary>Load every <c>*.xml</c> file in the given directory. Missing dir is a no-op.</summary>
    public void LoadFromDirectory(string udtExportDir)
    {
        if (!Directory.Exists(udtExportDir)) return;
        foreach (var file in Directory.GetFiles(udtExportDir, "*.xml"))
        {
            try { LoadFromXml(File.ReadAllText(file)); }
            catch { /* swallow; individual failures surface via UnresolvedUdts */ }
        }
    }
}
