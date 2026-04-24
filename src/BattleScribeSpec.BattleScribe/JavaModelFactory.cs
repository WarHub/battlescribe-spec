using net.battlescribe.model.data;
using net.battlescribe.model.roster;

namespace BattleScribeSpec;

/// <summary>
/// Factory for creating Java BattleScribe model objects (via IKVM) for use in engine tests.
/// Java model uses mutable lists accessed via getXxx() methods.
/// </summary>
public static class JavaModelFactory
{
    /// <summary>
    /// Create a GameSystem with cost types and optional force entries.
    /// </summary>
    public static GameSystem CreateGameSystem(
        string id = "test-gs",
        string name = "Test Game System",
        int revision = 1,
        string bsVersion = "2.03",
        IEnumerable<CostType>? costTypes = null,
        IEnumerable<ForceEntry>? forceEntries = null,
        IEnumerable<CategoryEntry>? categoryEntries = null,
        IEnumerable<ProfileType>? profileTypes = null,
        IEnumerable<Publication>? publications = null,
        IEnumerable<SelectionEntry>? selectionEntries = null,
        IEnumerable<EntryLink>? entryLinks = null,
        IEnumerable<net.battlescribe.model.data.Rule>? rules = null,
        IEnumerable<InfoLink>? infoLinks = null,
        IEnumerable<SelectionEntry>? sharedSelectionEntries = null,
        IEnumerable<SelectionEntryGroup>? sharedSelectionEntryGroups = null,
        IEnumerable<net.battlescribe.model.data.Rule>? sharedRules = null,
        IEnumerable<Profile>? sharedProfiles = null,
        IEnumerable<InfoGroup>? sharedInfoGroups = null)
    {
        var gs = new GameSystem();
        gs.setId(id);
        gs.setName(name);
        gs.setRevision(revision);
        gs.setBattleScribeVersion(bsVersion);
        gs.setAuthorName("Test");

        if (costTypes != null)
            foreach (var ct in costTypes)
                gs.getCostTypes().add(ct);

        if (forceEntries != null)
            foreach (var fe in forceEntries)
                gs.getForceEntries().add(fe);

        if (categoryEntries != null)
            foreach (var ce in categoryEntries)
                gs.getCategoryEntries().add(ce);

        if (profileTypes != null)
            foreach (var pt in profileTypes)
                gs.getProfileTypes().add(pt);

        if (publications != null)
            foreach (var pub in publications)
                gs.getPublications().add(pub);

        if (selectionEntries != null)
            foreach (var se in selectionEntries)
                gs.getSelectionEntries().add(se);

        if (entryLinks != null)
            foreach (var el in entryLinks)
                gs.getEntryLinks().add(el);

        if (rules != null)
            foreach (var r in rules)
                gs.getRules().add(r);

        if (infoLinks != null)
            foreach (var il in infoLinks)
                gs.getInfoLinks().add(il);

        if (sharedSelectionEntries != null)
            foreach (var se in sharedSelectionEntries)
                gs.getSharedSelectionEntries().add(se);

        if (sharedSelectionEntryGroups != null)
            foreach (var seg in sharedSelectionEntryGroups)
                gs.getSharedSelectionEntryGroups().add(seg);

        if (sharedRules != null)
            foreach (var r in sharedRules)
                gs.getSharedRules().add(r);

        if (sharedProfiles != null)
            foreach (var p in sharedProfiles)
                gs.getSharedProfiles().add(p);

        if (sharedInfoGroups != null)
            foreach (var ig in sharedInfoGroups)
                gs.getSharedInfoGroups().add(ig);

        return gs;
    }

    /// <summary>
    /// Create a CostType.
    /// </summary>
    public static CostType CreateCostType(
        string id,
        string name,
        double? defaultCostLimit = null,
        bool hidden = false,
        bool limit = false)
    {
        var ct = new CostType();
        ct.setId(id);
        ct.setName(name);
        // When no limit specified, use -1.0 (BattleScribe convention for "no limit")
        ct.setDefaultCostLimit(defaultCostLimit ?? -1.0);
        ct.setHidden(hidden);
        var setLimit = ct.GetType().GetMethod("setLimit")
            ?? throw new MissingMethodException(ct.GetType().FullName, "setLimit");
        setLimit.Invoke(ct, [limit]);
        return ct;
    }

