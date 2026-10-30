using System.Diagnostics.CodeAnalysis;
using System.Xml;
using System.Xml.Linq;

namespace BattleScribeSpec.NrRosterUiDriver;

/// <summary>
/// What the NR UI driver knows about the game data it handed NewRecruit: the game system, the
/// catalogues, and the names its UI actions click entries by. Built from the <c>.gst</c>/<c>.cat</c>
/// XML NR is given, on both setup paths — <c>Setup</c> generates that XML from the Protocol model,
/// <c>SetupFromFiles</c> receives it — so the driver has one reading of the data, and it is the
/// reading of the bytes NR actually loaded.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the XML and not the Protocol model.</b> This used to be built by walking the Protocol
/// model, and <c>SetupFromFiles</c> had no model to walk — so it recorded nothing, its first
/// <c>addForce</c> threw "Setup must be called before any mutation.", and every entry it would have
/// clicked had no name to click by. Two indexers (one per path) would be two answers to one
/// question; the files are what both paths have in common.
/// </para>
/// <para>
/// <b>Every entry element is indexed, wherever it sits.</b> The model walk registered containers
/// one by one, and missed the game system's shared entries until a spec failed on it
/// (docs/nr-ui-roster-coverage.md §3b): an unregistered entry falls back to its raw id, and the
/// driver hunts for a label "se-shared-weapon" in a panel that says "Shared Weapon". Indexing every
/// <c>selectionEntry</c>, <c>selectionEntryGroup</c> and <c>entryLink</c> in the document has no
/// container to forget.
/// </para>
/// </remarks>
internal sealed class NrUiDataIndex
{
    private readonly Dictionary<string, string> _forceEntryNames;
    private readonly Dictionary<string, string> _entryNames;

    private NrUiDataIndex(
        string gameSystemId,
        string gameSystemName,
        IReadOnlyList<NrUiCatalogue> catalogues,
        IReadOnlyList<(string FileName, string Content)> loadOrder,
        Dictionary<string, string> forceEntryNames,
        Dictionary<string, string> entryNames)
    {
        GameSystemId = gameSystemId;
        GameSystemName = gameSystemName;
        Catalogues = catalogues;
        LoadOrder = loadOrder;
        _forceEntryNames = forceEntryNames;
        _entryNames = entryNames;
    }

    public string GameSystemId { get; }

    public string GameSystemName { get; }

    /// <summary>The catalogues, in the order NR is handed them (see <see cref="LoadOrder"/>).</summary>
    public IReadOnlyList<NrUiCatalogue> Catalogues { get; }

    /// <summary>
    /// The files in the order NR should load them: the game system, then every non-library
    /// catalogue, then the library ones, then anything that is not a data file, each group in the
    /// order it was given.
    /// </summary>
    /// <remarks>
    /// Non-library catalogues before library ones so NR uses the primary catalogue as the default
    /// when it associates a force entry with a book. Non-data files are kept — a <c>dataSource</c>
    /// hands over a repository's README and licence beside its data, and NR's folder loader is the
    /// one that decides what it reads.
    /// </remarks>
    public IReadOnlyList<(string FileName, string Content)> LoadOrder { get; }

    /// <summary>The visible name of force entry <paramref name="forceEntryId"/>, or the id itself.</summary>
    public string ForceEntryName(string forceEntryId)
        => _forceEntryNames.GetValueOrDefault(forceEntryId, forceEntryId);

    /// <summary>The visible name of entry, group or link <paramref name="id"/>, when the data has one.</summary>
    public bool TryGetEntryName(string id, [NotNullWhen(true)] out string? name)
        => _entryNames.TryGetValue(id, out name);

    /// <summary>
    /// The catalogue the Create List dialog should pick: <paramref name="catalogueId"/>'s when it is
    /// one of ours, otherwise the first non-library catalogue — null when there is neither.
    /// </summary>
    public string? CatalogueNameFor(string? catalogueId)
        => (catalogueId is null ? null : Catalogues.FirstOrDefault(c => c.Id == catalogueId)?.Name)
            ?? Catalogues.FirstOrDefault(c => !c.Library)?.Name;

