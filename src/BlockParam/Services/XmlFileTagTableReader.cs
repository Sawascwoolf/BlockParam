using System.IO;
using System.Xml.Linq;
using Serilog;
using BlockParam.Models;

namespace BlockParam.Services;

/// <summary>
/// Reads tag table entries from exported SimaticML XML files on disk.
/// Each .xml file in the directory represents one tag table.
/// </summary>
public class XmlFileTagTableReader : ITagTableReader
{
    private static readonly ILogger Log = Serilog.Log.Logger;
    private readonly string _directory;
    private readonly string _commentCulture;

    public XmlFileTagTableReader(string directory, string commentCulture = "de-DE")
    {
        _directory = directory;
        _commentCulture = commentCulture;
    }

    public IReadOnlyList<TagTableEntry> ReadTagTable(string tableName)
    {
        var filePath = Path.Combine(_directory, $"{tableName}.xml");
        if (!File.Exists(filePath))
        {
            return Array.Empty<TagTableEntry>();
        }

        try
        {
            var doc = XDocument.Load(filePath);
            var constants = doc.Descendants("SW.Tags.PlcUserConstant");
            var entries = new List<TagTableEntry>();

            foreach (var constant in constants)
            {
                var attrs = constant.Element("AttributeList");
                if (attrs == null) continue;

                var name = attrs.Element("Name")?.Value;
                var value = attrs.Element("Value")?.Value;
                var dataType = attrs.Element("DataTypeName")?.Value ?? "Int";

                if (string.IsNullOrEmpty(name) || value == null) continue;

                // Read comments in all available cultures
                var comments = new Dictionary<string, string>();
                foreach (var textItem in constant.Descendants("MultilingualTextItem"))
                {
                    var culture = textItem.Element("AttributeList")?.Element("Culture")?.Value;
                    var text = textItem.Element("AttributeList")?.Element("Text")?.Value;
                    if (culture != null && !string.IsNullOrEmpty(text))
                        comments[culture] = text;
                }

                // Default comment: preferred culture, or first available
                var defaultComment = comments.TryGetValue(_commentCulture, out var dc) ? dc
                    : comments.Values.FirstOrDefault();

                entries.Add(new TagTableEntry(name, value, dataType, defaultComment, comments));
            }

            return entries;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read tag table file: {Path}", filePath);
            return Array.Empty<TagTableEntry>();
        }
    }

    public IReadOnlyList<string> GetTagTableNames()
    {
        if (!Directory.Exists(_directory))
            return Array.Empty<string>();

        return Directory.GetFiles(_directory, "*.xml")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n != null)
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