    /// <summary>
    /// Create a CategoryEntry.
    /// </summary>
    public static CategoryEntry CreateCategoryEntry(
        string id, string name, bool hidden = false,
        IEnumerable<Constraint>? constraints = null,
        IEnumerable<Modifier>? modifiers = null,
        IEnumerable<ModifierGroup>? modifierGroups = null,
        IEnumerable<Profile>? profiles = null,
        IEnumerable<Rule>? rules = null,
        IEnumerable<InfoGroup>? infoGroups = null,
        IEnumerable<InfoLink>? infoLinks = null,
        string? publicationId = null,
        string? page = null)
    {
        var ce = new CategoryEntry();
        ce.setId(id);
        ce.setName(name);
        ce.setHidden(hidden);
        if (!string.IsNullOrEmpty(publicationId))
            ce.setPublicationId(publicationId);
        if (!string.IsNullOrEmpty(page))
            ce.setPage(page);

        if (constraints != null)
            foreach (var c in constraints)
                ce.getConstraints().add(c);

        if (modifiers != null)
            foreach (var m in modifiers)
                ce.getModifiers().add(m);

        if (modifierGroups != null)
            foreach (var mg in modifierGroups)
                ce.getModifierGroups().add(mg);

        if (profiles != null)
            foreach (var p in profiles)
                ce.getProfiles().add(p);

        if (rules != null)
            foreach (var r in rules)
                ce.getRules().add(r);

        if (infoGroups != null)
            foreach (var ig in infoGroups)
                ce.getInfoGroups().add(ig);

        if (infoLinks != null)
            foreach (var il in infoLinks)
                ce.getInfoLinks().add(il);

        return ce;
    }

    /// <summary>
    /// Create a ForceEntry with category links.
    /// </summary>
    public static ForceEntry CreateForceEntry(
        string id,
        string name,
        bool hidden = false,
        IEnumerable<CategoryLink>? categoryLinks = null,
        IEnumerable<ForceEntry>? forceEntries = null,
        IEnumerable<Constraint>? constraints = null,
        IEnumerable<Modifier>? modifiers = null,
        IEnumerable<ModifierGroup>? modifierGroups = null,
        IEnumerable<Profile>? profiles = null,
        IEnumerable<Rule>? rules = null,
        IEnumerable<InfoGroup>? infoGroups = null,
        IEnumerable<InfoLink>? infoLinks = null,
        string? publicationId = null,
        string? page = null)
    {
        var fe = new ForceEntry();
        fe.setId(id);
        fe.setName(name);
        fe.setHidden(hidden);
        if (!string.IsNullOrEmpty(publicationId))
            fe.setPublicationId(publicationId);
        if (!string.IsNullOrEmpty(page))
            fe.setPage(page);

        if (categoryLinks != null)
            foreach (var cl in categoryLinks)
                fe.getCategoryLinks().add(cl);

        if (forceEntries != null)
            foreach (var child in forceEntries)
                fe.getForceEntries().add(child);

        if (constraints != null)
            foreach (var c in constraints)
                fe.getConstraints().add(c);

        if (modifiers != null)
            foreach (var m in modifiers)
                fe.getModifiers().add(m);

        if (modifierGroups != null)
            foreach (var mg in modifierGroups)
                fe.getModifierGroups().add(mg);

        if (profiles != null)
            foreach (var p in profiles)
                fe.getProfiles().add(p);

        if (rules != null)
            foreach (var r in rules)
                fe.getRules().add(r);

        if (infoGroups != null)
            foreach (var ig in infoGroups)
                fe.getInfoGroups().add(ig);

        if (infoLinks != null)
            foreach (var il in infoLinks)
                fe.getInfoLinks().add(il);

        return fe;
    }