    /// <summary>
    /// Reads <paramref name="files"/>. Throws <see cref="InvalidDataException"/>, naming the file,
    /// when a data file is not well-formed XML or the set does not hold exactly one game system.
    /// </summary>
    public static NrUiDataIndex FromFiles(IReadOnlyList<(string FileName, string Content)> files)
    {
        (string FileName, string Content)? gameSystemFile = null;
        string? gameSystemId = null;
        string? gameSystemName = null;
        var primaryCatalogues = new List<((string, string) File, NrUiCatalogue Catalogue)>();
        var libraryCatalogues = new List<((string, string) File, NrUiCatalogue Catalogue)>();
        var otherFiles = new List<(string, string)>();
        var roots = new List<XElement>();

        foreach (var file in files)
        {
            if (!IsDataFile(file.FileName))
            {
                otherFiles.Add(file);
                continue;
            }

            XElement root;
            try
            {
                root = XDocument.Parse(file.Content).Root
                    ?? throw new InvalidDataException($"'{file.FileName}' has no root element.");
            }
            catch (XmlException ex)
            {
                throw new InvalidDataException($"'{file.FileName}' is not well-formed XML: {ex.Message}", ex);
            }

            var id = (string?)root.Attribute("id");
            var name = (string?)root.Attribute("name");
            if (string.IsNullOrEmpty(id))
            {
                throw new InvalidDataException(
                    $"'{file.FileName}' has no id on its <{root.Name.LocalName}> root element.");
            }

            switch (root.Name.LocalName)
            {
                case "gameSystem":
                    if (gameSystemFile is { } first)
                    {
                        throw new InvalidDataException(
                            $"two game systems among the files ('{first.FileName}' and '{file.FileName}'); "
                            + "a roster is built on one.");
                    }

                    gameSystemFile = file;
                    gameSystemId = id;
                    gameSystemName = name ?? id;
                    break;

                case "catalogue":
                    var catalogue = new NrUiCatalogue(id, name ?? id, (bool?)root.Attribute("library") ?? false);
                    (catalogue.Library ? libraryCatalogues : primaryCatalogues).Add((file, catalogue));
                    break;

                default:
                    throw new InvalidDataException(
                        $"'{file.FileName}' has a <{root.Name.LocalName}> root; a .gst holds a <gameSystem> "
                        + "and a .cat a <catalogue>.");
            }

            roots.Add(root);
        }

        if (gameSystemFile is not { } gameSystem || gameSystemId is null)
        {
            throw new InvalidDataException("no game system (.gst) among the files.");
        }

        var (forceEntryNames, entryNames) = IndexNames(roots);
        var catalogues = primaryCatalogues.Concat(libraryCatalogues).ToList();

        return new NrUiDataIndex(
            gameSystemId,
            gameSystemName!,
            [.. catalogues.Select(c => c.Catalogue)],
            [gameSystem, .. catalogues.Select(c => c.File), .. otherFiles],
            forceEntryNames,
            entryNames);
    }

    private static bool IsDataFile(string fileName)
        => fileName.EndsWith(".gst", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".cat", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Force-entry names, and the names of every selection entry, group and entry link.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A link is indexed under its OWN name</b>, falling back to its target's only when the link
    /// is unnamed, because NR's UI shows the link's name. This is the opposite of the BattleScribe
    /// driver (<c>BsUiRosterEngine.ResolveEntryLabel</c>), and the two must not be unified — see
    /// <c>NrRosterUiEngine.ResolveEntryName</c>.
    /// </para>
    /// <para>
    /// Links are indexed after every entry and group, so an unnamed link takes its target's name
    /// wherever in the files the target is declared. (The model walk this replaced registered links
    /// as it met them, so an unnamed link met before its target fell back to the target's raw id.)
    /// </para>
    /// </remarks>
    private static (Dictionary<string, string> ForceEntries, Dictionary<string, string> Entries) IndexNames(
        IReadOnlyList<XElement> roots)
    {
        var forceEntryNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var entryNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var links = new List<(string Id, string? Name, string? TargetId)>();

        foreach (var element in roots.SelectMany(r => r.Descendants()))
        {
            var id = (string?)element.Attribute("id");
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            var name = (string?)element.Attribute("name");
            switch (element.Name.LocalName)
            {
                case "forceEntry":
                    forceEntryNames[id] = name ?? id;
                    break;

                case "selectionEntry":
                case "selectionEntryGroup":
                    entryNames[id] = name ?? id;
                    break;

                case "entryLink":
                    links.Add((id, name, (string?)element.Attribute("targetId")));
                    break;
            }
        }

        foreach (var (id, name, targetId) in links)
        {
            entryNames[id] = !string.IsNullOrEmpty(name)
                ? name
                : targetId is null ? id : entryNames.GetValueOrDefault(targetId, targetId);
        }

        return (forceEntryNames, entryNames);
    }
}

/// <summary>A catalogue the driver handed NR: its id, its name, and whether it is a library.</summary>
internal sealed record NrUiCatalogue(string Id, string Name, bool Library);
