namespace BattleScribeSpec.Protocol;

/// <summary>
/// Converts between internal Spec models and Protocol wire format types.
/// </summary>
public static class ProtocolConverter
{
    // ===== Spec → Protocol (runner side: sending setup data) =====

    public static SetupCommand ToSetupCommand(GameSystemSpec gs, CatalogueSpec cat) => new()
    {
        GameSystem = ToProtocol(gs),
        Catalogue = ToProtocol(cat),
    };

    public static ProtocolGameSystem ToProtocol(GameSystemSpec gs) => new()
    {
        Id = gs.Id,
        Name = gs.Name,
        CostTypes = gs.CostTypes?.Select(ToProtocol).ToList(),
        ForceEntries = gs.ForceEntries?.Select(ToProtocol).ToList(),
        CategoryEntries = gs.CategoryEntries?.Select(ct => new ProtocolCategoryEntry { Id = ct.Id, Name = ct.Name }).ToList(),
        ProfileTypes = gs.ProfileTypes?.Select(pt => new ProtocolProfileType
        {
            Id = pt.Id, Name = pt.Name,
            CharacteristicTypes = pt.CharacteristicTypes?.Select(ct => new ProtocolCharacteristicType { Id = ct.Id, Name = ct.Name }).ToList(),
        }).ToList(),
    };

    public static ProtocolCatalogue ToProtocol(CatalogueSpec cat) => new()
    {
        Id = cat.Id,
        Name = cat.Name,
        GameSystemId = cat.GameSystemId,
        SelectionEntries = cat.SelectionEntries?.Select(ToProtocol).ToList(),
        SelectionEntryGroups = cat.SelectionEntryGroups?.Select(ToProtocol).ToList(),
        EntryLinks = cat.EntryLinks?.Select(ToProtocol).ToList(),
    };

    static ProtocolCostType ToProtocol(CostTypeSpec ct) => new()
    {
        Id = ct.Id, Name = ct.Name, DefaultCostLimit = ct.DefaultCostLimit,
        Hidden = ct.Hidden, Limit = ct.Limit,
    };

    static ProtocolForceEntry ToProtocol(ForceEntrySpec fe) => new()
    {
        Id = fe.Id, Name = fe.Name,
        CategoryLinks = fe.CategoryLinks?.Select(ToProtocol).ToList(),
        ForceEntries = fe.ForceEntries?.Select(ToProtocol).ToList(),
    };

    static ProtocolSelectionEntry ToProtocol(SelectionEntrySpec se) => new()
    {
        Id = se.Id, Name = se.Name, Type = se.Type, Hidden = se.Hidden,
        Collective = se.Collective, Page = string.IsNullOrEmpty(se.Page) ? null : se.Page,
        Costs = se.Costs?.Select(ToProtocolCostValue).ToList(),
        Constraints = se.Constraints?.Select(ToProtocol).ToList(),
        Modifiers = se.Modifiers?.Select(ToProtocol).ToList(),
        ModifierGroups = se.ModifierGroups?.Select(ToProtocol).ToList(),
        SelectionEntries = se.ChildEntries?.Select(ToProtocol).ToList(),
        SelectionEntryGroups = se.SelectionEntryGroups?.Select(ToProtocol).ToList(),
        EntryLinks = se.EntryLinks?.Select(ToProtocol).ToList(),
        CategoryLinks = se.CategoryLinks?.Select(ToProtocol).ToList(),
        Rules = se.Rules?.Select(ToProtocol).ToList(),
        Profiles = se.Profiles?.Select(ToProtocol).ToList(),
        InfoGroups = se.InfoGroups?.Select(ToProtocol).ToList(),
    };