    /// <summary>
    /// Create a CategoryLink linking a ForceEntry to a CategoryEntry.
    /// </summary>
    public static CategoryLink CreateCategoryLink(
        string id, string targetId, string name, bool primary = false,
        bool hidden = false,
        IEnumerable<Constraint>? constraints = null,
        IEnumerable<Modifier>? modifiers = null,
        IEnumerable<ModifierGroup>? modifierGroups = null,
        IEnumerable<Profile>? profiles = null,
        IEnumerable<Rule>? rules = null,
        IEnumerable<InfoGroup>? infoGroups = null,
        IEnumerable<InfoLink>? infoLinks = null,
        string? publicationId = null,
        string? page = null)
    {
        var cl = new CategoryLink();
        cl.setId(id);
        cl.setTargetId(targetId);
        cl.setName(name);
        cl.setPrimary(primary);
        cl.setHidden(hidden);
        if (!string.IsNullOrEmpty(publicationId))
            cl.setPublicationId(publicationId);
        if (!string.IsNullOrEmpty(page))
            cl.setPage(page);

        if (constraints != null)
            foreach (var c in constraints)
                cl.getConstraints().add(c);

        if (modifiers != null)
            foreach (var m in modifiers)
                cl.getModifiers().add(m);

        if (modifierGroups != null)
            foreach (var mg in modifierGroups)
                cl.getModifierGroups().add(mg);

        if (profiles != null)
            foreach (var p in profiles)
                cl.getProfiles().add(p);

        if (rules != null)
            foreach (var r in rules)
                cl.getRules().add(r);

        if (infoGroups != null)
            foreach (var ig in infoGroups)
                cl.getInfoGroups().add(ig);

        if (infoLinks != null)
            foreach (var il in infoLinks)
                cl.getInfoLinks().add(il);

        return cl;
    }

    /// <summary>
    /// Create an EntryLink that references a shared entry.
    /// </summary>
    public static EntryLink CreateEntryLink(
        string id,
        string name,
        string targetId,
        string type = "selectionEntry",
        bool hidden = false,
        bool collective = false,
        IEnumerable<Cost>? costs = null,
        IEnumerable<Constraint>? constraints = null,
        IEnumerable<Modifier>? modifiers = null,
        IEnumerable<ModifierGroup>? modifierGroups = null,
        IEnumerable<CategoryLink>? categoryLinks = null,
        IEnumerable<SelectionEntry>? selectionEntries = null,
        IEnumerable<SelectionEntryGroup>? selectionEntryGroups = null,
        IEnumerable<EntryLink>? entryLinks = null,
        IEnumerable<Profile>? profiles = null,
        IEnumerable<net.battlescribe.model.data.Rule>? rules = null,
        IEnumerable<InfoGroup>? infoGroups = null,
        IEnumerable<InfoLink>? infoLinks = null,
        bool import = true,
        string? publicationId = null,
        string? page = null)
    {
        var el = new EntryLink();
        el.setId(id);
        el.setName(name);
        el.setTargetId(targetId);
        el.setType(type);
        el.setHidden(hidden);
        el.setCollective(collective);
        el.setImported(import);
        if (!string.IsNullOrEmpty(publicationId))
            el.setPublicationId(publicationId);
        if (!string.IsNullOrEmpty(page))
            el.setPage(page);

        if (costs != null)
            foreach (var c in costs)
                el.getCosts().add(c);

        if (constraints != null)
            foreach (var c in constraints)
                el.getConstraints().add(c);

        if (modifiers != null)
            foreach (var m in modifiers)
                el.getModifiers().add(m);

        if (modifierGroups != null)
            foreach (var mg in modifierGroups)
                el.getModifierGroups().add(mg);

        if (categoryLinks != null)
            foreach (var cl in categoryLinks)
                el.getCategoryLinks().add(cl);

        if (selectionEntries != null)
            foreach (var se in selectionEntries)
                el.getSelectionEntries().add(se);

        if (selectionEntryGroups != null)
            foreach (var seg in selectionEntryGroups)
                el.getSelectionEntryGroups().add(seg);

        if (entryLinks != null)
            foreach (var link in entryLinks)
                el.getEntryLinks().add(link);

        if (profiles != null)
            foreach (var p in profiles)
                el.getProfiles().add(p);

        if (rules != null)
            foreach (var r in rules)
                el.getRules().add(r);

        if (infoGroups != null)
            foreach (var ig in infoGroups)
                el.getInfoGroups().add(ig);

        if (infoLinks != null)
            foreach (var il in infoLinks)
                el.getInfoLinks().add(il);

        return el;
    }

