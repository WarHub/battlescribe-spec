namespace BattleScribeSpec.Protocol;

/// <summary>
/// Protocol-level validation and resolution for action parameters.
/// Called by both SpecRunner and AdapterHandler to enforce protocol invariants
/// independent of engine implementation.
/// </summary>
public static class ProtocolValidator
{
    /// <summary>
    /// Resolve the catalogueId for addForce/addChildForce actions.
    /// Auto-resolves when a single catalogue exists; requires explicit ID
    /// when multiple catalogues are in scope.
    /// <para>
    /// Each failure is a <see cref="SpecAddressingException"/>: a catalogue the setup never declared,
    /// or no catalogue named where several could be meant, is the spec failing to say which
    /// catalogue it means — no engine has been asked anything. It used to be a plain
    /// <see cref="InvalidOperationException"/>, which the action classifier reads as an engine
    /// refusal, so <c>catalogueId: cat-typo</c> under <c>expectFailure: true</c> passed on every
    /// engine (#25).
    /// </para>
    /// </summary>
    /// <param name="catalogueId">The catalogueId provided in the action, if any.</param>
    /// <param name="setupCatalogueIds">Catalogue IDs from setup. Empty = unknown (file-based setup).</param>
    /// <returns>The resolved, non-null catalogue ID.</returns>
    public static string ResolveCatalogueId(string? catalogueId, IReadOnlyList<string> setupCatalogueIds)
    {
        if (!string.IsNullOrEmpty(catalogueId))
        {
            if (setupCatalogueIds.Count > 0 && !setupCatalogueIds.Contains(catalogueId))
            {
                throw new SpecAddressingException(
                    $"catalogueId '{catalogueId}' not found in setup catalogues: [{string.Join(", ", setupCatalogueIds)}]");
            }

            return catalogueId;
        }

        return setupCatalogueIds.Count switch
        {
            0 => throw new SpecAddressingException(
                "addForce/addChildForce requires catalogueId (catalogue list unknown from file-based setup)"),
            1 => setupCatalogueIds[0],
            _ => throw new SpecAddressingException(
                $"addForce/addChildForce requires catalogueId when multiple catalogues exist ({setupCatalogueIds.Count} catalogues in setup)"),
        };
    }
}