    static ProtocolSelectionEntryGroup ToProtocol(SelectionEntryGroupSpec seg) => new()
    {
        Id = seg.Id, Name = seg.Name, Hidden = seg.Hidden,
        DefaultSelectionEntryId = string.IsNullOrEmpty(seg.DefaultSelectionEntryId) ? null : seg.DefaultSelectionEntryId,
        Constraints = seg.Constraints?.Select(ToProtocol).ToList(),
        Modifiers = seg.Modifiers?.Select(ToProtocol).ToList(),
        SelectionEntries = seg.SelectionEntries?.Select(ToProtocol).ToList(),
    };

    static ProtocolEntryLink ToProtocol(EntryLinkSpec el) => new()
    {
        Id = el.Id, Name = el.Name, TargetId = el.TargetId, Type = el.Type, Hidden = el.Hidden,
        Costs = el.Costs?.Select(ToProtocolCostValue).ToList(),
        Constraints = el.Constraints?.Select(ToProtocol).ToList(),
        Modifiers = el.Modifiers?.Select(ToProtocol).ToList(),
        CategoryLinks = el.CategoryLinks?.Select(ToProtocol).ToList(),
    };

    static ProtocolCategoryLink ToProtocol(CategoryLinkSpec cl) => new()
    { Id = cl.Id, TargetId = cl.TargetId, Name = cl.Name, Primary = cl.Primary };

    static ProtocolCostValue ToProtocolCostValue(CostSpec c) => new()
    { Name = c.Name, TypeId = c.TypeId, Value = c.Value };

    static ProtocolConstraint ToProtocol(ConstraintSpec c) => new()
    {
        Id = c.Id, Type = c.Type, Value = c.Value, Field = c.Field, Scope = c.Scope,
        Shared = c.Shared, IncludeChildSelections = c.IncludeChildSelections,
        IncludeChildForces = c.IncludeChildForces, PercentValue = c.PercentValue,
    };

    static ProtocolModifier ToProtocol(ModifierSpec m) => new()
    {
        Type = m.Type, Field = m.Field, Value = m.Value,
        Conditions = m.Conditions?.Select(ToProtocol).ToList(),
        ConditionGroups = m.ConditionGroups?.Select(ToProtocol).ToList(),
        Repeats = m.Repeats?.Select(ToProtocol).ToList(),
    };

    static ProtocolModifierGroup ToProtocol(ModifierGroupSpec mg) => new()
    {
        Conditions = mg.Conditions?.Select(ToProtocol).ToList(),
        ConditionGroups = mg.ConditionGroups?.Select(ToProtocol).ToList(),
        Repeats = mg.Repeats?.Select(ToProtocol).ToList(),
        Modifiers = mg.Modifiers?.Select(ToProtocol).ToList(),
        ModifierGroups = mg.ModifierGroups?.Select(ToProtocol).ToList(),
    };

    static ProtocolCondition ToProtocol(ConditionSpec c) => new()
    {
        Type = c.Type, Value = c.Value, Field = c.Field, Scope = c.Scope, ChildId = c.ChildId,
        Shared = c.Shared, IncludeChildSelections = c.IncludeChildSelections,
        IncludeChildForces = c.IncludeChildForces, PercentValue = c.PercentValue,
    };

    static ProtocolConditionGroup ToProtocol(ConditionGroupSpec cg) => new()
    {
        Type = cg.Type,
        Conditions = cg.Conditions?.Select(ToProtocol).ToList(),
        ConditionGroups = cg.ConditionGroups?.Select(ToProtocol).ToList(),
    };

    static ProtocolRepeat ToProtocol(RepeatSpec r) => new()
    {
        Value = r.Value, Repeats = r.Repeats, Field = r.Field, Scope = r.Scope, ChildId = r.ChildId,
        RoundUp = r.RoundUp, Shared = r.Shared, IncludeChildSelections = r.IncludeChildSelections,
        IncludeChildForces = r.IncludeChildForces, PercentValue = r.PercentValue,
    };

