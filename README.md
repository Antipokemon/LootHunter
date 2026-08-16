# LootHunter

LootHunter is a standalone Dalamud plugin for building material lists and automatically farming ordinary open-world monster drops in FINAL FANTASY XIV.

## Runtime dependencies

LootHunter does **not** require Henchman or GatherBuddyReborn to be installed. The automation and farm-state logic are implemented directly in LootHunter.

Required capability plugins for the first version:

- **Lifestream** — teleportation
- **vnavmesh** — local pathfinding and movement
- **BossModReborn** — combat autorotation

LootHunter loads structured monster-drop and spawn data directly from the `LuminaSupplemental.Excel` NuGet package. GatherBuddyReborn was useful as a reference for how that dataset can be consumed, but it is not a runtime dependency. MonsterLootHunter is also not required at runtime; its data model was evaluated during design, while the structured LuminaSupplemental dataset is the primary source in this version.

## Current workflow

1. Create a named loot list.
2. Search monster-drop items and assign target quantities.
3. Choose either:
   - **Target inventory** — stop when total inventory reaches the requested amount.
   - **Gather additional** — collect the requested amount on top of the inventory count at session start.
4. Resolve all requested items to open-world monster sources and spawn clusters.
5. Build a global route that favors:
   - monsters that drop multiple still-needed items,
   - the current territory,
   - dense spawn groups,
   - sources with an unlocked same-territory aetheryte.
6. Refresh unlocked teleport destinations at farm start.
7. Use Lifestream to teleport when a territory change is needed.
8. Auto-mount for longer travel and optionally fly when the current zone supports it.
9. Use vnavmesh to cycle known spawn locations.
10. Find targets by `BNpcNameId`, not localized display names.
11. Check the live monster level before combat.
12. Dismount, select the target, and let the active/configured BossModReborn autorotation preset handle combat actions.
13. Compare inventory before and after the kill so completion is based on actual drops rather than kill count.
14. Re-plan immediately when requested quantities change.
15. Continue until every enabled list entry is complete.

## Safety and failure handling

LootHunter will not start when:

- the character is not logged in,
- the character is dead,
- the current class/job is not a combat job,
- the character is inside a duty,
- the normal inventory is full,
- required Lifestream, vnavmesh, or BossModReborn IPC is unavailable,
- no active/configured BossModReborn autorotation preset is available,
- an enabled list item has no usable open-world source.

Monster levels are checked twice where possible. Static source levels can be unknown in the primary dataset, so unknown static levels are warnings. The live `IBattleNpc.Level` is authoritative immediately before combat. With **Skip unsafe targets** enabled, a source that is above the configured level threshold is skipped and an alternate source is planned when available.

Unreachable or repeatedly failing sources are excluded for the current farm session so route planning does not endlessly select the same bad target. Empty spawn clusters are retried using the configured pass/respawn limits, then LootHunter switches to an alternate source when one exists.

## BossModReborn behavior

LootHunter uses BossModReborn's public preset IPC to preserve the user's current autorotation preset or temporarily activate the preset configured in LootHunter. It does not depend on Henchman's BossMod wrappers or its AI orchestration. Movement remains under LootHunter/vnavmesh control.

If **BossMod preset name** is blank, LootHunter uses the preset already active in BossModReborn. If a preset name is configured, LootHunter temporarily activates it for combat and restores the previous preset afterward.

## Commands

- `/loothunter` — open the main LootHunter window.

## Project layout

```text
LootHunter/
├── Automation/       Farm state machine and session tracking
├── Data/             Direct mob-drop/spawn database
├── IPC/              Lifestream, vnavmesh, and BossModReborn adapters
├── Models/           Loot lists, sources, clusters, plans, progress
├── Services/         Inventory, planning, targeting, mounting, safety
├── Windows/          List editor, session status, settings
├── Configuration.cs
├── Plugin.cs
└── LootHunter.csproj
```

## Custom plugin repository

`repo.json` is included at the repository root for use as a Dalamud custom plugin repository. Once the GitHub repository and first release exist, add this URL under **Dalamud Settings → Experimental → Custom Plugin Repositories**:

```text
https://raw.githubusercontent.com/Antipokemon/LootHunter/main/repo.json
```

The release must contain an asset named `LootHunter.zip`. The manifest download links use GitHub's `releases/latest/download/LootHunter.zip` endpoint, so the URLs do not need to change for each release. Bump `AssemblyVersion` (and `TestingAssemblyVersion` when used) in `repo.json` whenever publishing a new plugin version.

## Current limitations

- BossModReborn is the first combat provider. The interface is intentionally isolated so another provider can be added later without changing the planner/state machine.
- The primary drop/spawn dataset does not provide a reliable static monster level for every source. Live monsters are checked before engagement.
- A source in another territory requires an unlocked teleport destination in that territory for this version.
- Aethernet shard optimization and non-teleport inter-zone traversal are not implemented yet.
- This source tree has not been locally compiled in the current workspace because the environment does not contain the .NET SDK/Dalamud development toolchain.

## Independence / licensing note

LootHunter is independently implemented. Henchman's high-level farming behavior was used only as design inspiration; no Henchman source, library, IPC wrapper, submodule, or installation dependency is included.
