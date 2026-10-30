using System.Xml;

namespace BattleScribeSpec.Scenario;

/// <summary>
/// The scenario's stand-in for BattleScribe's data folder: the files the roster editor reads when it
/// starts or opens a roster, keyed by root id. Written only by the data editor — its own export of every
/// setup file when the run starts, then whatever each <c>saveData</c> writes.
/// <para>
/// Files are named <c>{id}.gst</c> / <c>{id}.cat</c> from the XML's own root, never from what the spec
/// thinks it saved: the roster editor reads what the data editor wrote, and the root is where that says
/// what it is. Order is first-write order, so the game system and catalogues keep the order setup gave
/// them however often they are saved.
/// </para>
/// </summary>
internal sealed class ScenarioDataDirectory
{
    private readonly List<string> _order = [];
    private readonly Dictionary<string, (string FileName, string Content, bool IsCatalogue)> _files = new(StringComparer.Ordinal);

    /// <summary>Every file, in first-write order, as <see cref="Roster.IRosterEngine.SetupFromFiles"/> takes them.</summary>
    public IReadOnlyList<(string FileName, string Content)> Files
        => [.. _order.Select(id => (_files[id].FileName, _files[id].Content))];

    /// <summary>The catalogues' root ids, in first-write order — what <c>addForce</c> may default to.</summary>
    public IReadOnlyList<string> CatalogueIds => [.. _order.Where(id => _files[id].IsCatalogue)];

    /// <summary>
    /// Store <paramref name="xml"/> under the id its root element carries, replacing an earlier file with
    /// that id in place.
    /// </summary>
    /// <param name="xml">The file as the data editor wrote it.</param>
    /// <param name="replaces">
    /// The id of the file the editor opened, when it saves that file. An edit can change a file's own id,
    /// and the editor still writes back to the file it read, so the old id's entry gives way to the new one
    /// in its place — rather than staying beside it, where the roster editor would read two catalogues, or
    /// two game systems, where the app has one.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// The root is neither <c>gameSystem</c> nor <c>catalogue</c>, or has no id; or a save renamed its file
    /// to the id another file already has.
    /// </exception>
    public void Write(string xml, string? replaces = null)
    {
        var (root, id) = ReadRoot(xml);
        var isCatalogue = root switch
        {
            "catalogue" => true,
            "gameSystem" => false,
            _ => throw new InvalidOperationException(
                $"The data editor wrote a file whose root element is <{root}>; a data file is a <gameSystem> or a <catalogue>."),
        };

        if (string.IsNullOrEmpty(id))
        {
            throw new InvalidOperationException($"The data editor wrote a <{root}> with no id.");
        }

        if (replaces is not null && replaces != id && _files.ContainsKey(replaces))
        {
            if (_files.ContainsKey(id))
            {
                throw new InvalidOperationException(
                    $"The data editor saved '{replaces}' as '{id}', the id another file in the data directory already has.");
            }

            _files.Remove(replaces);
            _order[_order.IndexOf(replaces)] = id;
        }
        else if (!_files.ContainsKey(id))
        {
            _order.Add(id);
        }

        _files[id] = ($"{id}.{(isCatalogue ? "cat" : "gst")}", xml, isCatalogue);
    }

    private static (string Root, string? Id) ReadRoot(string xml)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            reader.MoveToContent();
            return (reader.LocalName, reader.GetAttribute("id"));
        }
        catch (XmlException ex)
        {
            throw new InvalidOperationException($"The data editor wrote a file that is not well-formed XML: {ex.Message}", ex);
        }
    }
}