    static ProtocolRule ToProtocol(RuleSpec r) => new()
    {
        Id = r.Id, Name = r.Name, Description = r.Description, Hidden = r.Hidden,
        Page = string.IsNullOrEmpty(r.Page) ? null : r.Page,
        Modifiers = r.Modifiers?.Select(ToProtocol).ToList(),
    };

    static ProtocolProfile ToProtocol(ProfileSpec p) => new()
    {
        Id = p.Id, Name = p.Name, TypeId = p.TypeId, TypeName = p.TypeName, Hidden = p.Hidden,
        Characteristics = p.Characteristics?.Select(c => new ProtocolCharacteristic
        { Name = c.Name, TypeId = c.TypeId, Value = c.Value }).ToList(),
        Modifiers = p.Modifiers?.Select(ToProtocol).ToList(),
    };

    static ProtocolInfoGroup ToProtocol(InfoGroupSpec ig) => new()
    {
        Id = ig.Id, Name = ig.Name, Hidden = ig.Hidden,
        Profiles = ig.Profiles?.Select(ToProtocol).ToList(),
        Rules = ig.Rules?.Select(ToProtocol).ToList(),
        Modifiers = ig.Modifiers?.Select(ToProtocol).ToList(),
    };

    // ===== Protocol → Spec (adapter side: receiving setup data) =====

    public static (GameSystemSpec, CatalogueSpec) FromSetupCommand(SetupCommand cmd) =>
        (FromProtocol(cmd.GameSystem), FromProtocol(cmd.Catalogue));

    public static GameSystemSpec FromProtocol(ProtocolGameSystem gs) => new(
        Id: gs.Id, Name: gs.Name,
        ForceEntries: gs.ForceEntries?.Select(FromProtocol).ToArray(),
        CostTypes: gs.CostTypes?.Select(ct => new CostTypeSpec(ct.Id, ct.Name, ct.DefaultCostLimit, ct.Hidden, ct.Limit)).ToArray(),
        CategoryEntries: gs.CategoryEntries?.Select(ce => new CategoryEntrySpec(ce.Id, ce.Name)).ToArray(),
        ProfileTypes: gs.ProfileTypes?.Select(pt => new ProfileTypeSpec(pt.Id, pt.Name,
            pt.CharacteristicTypes?.Select(ct => new CharacteristicTypeSpec(ct.Id, ct.Name)).ToArray())).ToArray());

    public static CatalogueSpec FromProtocol(ProtocolCatalogue cat) => new(
        Id: cat.Id, Name: cat.Name, GameSystemId: cat.GameSystemId,
        SelectionEntries: cat.SelectionEntries?.Select(FromProtocol).ToArray(),
        SelectionEntryGroups: cat.SelectionEntryGroups?.Select(FromProtocol).ToArray(),
        EntryLinks: cat.EntryLinks?.Select(FromProtocol).ToArray());

    static ForceEntrySpec FromProtocol(ProtocolForceEntry fe) => new(
        fe.Id, fe.Name,
        fe.CategoryLinks?.Select(FromProtocol).ToArray(),
        fe.ForceEntries?.Select(FromProtocol).ToArray());

    static SelectionEntrySpec FromProtocol(ProtocolSelectionEntry se) => new(
        Id: se.Id, Name: se.Name, Type: se.Type, Hidden: se.Hidden, Collective: se.Collective,
        Page: se.Page ?? "",
        Costs: se.Costs?.Select(c => new CostSpec(c.Name, c.TypeId, c.Value)).ToArray(),
        Constraints: se.Constraints?.Select(FromProtocol).ToArray(),
        Modifiers: se.Modifiers?.Select(FromProtocol).ToArray(),
        ModifierGroups: se.ModifierGroups?.Select(FromProtocol).ToArray(),
        ChildEntries: se.SelectionEntries?.Select(FromProtocol).ToArray(),
        SelectionEntryGroups: se.SelectionEntryGroups?.Select(FromProtocol).ToArray(),
        CategoryLinks: se.CategoryLinks?.Select(FromProtocol).ToArray(),
        Rules: se.Rules?.Select(FromProtocol).ToArray(),
        Profiles: se.Profiles?.Select(FromProtocol).ToArray(),
        InfoGroups: se.InfoGroups?.Select(FromProtocol).ToArray(),
        EntryLinks: se.EntryLinks?.Select(FromProtocol).ToArray());

