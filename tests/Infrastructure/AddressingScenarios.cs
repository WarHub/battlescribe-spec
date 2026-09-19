using BattleScribeSpec.Roster;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Roster steps that name something the roster does not have — a selection already removed, a
/// force already gone, a selection filed under a force it is not in, a cost type the game system
/// never declared — each under <c>expectFailure: true</c>, and each run through every lane's real
/// adapter. The one correct outcome, on every engine, is that the step fails as an
/// <see cref="ActionFailureKind.Address"/> failure: the declaration is NOT satisfied, and the run
/// says the adapter could not resolve an id the spec named.
/// <para>
/// <b>Harness tests, not specs.</b> This is the "invalid index / invalid id" half of #25, which the
/// issue moved out of <c>specs/</c> on purpose: an unresolvable id fails in the adapter's own lookup
/// before any engine is consulted, so a spec asserting one would be asserting the harness — and, if
/// it could pass, would make its own typo pass. What CAN be asserted is that every adapter reports
/// it as what it is, and that is what these check.
/// </para>
/// <para>
/// <b>It was not true when written.</b> Only the in-process BattleScribe adapter declared its lookup
/// misses. The NewRecruit store-direct adapter threw a plain <see cref="InvalidOperationException"/>
/// from every JS lookup, the BattleScribe desktop driver returned every agent error as an
/// <c>AgentException</c>, and the classifier's remainder rule reads both as the ENGINE refusing — so
/// on those two lanes each scenario below passed its <c>expectFailure</c>. The NewRecruit UI driver
/// never passed one, but reported them as capability gaps and timeouts, and deselected a selection
/// from whichever force held it regardless of the force the spec named.
/// </para>
/// </summary>
public static class AddressingScenarios
{
    /// <summary>The substring <see cref="ExpectFailure.Explain"/> uses for an addressing failure.</summary>
    public const string AddressVerdict = "could not resolve an id this spec named";

    private const string Setup = """
        setup:
          gameSystem:
            id: gs-1
            name: Test System
            costTypes:
              - id: pts
                name: pts
            categoryEntries:
              - id: cat-troops
                name: Troops
            forceEntries:
              - id: fe-1
                name: Detachment
                categoryLinks:
                  - id: cl-fe1-troops
                    targetId: cat-troops
                    name: Troops
              - id: fe-2
                name: Vanguard
                categoryLinks:
                  - id: cl-fe2-troops
                    targetId: cat-troops
                    name: Troops
          catalogues:
            - id: cat-1
              name: Test Catalogue
              gameSystemId: gs-1
              selectionEntries:
                - id: se-squad
                  name: Squad
                  type: unit
                  categoryLinks:
                    - id: cl-squad-troops
                      targetId: cat-troops
                      name: Troops
                      primary: true
                  costs:
                    - name: pts
                      typeId: pts
                      value: 10
                  selectionEntries:
                    - id: se-weapon
                      name: Weapon
                      type: upgrade
                      collective: true
                      costs:
                        - name: pts
                          typeId: pts
                          value: 5
        """;

