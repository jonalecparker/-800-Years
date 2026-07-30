# Next Session

**Where we left off:** The **wall-graph rebuild shipped** — all six steps of `Docs/WallGraphRebuild.md` in one session. The model is `WallNode` + `WallEdge` + `WallGraph`: edges meet only at nodes, crossings auto-split into shared 4-way nodes, delete is edge surgery, courses cascade with their base, and ~1,200 lines of inference machinery (`SplineWall` et al.) are deleted. Ten programmatic play-mode checks green. Then four same-day UX rounds from live feedback: flat free ends, face-flush ghost trims, node hovers that highlight the existing end masonry (no stubs, no round post markers — the user's own insight: the node is the anchor, angle is free), and full angle freedom with Shift = 5° stepping. User's pick for next time: **floors and roofs**.

## Top priorities

1. **Floors and roofs** — rooms = enclosed faces of the planar wall graph (the rebuild was partly for this). Needs: face enumeration over nodes/edges, a floor fill per enclosed face, roof geometry over rooms, BuildMenu entries. Design call on the way in: flat floors only, or terrain-following? Roof shapes (shed/gable/hip) worth a quick options ask.
2. **Hand-playtest a real castle layout** — still never done; two sessions of programmatic-only verification. Corners, tees, crossings, parallel courses, locked-top parapets on slopes, curves, Offset tool, deletes. Feel knobs: `endpointSnapRadius` (0.75), `lengthStep` (0.5) / `angleStep` (5, Shift-only), snap tint, guide ranges, crossing tolerances (end-collapse 0.35, node-reuse 0.3, merge 0.5).
3. **Design structural support (plan Step 4)** — load paths are now literal graph traversals. Still needs the design call: **refuse unsupported placement, or let it collapse?** Vertical rests-on semantics wanted too.

## Backlog / open questions

- Known graph edges (minor, watch in playtest): 0.75–0.9m dead sliver near node ends where neither snap fires; Alt placement can bury an end inside a wall with no graph connection (snapping is the collapse mechanism — is a backstop needed?); 2-valent collinear nodes from deletes never re-merge; drag ghosts from a node overlap the host near the joint (honest — commit does too).
- Refusal gate is deliberately narrow (<3°, <0.15m, >0.6m run) and crossings split at ANY angle — sliver crossings under ~10° may look rough; tune after hand-testing.
- Round-tower tool (center + radius) — natural spline-edge feature now.
- Vertical delete doesn't span stacked courses — reconsider under graph delete semantics.
- `BuildMenu` needs real categories/items once floors/roofs exist; options panel has one toggle so far.
- Dirt texture tiling repeats on the hill shoulder; world ends hard at the 800m terrain edge.
- Wall material lacks roughness/AO (HDRP mask map packing) — deferred for scope.
- Art pipeline step 1 (parallel lane, not started): buy one realistic modular medieval pack (on sale) → courtyard scene → HDRP lighting pass. User judges the image, Claude supplies settings.
- Design renown (#11) — top undesigned system; may tie the three paths together.
- Special rooms: which exist and what they do — #14 (feeds from/into the 1220 research pass).
- AI-prop bake-off (Tripo / Meshy / Rodin) judged in the courtyard; Character Creator 5 trial — check medieval wardrobe first.
- Walking liveliness beyond seasons+construction — #12. Hard-mode death (game over vs. heir) — #4 remainder. Liege as a mechanic — #5. What AI nobles simulate — #6. Vassal departure — #7 (user thinking). Alliances — ⏸ tabled.
- Deep-research pass: real lives of ~1220 landowners (doubles as art reference).
- Editor gotcha: MCP bridge auto-start is ON; if "Unity session not available" after a relaunch, see memory note `unity-mcp-bridge-recovery`. Don't leave play mode running across recompiles — a mid-play domain reload orphans runtime walls and spams "missing script" (transient but confusing).