    /// <summary>
    /// Create a Catalogue with optional entries.
    /// </summary>
    public static Catalogue CreateCatalogue(
        string id,
        string name,
        string gameSystemId,
        int revision = 1,
        string bsVersion = "2.03",
        bool library = false,
        IEnumerable<SelectionEntry>? selectionEntries = null,
        IEnumerable<EntryLink>? entryLinks = null,
        IEnumerable<SelectionEntry>? sharedSelectionEntries = null,
        IEnumerable<SelectionEntryGroup>? sharedSelectionEntryGroups = null,
        IEnumerable<Rule>? sharedRules = null,
        IEnumerable<Profile>? sharedProfiles = null,
        IEnumerable<InfoGroup>? sharedInfoGroups = null,
        IEnumerable<Rule>? rules = null,
        IEnumerable<CostType>? costTypes = null,
        IEnumerable<ProfileType>? profileTypes = null,
        IEnumerable<CategoryEntry>? categoryEntries = null,
        IEnumerable<ForceEntry>? forceEntries = null)
    {
        var cat = new Catalogue();
        cat.setId(id);
        cat.setName(name);
        cat.setGameSystemId(gameSystemId);
        cat.setRevision(revision);
        cat.setBattleScribeVersion(bsVersion);
        cat.setAuthorName("Test");
        cat.setLibrary(library);

        if (selectionEntries != null)
            foreach (var se in selectionEntries)
                cat.getSelectionEntries().add(se);

        if (entryLinks != null)
            foreach (var el in entryLinks)
                cat.getEntryLinks().add(el);

        if (sharedSelectionEntries != null)
            foreach (var se in sharedSelectionEntries)
                cat.getSharedSelectionEntries().add(se);

        if (sharedSelectionEntryGroups != null)
            foreach (var seg in sharedSelectionEntryGroups)
                cat.getSharedSelectionEntryGroups().add(seg);

        if (sharedRules != null)
            foreach (var r in sharedRules)
                cat.getSharedRules().add(r);

        if (sharedProfiles != null)
            foreach (var p in sharedProfiles)
                cat.getSharedProfiles().add(p);

        if (sharedInfoGroups != null)
            foreach (var ig in sharedInfoGroups)
                cat.getSharedInfoGroups().add(ig);

        if (rules != null)
            foreach (var r in rules)
                cat.getRules().add(r);

        if (costTypes != null)
            foreach (var ct in costTypes)
                cat.getCostTypes().add(ct);

        if (profileTypes != null)
            foreach (var pt in profileTypes)
                cat.getProfileTypes().add(pt);

        if (categoryEntries != null)
            foreach (var ce in categoryEntries)
                cat.getCategoryEntries().add(ce);

        if (forceEntries != null)
            foreach (var fe in forceEntries)
                cat.getForceEntries().add(fe);

        return cat;
    }

    /// <summary>
    /// Create a SelectionEntry (a unit, model, upgrade, etc.)
    /// </summary>
    public static SelectionEntry CreateSelectionEntry(
        string id,
        string name,
        string type = "unit",
        bool hidden = false,
        IEnumerable<Cost>? costs = null,
        IEnumerable<SelectionEntry>? selectionEntries = null,
        IEnumerable<EntryLink>? entryLinks = null,
        IEnumerable<CategoryLink>? categoryLinks = null,
        IEnumerable<Constraint>? constraints = null,
        IEnumerable<Modifier>? modifiers = null,
        bool collective = false,
        bool import = true,
        string? publicationId = null)
    {
        var se = new SelectionEntry();
        se.setId(id);
        se.setName(name);
        se.setType(type);
        se.setHidden(hidden);
        se.setCollective(collective);
        se.setImported(import);
        if (!string.IsNullOrEmpty(publicationId))
            se.setPublicationId(publicationId);

        if (costs != null)
            foreach (var c in costs)
                se.getCosts().add(c);

        if (selectionEntries != null)
            foreach (var child in selectionEntries)
                se.getSelectionEntries().add(child);

        if (entryLinks != null)
            foreach (var el in entryLinks)
                se.getEntryLinks().add(el);

        if (categoryLinks != null)
            foreach (var cl in categoryLinks)
                se.getCategoryLinks().add(cl);

        if (constraints != null)
            foreach (var c in constraints)
                se.getConstraints().add(c);

        if (modifiers != null)
            foreach (var m in modifiers)
                se.getModifiers().add(m);

        return se;
    }

