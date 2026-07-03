using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace BattleScribeSpec.NewRecruit;

/// <summary>
/// NewRecruit serializes its <c>.ros</c> export as a single line — faithful to its engine, but an
/// unreadable one-liner that produces whole-line git diffs. As a NewRecruit engine-adapter feature we
/// re-indent that output to a canonical multi-line layout before returning it, so the byte-compare
/// snapshots stay diffable. Only insignificant inter-element whitespace changes: attribute order,
/// self-closing empties, the XML declaration (incl. <c>standalone</c>), the default namespace, and all
/// values are preserved, so the snapshot still captures NewRecruit's real serialization.
/// </summary>
public static class NrRosterXml
{
    /// <summary>Re-indent NewRecruit's single-line roster XML to a 2-space, LF-delimited layout.</summary>
    public static string Pretty(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return xml;
        }

        // Parse without preserving whitespace so NR's existing single-line layout is dropped, then
        // re-emit with indentation. LoadOptions.None keeps attribute order and element order intact.
        var doc = XDocument.Parse(xml, LoadOptions.None);

        // Write the body with the declaration omitted, then prepend the ORIGINAL declaration verbatim:
        // an XmlWriter over a StringBuilder would otherwise stamp encoding="utf-16" (the buffer's
        // encoding), corrupting NR's "UTF-8" declaration.
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            NewLineChars = "\n",
            OmitXmlDeclaration = true,
        };

        var sb = new StringBuilder();
        using (var writer = XmlWriter.Create(sb, settings))
        {
            doc.Save(writer);
        }

        return doc.Declaration is { } decl ? decl + "\n" + sb : sb.ToString();
    }
}
