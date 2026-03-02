using net.battlescribe.model.data;
using net.battlescribe.model.roster;

namespace BattleScribeSpec;

/// <summary>
/// Factory for creating Java BattleScribe model objects (via IKVM) for use in oracle tests.
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
        IEnumerable<ProfileType>? profileTypes = null)
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

        return gs;
    }

    /// <summary>
    /// Create a CostType.
    /// </summary>
    public static CostType CreateCostType(
        string id,
        string name,
        double defaultCostLimit = -1.0,
        bool hidden = false,
        bool limit = false)
    {
        var ct = new CostType();
        ct.setId(id);
        ct.setName(name);
        ct.setDefaultCostLimit(defaultCostLimit);
        ct.setHidden(hidden);
        ct.GetType().GetMethod("setLimit")?.Invoke(ct, [limit]);
        return ct;
    }

    /// <summary>
    /// Create a CategoryEntry.
    /// </summary>
    public static CategoryEntry CreateCategoryEntry(string id, string name, bool hidden = false)
    {
        var ce = new CategoryEntry();
        ce.setId(id);
        ce.setName(name);
        ce.setHidden(hidden);
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
        IEnumerable<ForceEntry>? forceEntries = null)
    {
        var fe = new ForceEntry();
        fe.setId(id);
        fe.setName(name);
        fe.setHidden(hidden);

        if (categoryLinks != null)
            foreach (var cl in categoryLinks)
                fe.getCategoryLinks().add(cl);

        if (forceEntries != null)
            foreach (var child in forceEntries)
                fe.getForceEntries().add(child);

        return fe;
    }

    /// <summary>
    /// Create a CategoryLink linking a ForceEntry to a CategoryEntry.
    /// </summary>
    public static CategoryLink CreateCategoryLink(string id, string targetId, string name, bool primary = false)
    {
        var cl = new CategoryLink();
        cl.setId(id);
        cl.setTargetId(targetId);
        cl.setName(name);
        cl.setPrimary(primary);
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
        IEnumerable<Cost>? costs = null,
        IEnumerable<Constraint>? constraints = null,
        IEnumerable<Modifier>? modifiers = null,
        IEnumerable<CategoryLink>? categoryLinks = null)
    {
        var el = new EntryLink();
        el.setId(id);
        el.setName(name);
        el.setTargetId(targetId);
        el.setType(type);
        el.setHidden(hidden);

        if (costs != null)
            foreach (var c in costs)
                el.getCosts().add(c);

        if (constraints != null)
            foreach (var c in constraints)
                el.getConstraints().add(c);

        if (modifiers != null)
            foreach (var m in modifiers)
                el.getModifiers().add(m);

        if (categoryLinks != null)
            foreach (var cl in categoryLinks)
                el.getCategoryLinks().add(cl);

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
        IEnumerable<SelectionEntry>? selectionEntries = null,
        IEnumerable<EntryLink>? entryLinks = null,
        IEnumerable<SelectionEntry>? sharedSelectionEntries = null,
        IEnumerable<SelectionEntryGroup>? sharedSelectionEntryGroups = null,
        IEnumerable<Rule>? sharedRules = null,
        IEnumerable<Profile>? sharedProfiles = null,
        IEnumerable<InfoGroup>? sharedInfoGroups = null)
    {
        var cat = new Catalogue();
        cat.setId(id);
        cat.setName(name);
        cat.setGameSystemId(gameSystemId);
        cat.setRevision(revision);
        cat.setBattleScribeVersion(bsVersion);
        cat.setAuthorName("Test");

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
        bool collective = false)
    {
        var se = new SelectionEntry();
        se.setId(id);
        se.setName(name);
        se.setType(type);
        se.setHidden(hidden);
        se.setCollective(collective);

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
        IEnumerable<SelectionEntry>? selectionEntries = null,
        IEnumerable<Constraint>? constraints = null,
        IEnumerable<Modifier>? modifiers = null)
    {
        var seg = new SelectionEntryGroup();
        seg.setId(id);
        seg.setName(name);
        seg.setHidden(hidden);
        if (!string.IsNullOrEmpty(defaultSelectionEntryId))
            seg.setDefaultSelectionEntryId(defaultSelectionEntryId);

        if (selectionEntries != null)
            foreach (var se in selectionEntries)
                seg.getSelectionEntries().add(se);

        if (constraints != null)
            foreach (var c in constraints)
                seg.getConstraints().add(c);

        if (modifiers != null)
            foreach (var m in modifiers)
                seg.getModifiers().add(m);

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
        bool includeChildForces = false)
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
        IEnumerable<Modifier>? modifiers = null)
    {
        var r = new net.battlescribe.model.data.Rule();
        r.setId(id);
        r.setName(name);
        r.setDescription(description);
        r.setHidden(hidden);
        if (!string.IsNullOrEmpty(page))
            r.setPage(page);
        if (modifiers != null)
            foreach (var m in modifiers)
                r.getModifiers().add(m);
        return r;
    }

    public static Profile CreateProfile(
        string id, string name, string typeId = "", string typeName = "",
        bool hidden = false,
        IEnumerable<Characteristic>? characteristics = null,
        IEnumerable<Modifier>? modifiers = null)
    {
        var p = new Profile();
        p.setId(id);
        p.setName(name);
        p.setTypeId(typeId);
        p.setTypeName(typeName);
        p.setHidden(hidden);
        if (characteristics != null)
            foreach (var c in characteristics)
                p.getCharacteristics().add(c);
        if (modifiers != null)
            foreach (var m in modifiers)
                p.getModifiers().add(m);
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
        IEnumerable<Modifier>? modifiers = null)
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
        bool hidden = false, IEnumerable<Modifier>? modifiers = null)
    {
        var il = new InfoLink();
        il.setId(id);
        il.setName(name);
        il.setTargetId(targetId);
        il.setType(type);
        il.setHidden(hidden);
        if (modifiers != null)
            foreach (var m in modifiers)
                il.getModifiers().add(m);
        return il;
    }

    public static CatalogueLink CreateCatalogueLink(
        string id, string name, string targetId, bool importRootEntries = true)
    {
        var cl = new CatalogueLink();
        cl.setId(id);
        cl.setName(name);
        cl.setTargetId(targetId);
        cl.setImportRootEntries(importRootEntries);
        return cl;
    }
}
