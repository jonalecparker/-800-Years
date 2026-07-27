# Next Session

**Where we left off:** The building system got its foundational rework: **one wall system** (every wall is a `SplineWall`; straight = bulge-0 spline), **no grid** (free placement; `SnapToGrid`, `PlacedPiece`, `GridVisualizer` all deleted), **modeled connections** (`SplineWall.Links` — deleting a wall retracts neighbors' buried stubs and regrows junction gaps), and **cut junction geometry** (newer walls clip flush against older walls' faces via per-host bands + row clipping — crossings, T-joins, shallow angles, leans that fuse, multi-wall junctions). Assists replaced the grid: 0.5m length stepping relative to the anchor (direction freeform, Shift snaps 15°, Alt frees all), 4-sector distance guides, an Offset tool for locked-distance parallels, and red ghosts where placement is refused. All of it is **programmatically verified but not hand-played**.

## Top priorities

1. **Hand-playtest the free building toolkit.** Draw a real castle layout: freeform drags, curves, T-joins, crossings, chained corners, the Offset tool, course stacking, deleting walls out of a network (watch stubs retract / gaps regrow). Everything feel-related is provisional: `lengthStep` (0.5m), Shift-snap `angleStep` (15°), guide range/colors, guide sectors run-relative vs world-aligned (one-line change), offset distance stepping.
2. **Design structural support (plan Step 4)** — the last pillar of "freedom limited only by physics," riding on the `Links` graph (support = a path to ground + bearing rules). Needs a design decision first: **refuse unsupported placement, or let it collapse?** Also wants vertical rests-on semantics (course→base links already form via expanded-box overlap).
3. **Junction polish by pain** (pick after playtest): corner clips at a host's *end* still leave a sliver (two-plane nearest-face clip would fix); bands aren't height-aware (a wall taller than its host carries the gap full height); tangent-matched endpoint chaining (no-kink curves) still pending.
4. **Back to the graybox program**: floors, roofs, one premade room, first-person controller to walk what's built. Round-tower tool (center + radius) is a natural next spline feature.
5. **Art pipeline step 1 (parallel lane, still not started):** buy one realistic modular medieval pack (on sale) → assemble a courtyard scene → HDRP lighting pass. Workflow: user judges the image, Claude supplies/applies settings.

## Backlog / open questions

- `hasGaps` walls never rebuild → stale cuts possible; base-wall rebuild under stacked courses can shift cut boundaries slightly on slopes.
- Vertical delete doesn't span stacked spline courses.
- `BuildMenu` needs real categories/items once floors/roofs/rooms exist.
- Dirt texture tiling repeats visibly on the hill shoulder; world ends hard at the 800m terrain edge.
- Wall material lacks roughness/AO (HDRP mask map packing) — deferred for scope.
- `manage_texture`'s `set_import_settings` MCP action silently drops its payload — hand-edit `.meta` or use editor API via `execute_code`.
- Design renown (#11) — still the top undesigned system; may be the spine tying the three paths together.
- Special rooms: which exist and what they do — #14 (feeds from/into the 1220 research pass).
- AI-prop bake-off: same 3–4 props through Tripo / Meshy / Rodin, judged in the courtyard.
- Character Creator 5 trial — check medieval wardrobe availability before buying.
- Walking liveliness beyond seasons+construction baseline — #12.
- Hard-mode death: game over vs. heir — #4 remainder. Liege as a mechanic (fealty, taxes, levies)? — #5 remainder. What AI nobles actually simulate — #6 remainder. Vassal departure/underperformance — #7 remainder (user still thinking). Alliances — ⏸ tabled by choice.
- Deep-research pass: real lives of ~1220 landowners (doubles as art reference now).
- Stray Claude session mystery (unexplained prefab edit, 3 sessions back): never recurred, never explained.
