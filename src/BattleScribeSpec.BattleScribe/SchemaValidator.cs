using System.Xml;
using System.Xml.Schema;

namespace BattleScribeSpec;

/// <summary>
/// Validates BattleScribe XML documents against the v2.03 XSD schema.
/// </summary>
public static class SchemaValidator
{
    private static readonly Lazy<XmlSchemaSet> SchemaSet = new(LoadSchemas);

    /// <summary>
    /// Path to the XSD schema files. Set before first use if not using embedded resource.
    /// </summary>
    public static string SchemaDirectory { get; set; } =
        Path.GetFullPath(Path.Combine(FindRepoRoot() ?? AppContext.BaseDirectory,
            ".deps", "wham", "src", "dataformat", "xml", "schema", "latest"));

    private static string? FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "BattleScribeSpec.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    private static XmlSchemaSet LoadSchemas()
    {
        var schemaSet = new XmlSchemaSet();
        var catalogueXsd = Path.Combine(SchemaDirectory, "Catalogue.xsd");
        if (!File.Exists(catalogueXsd))
        {
            throw new FileNotFoundException(
                $"BattleScribe schema file not found: {catalogueXsd}. Set {nameof(SchemaDirectory)} before validation.");
        }

        schemaSet.Add(null, catalogueXsd);
        schemaSet.Compile();
        return schemaSet;
    }

    /// <summary>
    /// Validates an XML string against the BattleScribe XSD schema.
    /// Returns a list of validation errors (empty = valid).
    /// </summary>
    public static List<string> ValidateXml(string xml)
    {
        var errors = new List<string>();
        var settings = new XmlReaderSettings
        {
            ValidationType = ValidationType.Schema,
            Schemas = SchemaSet.Value
        };
        settings.ValidationEventHandler += (_, e) => errors.Add(e.Message);

        using var reader = XmlReader.Create(new StringReader(xml), settings);
        while (reader.Read())
        { }
        return errors;
    }

    /// <summary>
    /// Validates an XML file against the BattleScribe XSD schema.
    /// </summary>
    public static List<string> ValidateFile(string filePath)
    {
        var xml = File.ReadAllText(filePath);
        return ValidateXml(xml);
    }
}