    /// <summary>
    /// Create a SelectionEntryGroup (a group of mutually exclusive entries).
    /// </summary>
    public static SelectionEntryGroup CreateSelectionEntryGroup(
        string id,
        string name,
        bool hidden = false,
        string? defaultSelectionEntryId = null,
        bool collective = false,
        IEnumerable<SelectionEntry>? selectionEntries = null,
        IEnumerable<SelectionEntryGroup>? selectionEntryGroups = null,
        IEnumerable<EntryLink>? entryLinks = null,
        IEnumerable<CategoryLink>? categoryLinks = null,
        IEnumerable<Cost>? costs = null,
        IEnumerable<Constraint>? constraints = null,
        IEnumerable<Modifier>? modifiers = null,
        IEnumerable<ModifierGroup>? modifierGroups = null,
        IEnumerable<Profile>? profiles = null,
        IEnumerable<net.battlescribe.model.data.Rule>? rules = null,
        IEnumerable<InfoGroup>? infoGroups = null,
        IEnumerable<InfoLink>? infoLinks = null,
        string? page = null,
        string? publicationId = null,
        bool import = true)
    {
        var seg = new SelectionEntryGroup();
        seg.setId(id);
        seg.setName(name);
        seg.setHidden(hidden);
        seg.setCollective(collective);
        seg.setImported(import);
        if (!string.IsNullOrEmpty(defaultSelectionEntryId))
            seg.setDefaultSelectionEntryId(defaultSelectionEntryId);
        if (!string.IsNullOrEmpty(page))
            seg.setPage(page);
        if (!string.IsNullOrEmpty(publicationId))
            seg.setPublicationId(publicationId);

        if (selectionEntries != null)
            foreach (var se in selectionEntries)
                seg.getSelectionEntries().add(se);

        if (selectionEntryGroups != null)
            foreach (var child in selectionEntryGroups)
                seg.getSelectionEntryGroups().add(child);

        if (entryLinks != null)
            foreach (var el in entryLinks)
                seg.getEntryLinks().add(el);

        if (categoryLinks != null)
            foreach (var cl in categoryLinks)
                seg.getCategoryLinks().add(cl);

        if (costs != null)
            foreach (var c in costs)
                seg.getCosts().add(c);

        if (constraints != null)
            foreach (var c in constraints)
                seg.getConstraints().add(c);

        if (modifiers != null)
            foreach (var m in modifiers)
                seg.getModifiers().add(m);

        if (modifierGroups != null)
            foreach (var mg in modifierGroups)
                seg.getModifierGroups().add(mg);

        if (profiles != null)
            foreach (var p in profiles)
                seg.getProfiles().add(p);

        if (rules != null)
            foreach (var r in rules)
                seg.getRules().add(r);

        if (infoGroups != null)
            foreach (var ig in infoGroups)
                seg.getInfoGroups().add(ig);

        if (infoLinks != null)
            foreach (var il in infoLinks)
                seg.getInfoLinks().add(il);

        return seg;
    }

    /// <summary>
    /// Create a Cost entry.
    /// </summary>
    public static Cost CreateCost(string name, string typeId, double value)
    {
        var c = new Cost();
        c.setName(name);
        c.setTypeId(typeId);
        c.setValue(value);
        return c;
    }

    /// <summary>
    /// Create a Constraint (min/max selection count or cost).
    /// </summary>
    public static Constraint CreateConstraint(
        string id,
        string type,
        double value,
        string field,
        string scope,
        bool shared = false,
        bool includeChildSelections = false,
        bool includeChildForces = false,
        bool percentValue = false)
    {
        var c = new Constraint();
        c.setId(id);
        c.setType(type);
        c.setValue(value);
        c.setField(field);
        c.setScope(scope);
        c.setShared(shared);
        c.setIncludeChildSelections(includeChildSelections);
        c.setIncludeChildForces(includeChildForces);
        c.setPercentValue(percentValue);
        return c;
    }

    /// <summary>
    /// Create a Modifier that changes an element's property.
    /// </summary>
    public static Modifier CreateModifier(
        string type,
        string field,
        string value,
        IEnumerable<Condition>? conditions = null,
        IEnumerable<Repeat>? repeats = null)
    {
        var m = new Modifier();
        m.setType(type);
        m.setField(field);
        m.setValue(value);

        if (conditions != null)
            foreach (var cond in conditions)
                m.getConditions().add(cond);

        if (repeats != null)
            foreach (var r in repeats)
                m.getRepeats().add(r);

        return m;
    }

