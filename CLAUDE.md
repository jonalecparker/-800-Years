# -800 Years — working notes for Claude

Unity 6 / HDRP castle-builder graybox. Session workflow: `/start` to recap, `/wrap` to archive+commit — `Docs/NextSession.md` and `Docs/SessionLog/` are the project memory; read them, not this file, for current state.

## The wall graph (2026-07-29)

The wall system is a **node/edge graph** (`Docs/WallGraphRebuild.md` was the brief; it shipped 2026-07-29). Relationships are **stored, never inferred** — the old physics-probe machinery (junction bands, graze lanes, cut planes/ClipRow, the 10° gate, IsBlocked, buildOrder asymmetry, ignore lists, end extensions, Links, hasGaps) is deleted, not dormant. Don't reintroduce inference; every structural question is a graph question.

- **Model**: `WallNode` (point + member list) and `WallEdge` (exactly two nodes + quadratic Bézier `control`, height data, sections). `WallGraph` (static) is the ONLY mutator: `CommitEdge`, `CommitCourse`, `DeleteSections`, `SplitEdgeAtMany`. Edges meet only at nodes and never overlap or cross — illegal states are unrepresentable. An edge's geometry depends on nothing but its own nodes/control/heights/terrain, so **nothing ever rebuilds because a neighbour changed** (only node posts remesh).
- **Placement is graph editing** (`GridPlacementSystem`, name kept for scene refs): click = node, drag = edge. Snaps resolve to nodes: `TryNodeSnap` (any node within 0.75m), then `TryFlankSnap` (mid-edge **centerline** point; commit splits the host there — a T at any angle, host end zones ±0.9m excluded). Free ends get 1-valent nodes. **Crossings auto-split**: `FindCrossings` (polyline intersection, no physics) → each becomes a shared 4-way node — both walls split via exact polar-form subdivision (`SubCurve`), gold post ghosts preview the pending joints. Crossings within 0.35m of a gesture end collapse into the end's node; within 0.3m of an existing node they reuse it (chain-through).
- **The one refusal left** (`OverlapsExisting`, whole ghost red): a near-exact redraw along an existing wall — <3° relative tangent, <0.15m centerline distance, sustained >0.6m. Everything else is legal: shallow V's, flush parallels, drags across a flank anchor's host (now just a T + crossing, no more red).
- **Nodes own all joints** (`WallNode.RebuildMesh`, the old wedge rule promoted to sole renderer): implicit post radius = half thickness, member inward bearings sorted, each gap exposes gap−180° of arc. Valence 1 = nothing (the edge's flat cap IS the finished end — free ends and delete-hole boundaries cap flat), collinear butt = nothing, L = quarter barrel, 90° T/X = flush (zero exposed arc), shallow tee = one shoulder arc. Flank members no longer exist — a T splits the host so ALL members are end members. Posts read end-section spans and **climb course stacks** (`tMin`/`tMax` reaching the end), so posts rise with courses.
- **Courses are edges too**: `stackBase` set, same nodes/curve as the base (so all t parameters agree), sections copy the base slices with `bottomY = below.topY`. They're never node members and never split the planar graph. `UncoveredRanges` guards double-stacking (preview + commit both use it). Splitting/deleting a based edge remaps the whole stack over the survivors (`RebuildStackOver`) — masonry never floats.
- **Delete is edge surgery** (`DeleteSections`): kept section ranges → sub-edges; a mid-hole makes two new 1-valent tip nodes; a fully-deleted edge detaches and empty nodes dissolve (`OnMemberGone`). No linked rebuilds, no gap flags — survivors were never derived from the dead.
- Assists: length stepping relative to the anchor (`lengthStep` 0.5, Alt frees); direction is **fully free everywhere** — Shift steps it in `angleStep` 5° increments (absolute bearings on free drags, **measured from the face normal** on flank-anchored drags, so a lazy Shift-drag off a face lands exactly 90° flush); snapped-height adoption (anchor > drag-end > hover; Ctrl+scroll overrides via `heightOverride`); lock-top; distance guides; angle arc; Offset tool (commits through `CommitEdge`, so it splits what it crosses); Shift tangent-tied connector (only when both snapped ends have a single outward direction — valence-1 node or flank). Node-snap hovers highlight the members' existing end sections gold (no stub, no post markers — posts preview only at pending crossing points); a bare click on a node commits nothing.

## Vertical math is spans, not layers

- `WallEdgeSection.bottomY/topY` is the source of truth. Never assume uniform section height or ground at y=0 (terrain exists; slice ground = min of sampled corners, quantized down by `baseStep`).
- Bulge (0.5m) and foundation bases (0.5m) are deliberately **stepped** so separately-built structures align — don't make those continuous. Wall height is the exception: it scrolls **smoothly** (Ctrl+scroll, `heightScrollRate` 0.25m/notch, raw ±120 Windows deltas normalized), and alignment comes from snapping — a snapped ghost adopts the host's exact height. A **locked-top host matches by top elevation**, not stored height (`SnapHeightOf` = `fixedTopY` − ground at the snap), and `fixedTopY` survives splits.

## Edge geometry

- Section meshes are watertight only because neighbors evaluate the same curve at the same boundary parameter from one shared arc table. Section `tStart/tEnd` always lie in [0,1] now — the extended-arc machinery died with end extensions; edges cap flat exactly ON their node points.
- Section meshes are runtime-owned (`ownedMesh`, destroyed with the section). Preview meshes are preview-owned and always destroyed on rebuild/cancel — commit rebuilds fresh through the graph (no ownership transfer anymore).
- Colliders exist for cursor raycasts only (targeting, course hover, delete) — never for occupancy.

## Input arbitration

- The scroll wheel has four meanings: camera dolly (default), wall height (Ctrl), curve bulge (bend phase), offset distance (Offset tool with a source selected). The last two claim the wheel via `GridPlacementSystem.ClaimingScrollWheel`; `FreeFlyCamera` checks it and Ctrl before dollying — any new wheel use must join this arbitration.
- No trigger colliders exist in the build scene, but raycast/overlap code still filters `isTrigger` — keep those filters when adding new triggers, or placement targeting will misread them.

## Tooling gotchas

- `manage_texture`'s `set_import_settings` MCP action silently drops its payload — set import settings via `execute_code` (editor API) or hand-edit the `.meta`.
- HDRP motion blur smears screenshots taken the frame after a camera teleport — capture twice, keep the second.
- After script edits: `refresh_unity` (compile) + `read_console` for errors before proceeding; play-mode `execute_code` tests that delete-then-rebuild must use `DestroyImmediate` (deferred `Destroy` races validation).
