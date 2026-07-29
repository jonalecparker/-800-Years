# Next Session

**Where we left off:** The joint system got its unifying shape — **corner/T nodes** (`WallNode`): snapped ends cap flat on the shared point and an implicit round post renders every joint from one wedge rule (any angle, any member count, zero cut geometry). Face snapping works on both drag ends now, walls **never intersect** (through-crossings refuse red; end burials still fuse), snap ghosts are rectangles outside the host, and face-anchored walls land exactly perpendicular by default. All programmatically verified. Then the real decision: the user called the whack-a-mole pattern, and we chose to **rebuild the core on a node/edge graph** — relationships stored, not inferred; illegal states unrepresentable. `Docs/WallGraphRebuild.md` is the full executable brief (principles, auto-split crossings, what dies, what survives, build order).

## Top priorities

1. **Execute the wall-graph rebuild** — follow `Docs/WallGraphRebuild.md`. Nodes + edges as the stored model, edge meshing from the existing section sweep (always node-capped), node meshing from the existing wedge rule, placement as graph editing (click = node, drag = edge, crossing = auto-split into a 4-way node), delete as edge removal/splitting. Estimated one focused session for core, part of another for delete/courses/polish. **Do not extend the old junction machinery in the meantime.**
2. **Hand-playtest on the new model** — the long-overdue real castle layout: corners, tees, parallel courses, locked-top parapets on slopes, curves, Offset tool, deletes. Feel knobs carry over from the old toolkit: `endpointSnapRadius` (0.75), `lengthStep`/`angleStep`, `snapColor`, `heightScrollRate`, guide ranges — plus the new question: does always-on 15°-from-normal stepping on face anchors ever fight you?
3. **Design structural support (plan Step 4)** — now a graph traversal by construction (load paths over nodes/edges). Still needs the design call: **refuse unsupported placement, or let it collapse?** Vertical rests-on semantics wanted too.
4. **Back to the graybox program**: floors, roofs, one premade room, first-person controller. Rooms = enclosed faces of the planar wall graph — falls out of the rebuild. Round-tower tool (center + radius) is a natural spline-edge feature.
5. **Art pipeline step 1 (parallel lane, still not started):** buy one realistic modular medieval pack (on sale) → assemble a courtyard scene → HDRP lighting pass. Workflow: user judges the image, Claude supplies/applies settings.

## Backlog / open questions

- Old-model junction polish items (two-plane corner clips, S-connector, height-aware bands, `hasGaps` stale cuts) are **superseded by the graph rebuild** — joints become node-owned, gaps become edge splits. Don't work these.
- Matching height ≠ matching top on slopes (0.5m foundation steps at joints) — inherent; lock-top is the tool for level runs. Watch in playtest.
- Vertical delete doesn't span stacked spline courses — reconsider under graph delete semantics.
- `BuildMenu` needs real categories/items once floors/roofs/rooms exist; options panel has one toggle so far.
- Dirt texture tiling repeats visibly on the hill shoulder; world ends hard at the 800m terrain edge.
- Wall material lacks roughness/AO (HDRP mask map packing) — deferred for scope.
- Design renown (#11) — still the top undesigned system; may be the spine tying the three paths together.
- Special rooms: which exist and what they do — #14 (feeds from/into the 1220 research pass).
- AI-prop bake-off: same 3–4 props through Tripo / Meshy / Rodin, judged in the courtyard.
- Character Creator 5 trial — check medieval wardrobe availability before buying.
- Walking liveliness beyond seasons+construction baseline — #12.
- Hard-mode death: game over vs. heir — #4 remainder. Liege as a mechanic (fealty, taxes, levies)? — #5 remainder. What AI nobles actually simulate — #6 remainder. Vassal departure/underperformance — #7 remainder (user still thinking). Alliances — ⏸ tabled by choice.
- Deep-research pass: real lives of ~1220 landowners (doubles as art reference now).
- Editor gotcha (in Claude's memory too): MCP bridge auto-start is ON now; if tools ever report "Unity session not available" after an editor relaunch, see memory note `unity-mcp-bridge-recovery`.
- Stray Claude session mystery (unexplained prefab edit, 3 sessions back): never recurred, never explained.
