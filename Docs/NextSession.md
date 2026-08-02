# Next Session

> Long arc: `Docs/Roadmap.md` (agreed 2026-07-30) — five phases from
> builder to siege. Phase 1 is current work; its two ⚠ design sessions
> (storeys/stairs, structural support) come before their code.

**Where we left off:** The **storeys half** of the ⚠ storeys/stairs design call, shipped. A wall standing on another wall is no longer a *course* (a second kind of object riding a base edge) — it's an ordinary edge with its own nodes and a `baseY`, which is what lets an upper storey be a room. All the course machinery is deleted. The **planar graph is now per-base**, closing last session's "height-blind" item. Building on a wall top **rides** it: the run is a span of the wall below, inheriting its exact curve and chaining through its nodes, with Alt as the only way off. Four rounds of playtest findings were fixed on top of that — stub overhang, free direction at an upper wall's end, daylight over stepped joints (`skirt`), circles stopping at every node, upper walls not meeting in the middle, a 0.5m height flip, and finally a hover that jumped a storey and drew a red ghost. **Stairs were not touched.**

## Top priorities

1. **Play the storeys work.** Nothing from this session is hand-confirmed — rounds 1–3 were *found* by playing, but each fix went out and the next finding came back about something else, and round 4's fix (the storey-filter change) has never been played at all. Watch specifically: whether the ride's reach feels right now that you must point at a wall rather than near it; the ride chaining across a circle or room ring in one drag; and whether skirted stacks on sloped ground read as intended.
2. **Stairs ⚠ design session** — the remaining half. You can now build a three-storey keep with no way to climb it. Owns: what a stair *is* in the graph (edge? room? third thing?), how it attaches to two storeys at once, and what a drag leaving a deck edge should mean.
3. **Finish the centre-castle playtest** — now interrupted across two sessions.
4. **Phase 1 remainder** (`Docs/Roadmap.md`): wall types ladder (strength progression — feeds procedural detailing), openings as section variants (doors first, no CSG), gates/gatehouse, structural support ⚠ design call.

## Backlog / open questions

- **Structural support is deliberately absent** (user's call): nothing stores what rests on what, so deleting a lower wall leaves masonry hanging in the air. `baseY` alone is enough to reconstruct support when the design call lands — don't add a spatial "what's under me" query in the meantime.
- **Circle and rect don't get the skirt** — one shared `EdgeParams` per figure, so a stacked circle on a stepped host still shows the gaps the wall tool no longer does.
- **The room chain's live segment is a straight chord** — riding a *curved* wall top follows the centerline at the ends but doesn't bend along it. The wall tool does.
- **Elevated rooms get no floor slab** — a room designated on an upper storey has nothing to stand on.
- **A room's roof reads its own ring edges' tops**, so stacking a wall over a room's low wall does *not* make that room roofable. `WallRoom.RetryRoofless` fires on every `CommitEdge`, which isn't the same thing.
- **Stacked walls sit on a buried `skirt`, not a stepped bottom.** If a storey should visibly step with its sloped host instead of reading as a level parapet, the fix is a baked per-section bottom profile — which must survive `SubCurve` reparameterization and serialization.
- **Undesignating a room** — not built, not designed. A sub-face inside a designated room is refused as "already a room", which blocks splitting a hall in two.
- **Vertical delete** doesn't span a stack: deleting a wall is per-edge, and the storey above it survives (see structural support, above).
- `WallGraph.FaceAt` traces every edge both ways each hover frame in Designate mode — free at ~40 edges, wants caching in the hundreds.
- Stepped landings shallower than ~40° can't reach the host within the 2m probe and stay free at the stepped bearing — may want an angle-scaled reach if it bites in play.
- Room stamp tool (user picked "both — rect first" 2026-07-31): copy a finished room's footprint and stamp clones elsewhere, rotation snapping to the wall bearing at the drop point. Lands with Phase 1 openings/storeys.
- Rect length/width solves scan **free (1-valent) wall ends only** — right for curtain ends; revisit if towers should also solve against T-nodes or room corners.
- Lock-top planes are per-gesture: only a snapped *start* adopts a host's plane, and a room's ring includes any reused wall spans (which must be level too). Workflow answer for now — chain the curtain wall corner to corner so one plane travels the circuit.
- Known graph edges (minor, watch in playtest): 0.75–0.9m dead sliver near node ends where neither snap fires; Alt placement can bury an end inside a wall with no graph connection; 2-valent collinear nodes from deletes never re-merge; drag ghosts from a node overlap the host near the joint (honest — commit does too).
- Refusal gate is deliberately narrow (<3°, <0.15m, >0.6m run) and crossings split at ANY angle — sliver crossings under ~10° may look rough; tune after hand-testing.
- **Procedural detailing** (potential feature, 2026-07-30): the game dresses the player's shapes — crenellations on wall-walk tops, arrow loops / windows / doors as *section mesh variants* (no CSG), towers suggested at corners, chimneys on kitchens. Details are DERIVED (a pure seeded function of edge + room + wall type, re-derived on rebuild like node posts), never stored; the player steers via wall type and room designation. Waits mostly on wall types. Cheap proof: crenellation pass + arrow-loop sections on the current stone wall.
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