    static SelectionEntryGroupSpec FromProtocol(ProtocolSelectionEntryGroup seg) => new(
        Id: seg.Id, Name: seg.Name, Hidden: seg.Hidden,
        DefaultSelectionEntryId: seg.DefaultSelectionEntryId ?? "",
        Constraints: seg.Constraints?.Select(FromProtocol).ToArray(),
        Modifiers: seg.Modifiers?.Select(FromProtocol).ToArray(),
        SelectionEntries: seg.SelectionEntries?.Select(FromProtocol).ToArray());

    static EntryLinkSpec FromProtocol(ProtocolEntryLink el) => new(
        Id: el.Id, Name: el.Name, TargetId: el.TargetId, Type: el.Type, Hidden: el.Hidden,
        Costs: el.Costs?.Select(c => new CostSpec(c.Name, c.TypeId, c.Value)).ToArray(),
        Constraints: el.Constraints?.Select(FromProtocol).ToArray(),
        Modifiers: el.Modifiers?.Select(FromProtocol).ToArray(),
        CategoryLinks: el.CategoryLinks?.Select(FromProtocol).ToArray());

    static CategoryLinkSpec FromProtocol(ProtocolCategoryLink cl) => new(cl.Id, cl.TargetId, cl.Name, cl.Primary);

    static ConstraintSpec FromProtocol(ProtocolConstraint c) => new(
        c.Id, c.Type, c.Value, c.Field, c.Scope, c.Shared,
        c.IncludeChildSelections, c.IncludeChildForces, c.PercentValue);

    static ModifierSpec FromProtocol(ProtocolModifier m) => new(
        m.Type, m.Field, m.Value,
        m.Conditions?.Select(FromProtocol).ToArray(),
        m.ConditionGroups?.Select(FromProtocol).ToArray(),
        m.Repeats?.Select(FromProtocol).ToArray());

    static ModifierGroupSpec FromProtocol(ProtocolModifierGroup mg) => new(
        mg.Conditions?.Select(FromProtocol).ToArray(),
        mg.ConditionGroups?.Select(FromProtocol).ToArray(),
        mg.Repeats?.Select(FromProtocol).ToArray(),
        mg.Modifiers?.Select(FromProtocol).ToArray(),
        mg.ModifierGroups?.Select(FromProtocol).ToArray());

    static ConditionSpec FromProtocol(ProtocolCondition c) => new(
        c.Type, c.Value, c.Field, c.Scope, c.ChildId, c.PercentValue,
        c.Shared, c.IncludeChildSelections, c.IncludeChildForces);

    static ConditionGroupSpec FromProtocol(ProtocolConditionGroup cg) => new(
        cg.Type,
        cg.Conditions?.Select(FromProtocol).ToArray(),
        cg.ConditionGroups?.Select(FromProtocol).ToArray());

    static RepeatSpec FromProtocol(ProtocolRepeat r) => new(
        r.Value, r.Repeats, r.Field, r.Scope, r.ChildId,
        r.RoundUp, r.Shared, r.IncludeChildSelections, r.IncludeChildForces, r.PercentValue);

    static RuleSpec FromProtocol(ProtocolRule r) => new(
        r.Id, r.Name, r.Description, r.Hidden, r.Page ?? "",
        r.Modifiers?.Select(FromProtocol).ToArray());

