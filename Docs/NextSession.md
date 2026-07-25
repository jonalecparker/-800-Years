# Next Session

**Where we left off:** The castle-building graybox is live and playable. `GridPlacementSystem` handles click-drag placement (45°-locked lines), right-click-drag deletion, vertical stacking, and precise adjacent-cell targeting via an invisible reach-collider on every placed piece (`PlacementAssist`). A dark-grey grid overlay (`GridVisualizer`) fades smoothly with distance over a 120×120 ground plane with atmospheric fog. Walls use a real downloaded stone texture (`PavingStones115A`, albedo + normal). A data-driven bottom-bar build menu (`BuildMenu`) exists with one category ("Walls") and one item. The living design doc is `medieval-castle-lord.md` at the repo root.

## Top priorities

1. **Reconcile the grid system with the 7-23 design conversation.** That session discussed a spline-based wall tool with free rotation and soft-snap-to-socket (not a rigid grid), explicitly deferred until "the grid/wall prototype is visible." It's visible now — decide whether the fixed 1-unit grid that got built is the actual direction, or whether it's a stepping stone toward the spline tool.
2. **Continue the graybox scope**: floors, roofs, one premade room, and a first-person controller to walk what's built (still using a free-fly dev camera today).
3. **Check for a stray Claude Code session/window.** `WallSegment_Placeholder.prefab`'s scale changed mid-session (square → wide slab) without this session or the user making the edit — never identified. Worth ruling out another agent connected to the same Unity project before it happens again silently.
4. **Art pipeline step 1 (parallel lane, still not started):** buy one realistic modular medieval pack (on sale) → assemble a courtyard scene → HDRP lighting pass. Workflow: you judge the image, Claude supplies/applies settings.
5. **Design renown (#11)** — still the top undesigned system; may be the spine tying the three paths together.

## Backlog / open questions

- `BuildMenu` needs real categories/items once floors, roofs, and rooms exist as placeable objects.
- Wall material is missing roughness/AO (HDRP mask map channel packing) — skipped for scope, could revisit for surface fidelity.
- The two downloaded texture source files are large (33MB + 87MB, 4K originals; Unity import already caps them at 2048 in-engine) — consider downsizing the source PNGs to cut repo weight if it becomes a problem.
- `manage_texture`'s `set_import_settings` MCP action silently drops its payload — known bug, workaround is hand-editing the `.meta` file directly.
- Special rooms: which exist and what they do — #14 (feeds from, and into, the 1220 research pass)
- AI-prop bake-off: same 3–4 props through Tripo / Meshy / Rodin, judged in the courtyard
- Character Creator 5 trial — check medieval wardrobe availability before buying
- Walking liveliness beyond seasons+construction baseline — #12
- Hard-mode death: game over vs. heir — #4 remainder
- Liege as a mechanic (fealty, taxes, levies)? — #5 remainder
- What AI nobles actually simulate — #6 remainder
- Vassal departure/underperformance mechanics — #7 remainder (user still thinking)
- Alliances — ⏸ tabled by choice
- Deep-research pass: real lives of ~1220 landowners (doubles as art reference now)