    /// <summary>
    /// Create a Condition for a modifier.
    /// </summary>
    public static Condition CreateCondition(
        string type,
        double value,
        string field,
        string scope,
        string childId = "",
        bool shared = false,
        bool includeChildSelections = false,
        bool includeChildForces = false,
        bool percentValue = false)
    {
        var c = new Condition();
        c.setType(type);
        c.setValue(value);
        c.setField(field);
        c.setScope(scope);
        c.setChildId(childId);
        c.setShared(shared);
        c.setIncludeChildSelections(includeChildSelections);
        c.setIncludeChildForces(includeChildForces);
        c.setPercentValue(percentValue);
        return c;
    }

    /// <summary>
    /// Create a ConditionGroup (AND/OR logic for conditions).
    /// </summary>
    public static ConditionGroup CreateConditionGroup(
        string type = "and",
        IEnumerable<Condition>? conditions = null,
        IEnumerable<ConditionGroup>? conditionGroups = null)
    {
        var cg = new ConditionGroup();
        cg.setType(type);

        if (conditions != null)
            foreach (var c in conditions)
                cg.getConditions().add(c);

        if (conditionGroups != null)
            foreach (var child in conditionGroups)
                cg.getConditionGroups().add(child);

        return cg;
    }

    /// <summary>
    /// Create a Repeat for a modifier.
    /// </summary>
    public static Repeat CreateRepeat(
        double value = 1,
        int repeats = 1,
        string field = "selections",
        string scope = "self",
        string childId = "",
        bool roundUp = false,
        bool shared = false,
        bool includeChildSelections = false,
        bool includeChildForces = false,
        bool percentValue = false)
    {
        var r = new Repeat();
        r.setValue(value);
        r.setRepeats(repeats);
        r.setField(field);
        r.setScope(scope);
        r.setChildId(childId);
        r.setRoundUp(roundUp);
        r.setShared(shared);
        r.setIncludeChildSelections(includeChildSelections);
        r.setIncludeChildForces(includeChildForces);
        r.setPercentValue(percentValue);
        return r;
    }

    /// <summary>
    /// Create a ModifierGroup containing conditions and modifiers.
    /// </summary>
    public static ModifierGroup CreateModifierGroup(
        IEnumerable<Condition>? conditions = null,
        IEnumerable<ConditionGroup>? conditionGroups = null,
        IEnumerable<Repeat>? repeats = null,
        IEnumerable<Modifier>? modifiers = null)
    {
        var mg = new ModifierGroup();

        if (conditions != null)
            foreach (var c in conditions)
                mg.getConditions().add(c);

        if (conditionGroups != null)
            foreach (var cg in conditionGroups)
                mg.getConditionGroups().add(cg);

        if (repeats != null)
            foreach (var r in repeats)
                mg.getRepeats().add(r);

        if (modifiers != null)
            foreach (var m in modifiers)
                mg.getModifiers().add(m);

        return mg;
    }

    public static net.battlescribe.model.data.Rule CreateRule(
        string id, string name, string description = "", bool hidden = false, string page = "",
        IEnumerable<Modifier>? modifiers = null, string? publicationId = null,
        IEnumerable<ModifierGroup>? modifierGroups = null)
    {
        var r = new net.battlescribe.model.data.Rule();
        r.setId(id);
        r.setName(name);
        r.setDescription(description);
        r.setHidden(hidden);
        if (!string.IsNullOrEmpty(page))
            r.setPage(page);
        if (!string.IsNullOrEmpty(publicationId))
            r.setPublicationId(publicationId);
        if (modifiers != null)
            foreach (var m in modifiers)
                r.getModifiers().add(m);
        if (modifierGroups != null)
            foreach (var mg in modifierGroups)
                r.getModifierGroups().add(mg);
        return r;
    }

    public static Profile CreateProfile(
        string id, string name, string typeId = "", string typeName = "",
        bool hidden = false,
        IEnumerable<Characteristic>? characteristics = null,
        IEnumerable<Modifier>? modifiers = null,
        string? page = null,
        string? publicationId = null,
        IEnumerable<ModifierGroup>? modifierGroups = null)
    {
        var p = new Profile();
        p.setId(id);
        p.setName(name);
        p.setTypeId(typeId);
        p.setTypeName(typeName);
        p.setHidden(hidden);
        if (page != null)
            p.setPage(page);
        if (!string.IsNullOrEmpty(publicationId))
            p.setPublicationId(publicationId);
        if (characteristics != null)
            foreach (var c in characteristics)
                p.getCharacteristics().add(c);
        if (modifiers != null)
            foreach (var m in modifiers)
                p.getModifiers().add(m);
        if (modifierGroups != null)
            foreach (var mg in modifierGroups)
                p.getModifierGroups().add(mg);
        return p;
    }

