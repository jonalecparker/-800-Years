# Next Session

> Long arc: `Docs/Roadmap.md` (agreed 2026-07-30) — five phases from
> builder to siege. Phase 1 is current work; its remaining ⚠ design
> session (structural support) comes before its code.

**Where we left off:** The **stairs half** of the ⚠ storeys/stairs design call, shipped — and then everything it took to make a stair usable. `WallStair` is a rider on the graph (like `WallRoom`), a chain of spans following the masonry through its nodes, one section per step, with the length *derived* from the rise so there is no pitch refusal. `CutawayView` slices the world at one plane so you can see and build inside a roofed room. Stairs got a 2.5m landing; roofs got a **well** cut through them. Doors landed as `WallEdge.Opening` — stored on the edge, a lintel being a section with a raised bottom — and then learned that a doorway serves a **floor**, not the ground it stands on (`sill`, threshold blocks, stairs that climb *to* a doorway). Last fix of the session: cutting a doorway no longer re-divides the wall around it. **Both halves of the storeys/stairs design call are now done.**

## Top priorities

1. **Playtest round three** (`Docs/Stairs.md` build order, step 5). Nothing from 08-03/08-04 past the landings and wells is hand-confirmed. Watch specifically: the landing's feel at 2.5m; whether the well's headroom is generous enough to walk rather than clip; the 5cm coplanar lip along the landing's outer edge; the raised sills and threshold blocks; the door tool's step readout; and stairs arriving *at* a doorway rather than setting off from it.
2. **Finish the centre-castle playtest** — now interrupted across three sessions.
3. **Phase 1 remainder** (`Docs/Roadmap.md`): wall types ladder (strength progression — feeds procedural detailing), gates/gatehouse, structural support ⚠ design call.
4. **Graduate the two design briefs.** `Docs/Stairs.md` and `Docs/Doors.md` move into `CLAUDE.md` and become history once they've survived a playtest or two — same lifecycle as `WallGraphRebuild.md`. The cross-cutting constraints are already in `CLAUDE.md`; the rationale isn't.

## Backlog / open questions

