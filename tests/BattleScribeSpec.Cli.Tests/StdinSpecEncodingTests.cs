using System.Text.Json;
using BattleScribeSpec.TestSupport;

namespace BattleScribeSpec.Cli.Tests;

/// <summary>
/// Raw UTF-8 through <c>bs-spec run -</c>: a spec whose entry name carries an em dash (U+2014),
/// written to the CLI's stdin as UTF-8, must come back out of the state dump as that same name —
/// on a console whose code page is not UTF-8. This is the end-to-end half of <c>Utf8Stdio</c>;
/// <c>RedirectedStdioEncodingTests</c> holds the structural half.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why two dumps.</b> On a single-byte code page such as 437, decoding stdin wrongly and
/// encoding stdout wrongly cancel out: UTF-8's <c>E2 80 94</c> is read as the three characters
/// <c>ΓÇö</c>, which 437 writes back as exactly <c>E2 80 94</c>, so a tree dump alone shows the em
/// dash before the fix as well as after it. The JSON dump cannot be fooled that way — System.Text.Json
/// escapes non-ASCII, so its bytes are ASCII whatever stdout's encoding is, and the name it carries
/// is exactly what the CLI decoded from stdin. Together: the JSON dump pins the stdin half, and the
/// tree dump (raw text) pins the stdout half — with stdin fixed and stdout not, 437 best-fits the em
/// dash to <c>-</c> on the way out.
/// </para>
/// <para>
/// <b>Falsifiable on Windows, where the defect lives.</b> The child runs on a console of its own set
/// to code page 437 (<see cref="LegacyCodePageConsole"/>). Remove
/// <c>Utf8Stdio.UseForRedirectedStreams()</c> from <c>Program.Main</c> and the JSON test reads
/// <c>"Unit ΓÇö Alpha"</c>; remove only its <c>Console.SetOut</c> and the tree test reads
/// <c>"Unit - Alpha"</c>. Off Windows the console encoding follows the locale and is UTF-8 here, so
/// both pass trivially there — including in every CI job, which all run on Linux.
/// </para>
/// </remarks>
public sealed class StdinSpecEncodingTests
{
    private const string UnitName = "Unit \u2014 Alpha"; // an em dash, spelled as an escape so this file's own encoding cannot matter

    private const string Spec = $$$"""
        id: stdin-utf8
        category: protocol
        description: Raw UTF-8 through the stdin spec path

        setup:
          gameSystem:
            forceEntries:
              - id: fe-1
                name: Patrol
          catalogues:
            - id: cat-1
              name: Catalogue
              selectionEntries:
                - id: se-1
                  name: {{{UnitName}}}
                  type: unit

        steps:
          - action: addForce
            id: add-patrol
            forceEntryId: fe-1
            catalogueId: cat-1

          - action: selectEntry
            forceId: ${{ steps.add-patrol.forceId }}
            entryId: se-1

          - expectedState:
              forces:
                - name: Patrol
                  selections:
                    - name: {{{UnitName}}}
        """;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RunStdin_RawUtf8Name_IsDecodedIntact_JsonDump()
    {
        var (exitCode, stdOut, stdErr) = await CliProcess.RunAsync(
            ["run", "-", "--engine", "battlescribe", "--roster", "--json"], Spec, legacyCodePageConsole: true);

        Assert.True(exitCode == 0, $"exit code {exitCode}; stderr:\n{stdErr}");
        using var dump = JsonDocument.Parse(stdOut);
        var name = dump.RootElement
            .GetProperty("roster").GetProperty("forces")[0]
            .GetProperty("selections")[0].GetProperty("name").GetString();
        Assert.Equal(UnitName, name);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RunStdin_RawUtf8Name_ComesBackIntact_TreeDump()
    {
        var (exitCode, stdOut, stdErr) = await CliProcess.RunAsync(
            ["run", "-", "--engine", "battlescribe", "--roster"], Spec, legacyCodePageConsole: true);

        Assert.True(exitCode == 0, $"exit code {exitCode}; stderr:\n{stdErr}");
        Assert.True(
            stdOut.Contains($"\"{UnitName}\"", StringComparison.Ordinal),
            $"the tree dump does not carry \"{UnitName}\" intact:\n{stdOut}");
    }
}
