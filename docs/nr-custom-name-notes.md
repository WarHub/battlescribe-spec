# NR Custom Name & Notes Investigation

> Discovered April 2026 via live Playwright probing of [newrecruit.eu](https://newrecruit.eu).
> Verified visually in the NR UI and via programmatic Pinia store inspection.

## Summary

NR supports `customName` and `note` (custom notes) on both **force** and
**selection** instance nodes. These are premium (supporter-only) features
gated by the NR paywall, but the data model fully supports them regardless
of subscription status.

## Property Access

Both properties exist directly on instance nodes (own properties):

```javascript
// Instance own properties include: customName, note
'customName' in instance  // → true
'note' in instance        // → true

// Default value when not set
instance.customName       // → undefined (not null, not "")
instance.note             // → undefined

// Setting values
instance.customName = "Commander Bob";
instance.note = "Bob is the bravest";
```

### Key Naming Difference: `note` vs `customNotes`

NR uses **`note`** internally, NOT `customNotes`:

| Context | Property name | Example |
|---------|--------------|---------|
| NR instance property | `.note` | `sel.note = "notes text"` |
| NR JSON serialization | `note` | `{ customName: "Bob", note: "notes" }` |
| BattleScribe XML | `customNotes` | `<selection customNotes="notes">` |
| Adapter mapping | `.note` → `CustomNotes` | `customNotes: sel.note \|\| null` |

The adapter in `JsHelpers.cs` correctly maps `sel.note` → `customNotes` and
`f.note` → `customNotes` to match the BattleScribe XML attribute name.

### getName() Does NOT Return Custom Name

```javascript
instance.getName()        // → "Test Unit" (always the DEFINITION name)
instance.customName       // → "Commander Bob" (the custom name)
// NR UI renders: "Commander Bob - Test Unit"
```

There is a `getCustomName()` method that returns the custom name when set.

## UI Display Format

NR renders custom names as **"CustomName - OriginalName"**:

| Element | Display |
|---------|---------|
| Force with customName | "My Custom Force - Test Force" |
| Selection with customName | "Commander Bob - Test Unit" |

### Where Notes Are Visible

| Element | Note visible? | Location |
|---------|--------------|----------|
| **Selection** | ✅ Yes | Shown as text block below the unit header in the expanded detail panel |
| **Force** | ❌ Not visible | Force notes exist in the data model but have no visible UI location in the current NR version |

The force `note` property persists correctly in serialization and can be read
back, but NR's current UI (v34.47) only displays the force `customName` — there
is no visible area for force notes.

## Premium Paywall

Custom Names and Notes editing is a **supporter-only** feature in NR:

- Clicking the pencil "Edit Force Name" or "Edit Name/Note" icons shows a popup:
  *"Support New Recruit to unlock Custom Names and Notes!"*
- The data model supports the feature regardless — setting values via JS works

### Bypassing the Paywall for Testing

The paywall check uses `userStore.isSupporter()`. Setting a fake user object
with `supporter: true` bypasses it:

```javascript
const pinia = document.querySelector('#__nuxt')
    ?.__vue_app__?.config?.globalProperties?.$pinia;
const userStore = pinia._s.get('userStore');

userStore.user = {
    supporter: true,
    name: 'TestUser',
    _id: 'fake-supporter'
};

// Verify
userStore.isSupporter();  // → true
// The "Edit Force Name" pencil now opens an inline editor instead of the paywall
```

After the bypass:
- Force custom name becomes editable inline (text field replaces display)
- The "?" badge next to the roster name changes to a user avatar
- "Support" nav link changes to "Profile"

## JSON Serialization

NR's `toJsonObject()` serializes custom name and notes:

### Force serialization
```javascript
forceInstance.toJsonObject()
// → { name: "Test Force", option_id: "fe-1",
//     customName: "My Custom Force", note: "Force tactical notes",
//     catalogue_id: "...", options: [...] }
```

### Selection serialization
```javascript
selectionInstance.toJsonObject()
// → { name: "Test Unit", option_id: "se-1",
//     customName: "Commander Bob", note: "Bob is the bravest",
//     amount: 1, options: [...] }
```

## Adapter Mapping

The adapter reads these properties in `JsHelpers.cs`:

```javascript
// Force extraction (extractForce)
customName: f.customName || null,
customNotes: f.note || null        // note → customNotes mapping

// Selection extraction (extractSelection)
customName: sel.customName || null,
customNotes: sel.note || null      // note → customNotes mapping
```

Mapped to C# state in `NewRecruitStateReader.cs`:
- `NrForceSnapshot.CustomName` → `ForceState.CustomName`
- `NrForceSnapshot.CustomNotes` → `ForceState.CustomNotes`
- `NrSelectionSnapshot.CustomName` → `SelectionState.CustomName`
- `NrSelectionSnapshot.CustomNotes` → `SelectionState.CustomNotes`

## Pinia Store Access (Live Roster)

The live roster tree is accessible at:

```javascript
const pinia = document.querySelector('#__nuxt')
    ?.__vue_app__?.config?.globalProperties?.$pinia;
const listsPage = pinia._s.get('listsPage');
const army = listsPage.editedList.army;

// Force instances
const forces = army.getForces();  // → ForceInstance[]
forces[0].customName              // → "My Custom Force"
forces[0].note                    // → "Force tactical notes"

// Selection instances
const selections = forces[0].getSelections();
selections[0].customName          // → "Commander Bob"
selections[0].note                // → "Bob is the bravest"
```

> **Note:** `figurineStore.book.getForces()` returns force **entries** (definitions),
> not roster forces. Use `listsPage.editedList.army` for the live roster tree.