- **A room inside a room may lay a second floor slab coplanar with the first** (found 08-04, **not confirmed**). `BuildFloor`'s elevated-room branch needs `wallBase > floorY + 0.25`, and a deck sitting exactly one base step above the terrain doesn't clear it. Observed both rooms reporting `floorY` 54.50 on near-level ground but never checked whether a second slab object existed. On sloped ground the branch fired correctly.
- **A doorway with 0.6m–2.2m of wall above its sill becomes a full-height gateway** rather than a doorway (`DoorRefusal` demands 0.6m; a lintel needs head + 0.1). Intentional for a low wall with the sill at grade; may read as damage where the sill was *raised*.
- **Structural support is deliberately absent** (user's call): nothing stores what rests on what, so deleting a lower wall leaves masonry hanging in the air. `baseY` alone is enough to reconstruct support when the design call lands — don't add a spatial "what's under me" query in the meantime.
- **The well's 5cm lip** leaves a coplanar strip of deck over the landing's outer edge. If it shimmers, the honest fix is a real polygon difference (the well as a notch in the outline), not a bigger lip.
- **Mid landing / dog-leg** — needs a real floating platform object at an intermediate base, not geometry the stair can grow. Also deferred: spiral/newel stairs (one quadratic can't turn 360°; the circle tool's eight arcs work but need chaining), mural stairs (closer now `WallEdge.openings` exists — the same subtraction with a sloped head), a pitch dial (would join `ClaimingScrollWheel`).
- **Windows** — the same `Opening` with a sill *and* a head, so `Runs` wants a second raised-bottom case. Also deferred: arched heads (a section's bottom is flat), a door that opens, doorways at nodes (an opening lives strictly inside one edge — probably right).
- **Circle and rect don't get the skirt** — one shared `EdgeParams` per figure, so a stacked circle on a stepped host still shows the gaps the wall tool no longer does.
- **The room chain's live segment is a straight chord** — riding a *curved* wall top follows the centerline at the ends but doesn't bend along it. The wall tool does.
- **Elevated rooms get no floor slab** — a room designated on an upper storey has nothing to stand on.
- **A room's roof reads its own ring edges' tops**, so stacking a wall over a room's low wall does *not* make that room roofable. `WallRoom.RetryRoofless` fires on every `CommitEdge`, which isn't the same thing.
- **Stacked walls sit on a buried `skirt`, not a stepped bottom.** If a storey should visibly step with its sloped host instead of reading as a level parapet, the fix is a baked per-section bottom profile — which must survive `SubCurve` reparameterization and serialization.
- **Undesignating a room** — not built, not designed. A sub-face inside a designated room is refused as "already a room", which blocks splitting a hall in two.
- **Vertical delete** doesn't span a stack: deleting a wall is per-edge, and the storey above it survives (see structural support, above).
- `WallGraph.FaceAt` traces every edge both ways each hover frame in Designate mode, and `CutawayView` walks a few hundred renderers with one array allocation a frame — both free at this scale, both want revisiting together in the hundreds.
- Stepped landings shallower than ~40° can't reach the host within the 2m probe and stay free at the stepped bearing — may want an angle-scaled reach if it bites in play.
- Room stamp tool (user picked "both — rect first" 2026-07-31): copy a finished room's footprint and stamp clones elsewhere, rotation snapping to the wall bearing at the drop point.
- Rect length/width solves scan **free (1-valent) wall ends only** — right for curtain ends; revisit if towers should also solve against T-nodes or room corners.
- Lock-top planes are per-gesture: only a snapped *start* adopts a host's plane, and a room's ring includes any reused wall spans (which must be level too). Workflow answer for now — chain the curtain wall corner to corner so one plane travels the circuit.
- Known graph edges (minor, watch in playtest): 0.75–0.9m dead sliver near node ends where neither snap fires; Alt placement can bury an end inside a wall with no graph connection; 2-valent collinear nodes from deletes never re-merge; drag ghosts from a node overlap the host near the joint (honest — commit does too).
- Refusal gate is deliberately narrow (<3°, <0.15m, >0.6m run) and crossings split at ANY angle — sliver crossings under ~10° may look rough; tune after hand-testing.
- **Procedural detailing** (potential feature, 2026-07-30): the game dresses the player's shapes — crenellations on wall-walk tops, arrow loops / windows as *section variants* (no CSG — `WallEdge.Opening` is now the proof this works), towers suggested at corners, chimneys on kitchens. Details are DERIVED (a pure seeded function of edge + room + wall type, re-derived on rebuild like node posts), never stored; the player steers via wall type and room designation. Waits mostly on wall types.
- Marquee delete takes occluded walls inside the box (selects by screen projection); red shells are the confirmation surface. Watch whether that bites in dense builds.
- `BuildMenu` categories are still placeholder-ish — real wall types will force the issue.
- Dirt texture tiling repeats on the hill shoulder; world ends hard at the 800m terrain edge.
- Wall material lacks roughness/AO (HDRP mask map packing) — deferred for scope.
- Art pipeline step 1 (parallel lane, not started): buy one realistic modular medieval pack (on sale) → courtyard scene → HDRP lighting pass. User judges the image, Claude supplies settings.
- Design renown (#11) — top undesigned system; may tie the three paths together.
- Special rooms: which exist and what they do — #14 (feeds from/into the 1220 research pass).
- AI-prop bake-off (Tripo / Meshy / Rodin) judged in the courtyard; Character Creator 5 trial — check medieval wardrobe first.
- Walking liveliness beyond seasons+construction — #12. Hard-mode death (game over vs. heir) — #4 remainder. Liege as a mechanic — #5. What AI nobles simulate — #6. Vassal departure — #7 (user thinking). Alliances — ⏸ tabled.
- Deep-research pass: real lives of ~1220 landowners (doubles as art reference).
- Editor gotchas: the wall graph is **play-mode only** (`OnEnable` registration — edit-mode tests register nothing and leave orphans). `execute_code` runs as a *method body*: no `using` directives, and `Object` is ambiguous — fully qualify `UnityEngine.Object`. MCP bridge auto-start is ON; if "Unity session not available" after a relaunch, see memory note `unity-mcp-bridge-recovery`. "PlayerLoop called recursively" console spam = MCP screenshot capture in play mode, benign. Don't leave play mode running across recompiles — a domain reload orphans runtime walls.