    static ProfileSpec FromProtocol(ProtocolProfile p) => new(
        p.Id, p.Name, p.TypeId, p.TypeName, p.Hidden,
        p.Characteristics?.Select(c => new CharacteristicSpec(c.Name, c.TypeId, c.Value)).ToArray(),
        p.Modifiers?.Select(FromProtocol).ToArray());

    static InfoGroupSpec FromProtocol(ProtocolInfoGroup ig) => new(
        ig.Id, ig.Name, ig.Hidden,
        ig.Profiles?.Select(FromProtocol).ToArray(),
        ig.Rules?.Select(FromProtocol).ToArray(),
        ig.Modifiers?.Select(FromProtocol).ToArray());

    // ===== Protocol → Engine state records =====

    public static RosterState ToRosterState(StateResponse state) => new(
        state.Name, state.GameSystemId,
        state.Forces.Select(ToForceState).ToList(),
        state.Costs.Select(ToCostState).ToList(),
        state.ValidationErrors);

    static ForceState ToForceState(ProtocolForce f) => new(
        f.Name, f.CatalogueId,
        f.Selections.Select(ToSelectionState).ToList());

    static SelectionState ToSelectionState(ProtocolSelection s) => new(
        s.Name, s.EntryId, s.Type, s.Number, s.Hidden,
        s.Costs.Select(ToCostState).ToList(),
        s.Children.Select(ToSelectionState).ToList(),
        Profiles: s.Profiles?.Select(p => new ProfileState(
            p.Name, p.TypeId, p.TypeName, p.Hidden,
            p.Characteristics.Select(c => new CharacteristicState(c.Name, c.TypeId, c.Value)).ToList())).ToList()!,
        Rules: s.Rules?.Select(r => new RuleState(r.Name, r.Description, r.Hidden)).ToList()!,
        Categories: s.Categories?.Select(c => new CategoryState(c.Name, c.EntryId, c.Primary)).ToList()!,
        Page: s.Page);

    static CostState ToCostState(ProtocolCost c) => new(c.Name, c.TypeId, c.Value);

    // ===== Engine state → Protocol (adapter side: sending state) =====

    public static StateResponse ToStateResponse(RosterState state) => new()
    {
        Name = state.Name,
        GameSystemId = state.GameSystemId,
        Forces = state.Forces.Select(ToProtocolForce).ToList(),
        Costs = state.Costs.Select(ToProtocolCost).ToList(),
        ValidationErrors = state.ValidationErrors.ToList(),
    };

    static ProtocolForce ToProtocolForce(ForceState f) => new()
    {
        Name = f.Name, CatalogueId = f.CatalogueId,
        Selections = f.Selections.Select(ToProtocolSelection).ToList(),
    };

    static ProtocolSelection ToProtocolSelection(SelectionState s) => new()
    {
        Name = s.Name, EntryId = s.EntryId, Type = s.Type,
        Number = s.Number, Hidden = s.Hidden,
        Costs = s.Costs.Select(ToProtocolCost).ToList(),
        Children = s.Children.Select(ToProtocolSelection).ToList(),
        Profiles = s.Profiles?.Select(p => new ProtocolSelectionProfile
        {
            Name = p.Name, TypeId = p.TypeId, TypeName = p.TypeName, Hidden = p.Hidden,
            Characteristics = p.Characteristics.Select(c => new ProtocolCharacteristic
            { Name = c.Name, TypeId = c.TypeId ?? "", Value = c.Value }).ToList(),
        }).ToList(),
        Rules = s.Rules?.Select(r => new ProtocolSelectionRule
        { Name = r.Name, Description = r.Description, Hidden = r.Hidden }).ToList(),
        Categories = s.Categories?.Select(c => new ProtocolSelectionCategory
        { Name = c.Name, EntryId = c.EntryId, Primary = c.Primary }).ToList(),
        Page = s.Page,
    };

    static ProtocolCost ToProtocolCost(CostState c) => new()
    { Name = c.Name, TypeId = c.TypeId, Value = c.Value };
}