    public static Characteristic CreateCharacteristic(string name, string typeId, string value = "")
    {
        var c = new Characteristic();
        c.setName(name);
        c.setTypeId(typeId);
        c.setValue(value);
        return c;
    }

    public static InfoGroup CreateInfoGroup(
        string id, string name, bool hidden = false,
        IEnumerable<Profile>? profiles = null,
        IEnumerable<net.battlescribe.model.data.Rule>? rules = null,
        IEnumerable<Modifier>? modifiers = null,
        string? publicationId = null, string? page = null,
        IEnumerable<ModifierGroup>? modifierGroups = null,
        IEnumerable<InfoGroup>? infoGroups = null)
    {
        var ig = new InfoGroup();
        ig.setId(id);
        ig.setName(name);
        ig.setHidden(hidden);
        if (profiles != null)
            foreach (var p in profiles)
                ig.getProfiles().add(p);
        if (rules != null)
            foreach (var r in rules)
                ig.getRules().add(r);
        if (modifiers != null)
            foreach (var m in modifiers)
                ig.getModifiers().add(m);
        if (modifierGroups != null)
            foreach (var mg in modifierGroups)
                ig.getModifierGroups().add(mg);
        if (infoGroups != null)
            foreach (var child in infoGroups)
                ig.getInfoGroups().add(child);
        if (!string.IsNullOrEmpty(publicationId))
            ig.setPublicationId(publicationId);
        if (!string.IsNullOrEmpty(page))
            ig.setPage(page);
        return ig;
    }

    public static ProfileType CreateProfileType(
        string id, string name,
        IEnumerable<CharacteristicType>? characteristicTypes = null)
    {
        var pt = new ProfileType();
        pt.setId(id);
        pt.setName(name);
        if (characteristicTypes != null)
            foreach (var ct in characteristicTypes)
                pt.getCharacteristicTypes().add(ct);
        return pt;
    }

    public static CharacteristicType CreateCharacteristicType(string id, string name)
    {
        var ct = new CharacteristicType();
        ct.setId(id);
        ct.setName(name);
        return ct;
    }

    public static InfoLink CreateInfoLink(
        string id, string name, string targetId, string type = "profile",
        bool hidden = false, IEnumerable<Modifier>? modifiers = null,
        string? publicationId = null, string? page = null,
        IEnumerable<ModifierGroup>? modifierGroups = null)
    {
        var il = new InfoLink();
        il.setId(id);
        il.setName(name);
        il.setTargetId(targetId);
        il.setType(type);
        il.setHidden(hidden);
        if (!string.IsNullOrEmpty(publicationId))
            il.setPublicationId(publicationId);
        if (!string.IsNullOrEmpty(page))
            il.setPage(page);
        if (modifiers != null)
            foreach (var m in modifiers)
                il.getModifiers().add(m);
        if (modifierGroups != null)
            foreach (var mg in modifierGroups)
                il.getModifierGroups().add(mg);
        return il;
    }

    public static CatalogueLink CreateCatalogueLink(
        string id, string name, string targetId, bool importRootEntries = true, string? type = null)
    {
        var cl = new CatalogueLink();
        cl.setId(id);
        cl.setName(name);
        cl.setTargetId(targetId);
        cl.setImportRootEntries(importRootEntries);
        if (!string.IsNullOrEmpty(type))
            cl.setType(type);
        return cl;
    }

    public static Publication CreatePublication(
        string id, string name, string shortName = "", string publisher = "",
        string publicationDate = "", string publisherUrl = "")
    {
        var pub = new Publication();
        pub.setId(id);
        pub.setName(name);
        if (!string.IsNullOrEmpty(shortName)) pub.setShortName(shortName);
        if (!string.IsNullOrEmpty(publisher)) pub.setPublisher(publisher);
        if (!string.IsNullOrEmpty(publicationDate)) pub.setPublicationDate(publicationDate);
        if (!string.IsNullOrEmpty(publisherUrl)) pub.setPublisherUrl(publisherUrl);
        return pub;
    }
}
