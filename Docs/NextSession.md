# Next Session

**Where we left off:** The castle builder now runs on realistic procedural terrain (800×800m countryside with a central buildable hill, `TerrainGenerator`) with **two wall systems**: straight grid walls (cell-based, spans, groups) and **spline walls** (`SplineWall` — first-class curved wall objects, uniform equal-arc sections, three-click place-and-bend flow, course stacking by clicking a wall top, wall-following delete). Walls have variable height (Ctrl+scroll, ×0.25–×3), follow terrain with stepped foundations, and connect to each other: crossings ≥25° interpenetrate as junctions, endpoints resolve through the grid-adjacency helpers, and wall ends extend backward into whatever wall stands behind them. The junction toolkit was verified programmatically at end of session but **not yet hand-played**.

## Top priorities

1. **Playtest the junction toolkit.** Adjacent-cell joins against both wall kinds, T-joins, branching off a wall flank, endpoint chaining, one-cell-away parallel placement (should work), drawing a duplicate along an existing wall (should refuse). Expect tuning: the 25° crossing threshold, the 0.75m/1.3m snap and probe reaches.
2. **Continue the graybox on the new foundations**: floors, roofs, one premade room, and a first-person controller to walk what's built. A round-tower tool (center + radius) is a natural next spline feature and very castle-authentic.
3. **Spline polish backlog** (pick by pain): tangent-matched endpoint chaining (no kinks), shallow-angle (<25°) crossings, unifying the straight tool into the spline system (a straight wall is a bulge-0 spline — would collapse two systems into one).
4. **Art pipeline step 1 (parallel lane, still not started):** buy one realistic modular medieval pack (on sale) → assemble a courtyard scene → HDRP lighting pass. Workflow: you judge the image, Claude supplies/applies settings.
5. **Design renown (#11)** — still the top undesigned system; may be the spine tying the three paths together.

## Backlog / open questions

- Grid pieces can't stack on spline walls; vertical delete doesn't span stacked spline courses.
- Straight box pieces still stretch their texture with height (curved walls fixed via world-anchored UVs).
- Dirt texture tiling repeats visibly on the hill shoulder; world ends hard at the 800m terrain edge.
- `BuildMenu` needs real categories/items once floors/roofs/rooms exist.
- Wall material lacks roughness/AO (HDRP mask map packing) — deferred for scope.
- `manage_texture`'s `set_import_settings` MCP action silently drops its payload — hand-edit `.meta` or use editor API via `execute_code`.
- Terrain textures are 1K JPGs (repo-friendly); 4K wall texture sources are still large (33MB + 87MB) if repo weight becomes a problem.
- Stray Claude session mystery (unexplained prefab edit two sessions ago): never recurred, never explained.
- Special rooms: which exist and what they do — #14 (feeds from/into the 1220 research pass)
- AI-prop bake-off: same 3–4 props through Tripo / Meshy / Rodin, judged in the courtyard
- Character Creator 5 trial — check medieval wardrobe availability before buying
- Walking liveliness beyond seasons+construction baseline — #12
- Hard-mode death: game over vs. heir — #4 remainder
- Liege as a mechanic (fealty, taxes, levies)? — #5 remainder
- What AI nobles actually simulate — #6 remainder
- Vassal departure/underperformance mechanics — #7 remainder (user still thinking)
- Alliances — ⏸ tabled by choice
- Deep-research pass: real lives of ~1220 landowners (doubles as art reference now)
