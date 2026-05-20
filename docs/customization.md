# Customization (Custom Name & Notes)

BattleScribe allows users to assign a **custom name** and **custom notes** to roster
entities — forces, selections, and categories. This is a user-facing labeling feature
for personalizing roster printouts and organization.

## Supported entities

| Entity | customName | customNotes |
|--------|:----------:|:-----------:|
| Force | ✅ | ✅ |
| Selection | ✅ | ✅ |
| Category | ✅ | ✅ |

## Engine compatibility

| Engine | Force | Selection | Category |
|--------|:-----:|:---------:|:--------:|
| BattleScribe (IKVM) | ✅ | ✅ | ✅ |
| BattleScribe (UI Driver) | ✅ | ✅ | ✅ |
| NewRecruit | ✅ | ✅ | ❌ |

NewRecruit does not support category-level customization. When a spec sets
`categoryEntryId`, the NR adapter silently ignores it. Use per-engine assertion
overrides to omit category custom fields from NR expected state.

NR maps `customNotes` to its internal `note` field on forces and selections.

## Supporter / paywall gating

Both BattleScribe and NewRecruit gate access to the customization feature behind
supporter (paid) status:

### BattleScribe Desktop

- The **edit panel "Customise" button** (in the right-hand toolbar) checks
  `.isSupporter()` and shows a dialog prompting upgrade if the user is not a supporter.
- The **context menu "Customise Name..."** item (right-click on any tree item) does
  **NOT** check supporter status — it directly calls `showCustomiseSelectableDialog`.
- The BS UI engine adapter uses the context menu code path to bypass the paywall check.

### NewRecruit

- The **"Custom Name" button** in the selection details panel shows a "Supporter Only"
  toast for non-supporter users.
- Our NR adapter sets customization via direct Pinia store manipulation, bypassing the
  UI paywall check.

## Spec action

```yaml
- action: setCustomization
  forceId: ${{ steps.add-force.forceId }}       # required — target force
  selectionId: ${{ steps.select-unit.selectionId }}  # optional — target selection
  categoryEntryId: cat-hq                       # optional — target category (by entryId)
  customName: My Custom Name                    # optional
  customNotes: My notes here                    # optional
```

**Targeting rules:**
- `forceId` is always required (identifies the force context)
- If `categoryEntryId` is set → customization applies to that category
- Else if `selectionId` is set → customization applies to that selection
- Else → customization applies to the force itself

## State representation

Custom fields appear in expected state assertions:

```yaml
- expectedState:
    forces:
      - customName: Alpha Strike Force
        customNotes: Main battle group
        categories:
          - name: HQ
            entryId: cat-hq
            customName: Command HQ
            customNotes: Deployment zone alpha
        selections:
          - name: Commander
            customName: The General
            customNotes: Veteran commander
```

## Spec files

| Spec | Description |
|------|-------------|
| `customization/customization-force.yaml` | Sets customName + customNotes on a force |
| `customization/customization-selection.yaml` | Sets customName + customNotes on a selection |
| `customization/customization-category.yaml` | Sets customName + customNotes on a category (NR skipped) |
| `protocol/protocol-kitchen-sink.yaml` | Steps 4c–4e exercise all three entity types |