    /// <summary>Scenario name → its steps. Every scenario's last step is the one under test.</summary>
    private static readonly Dictionary<string, string> Steps = new(StringComparer.Ordinal)
    {
        ["deselect-removed-selection"] = """
            - action: addForce
              id: add-detachment
              forceEntryId: fe-1
            - action: selectEntry
              id: select-squad
              forceId: ${{ steps.add-detachment.forceId }}
              entryId: se-squad
            - action: deselectSelection
              forceId: ${{ steps.add-detachment.forceId }}
              selectionId: ${{ steps.select-squad.selectionId }}
            - action: deselectSelection
              forceId: ${{ steps.add-detachment.forceId }}
              selectionId: ${{ steps.select-squad.selectionId }}
              expectFailure: true
            """,

        ["select-into-removed-force"] = """
            - action: addForce
              id: add-detachment
              forceEntryId: fe-1
            - action: addForce
              forceEntryId: fe-2
            - action: removeForce
              forceId: ${{ steps.add-detachment.forceId }}
            - action: selectEntry
              forceId: ${{ steps.add-detachment.forceId }}
              entryId: se-squad
              expectFailure: true
            """,

        ["remove-removed-force"] = """
            - action: addForce
              id: add-detachment
              forceEntryId: fe-1
            - action: addForce
              forceEntryId: fe-2
            - action: removeForce
              forceId: ${{ steps.add-detachment.forceId }}
            - action: removeForce
              forceId: ${{ steps.add-detachment.forceId }}
              expectFailure: true
            """,

        ["child-under-removed-parent"] = """
            - action: addForce
              id: add-detachment
              forceEntryId: fe-1
            - action: selectEntry
              id: select-squad
              forceId: ${{ steps.add-detachment.forceId }}
              entryId: se-squad
            - action: deselectSelection
              forceId: ${{ steps.add-detachment.forceId }}
              selectionId: ${{ steps.select-squad.selectionId }}
            - action: selectChildEntry
              forceId: ${{ steps.add-detachment.forceId }}
              selectionId: ${{ steps.select-squad.selectionId }}
              entryId: se-weapon
              expectFailure: true
            """,

        ["count-removed-selection"] = """
            - action: addForce
              id: add-detachment
              forceEntryId: fe-1
            - action: selectEntry
              id: select-squad
              forceId: ${{ steps.add-detachment.forceId }}
              entryId: se-squad
            - action: selectChildEntry
              id: select-weapon
              forceId: ${{ steps.add-detachment.forceId }}
              selectionId: ${{ steps.select-squad.selectionId }}
              entryId: se-weapon
            - action: deselectSelection
              forceId: ${{ steps.add-detachment.forceId }}
              selectionId: ${{ steps.select-squad.selectionId }}
            - action: setSelectionCount
              forceId: ${{ steps.add-detachment.forceId }}
              selectionId: ${{ steps.select-weapon.selectionId }}
              count: 2
              expectFailure: true
            """,

        // The selection exists — in the OTHER force. A driver that looks a selection up by its id
        // alone finds it and acts on it; the spec named a different force, so nothing it names is
        // there.
        ["selection-named-in-other-force"] = """
            - action: addForce
              id: add-detachment
              forceEntryId: fe-1
            - action: addForce
              id: add-vanguard
              forceEntryId: fe-2
            - action: selectEntry
              id: select-squad
              forceId: ${{ steps.add-detachment.forceId }}
              entryId: se-squad
            - action: deselectSelection
              forceId: ${{ steps.add-vanguard.forceId }}
              selectionId: ${{ steps.select-squad.selectionId }}
              expectFailure: true
            """,

        ["unknown-cost-type"] = """
            - action: addForce
              forceEntryId: fe-1
            - action: setCostLimit
              costTypeId: ct-not-declared
              value: 100
              expectFailure: true
            """,
    };

    /// <summary>Every scenario name, for a theory.</summary>
    public static TheoryData<string> Names()
    {
        var data = new TheoryData<string>();
        foreach (var name in Steps.Keys)
        {
            data.Add(name);
        }

        return data;
    }

    /// <summary>Every scenario name, for a lane that runs them in one test.</summary>
    public static IReadOnlyCollection<string> All => Steps.Keys;

    /// <summary>The scenario as a spec.</summary>
    public static SpecFile Load(string name)
    {
        var steps = string.Join(
            "\n",
            Steps[name].Split('\n').Select(line => line.Length == 0 ? line : "  " + line));
        return SpecLoader.LoadFromYaml(
            $"id: addressing-{name}\ncategory: addressing\ndescription: harness\n\n{Setup}\n\nsteps:\n{steps}\n");
    }

    /// <summary>
    /// Why <paramref name="result"/> is not the one correct outcome, or null when it is: exactly one
    /// failure, and that failure the addressing verdict. A PASS means the lane accepted a stale id as
    /// an engine refusal — the defect these scenarios exist to keep out.
    /// </summary>
    public static string? Judge(string name, SpecResult result)
    {
        if (result.Passed)
        {
            return $"{name}: expectFailure was SATISFIED — the lane reported a lookup miss as an engine "
                + "refusal, so a spec naming an id that is not there would pass.";
        }

        if (result.Failures.Count != 1 || !result.Failures[0].Contains(AddressVerdict, StringComparison.Ordinal))
        {
            return $"{name}: expected one addressing failure, got {result.Failures.Count}:\n  "
                + string.Join("\n  ", result.Failures);
        }

        return null;
    }
}
