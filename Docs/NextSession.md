# Next Session

> Long arc: `Docs/Roadmap.md` (agreed 2026-07-30) — five phases from
> builder to siege. Phase 1 is current work; its two ⚠ design sessions
> (storeys/stairs, structural support) come before their code.

**Where we left off:** **Rooms shipped** (`WallRoom`) — designate an enclosed face of the wall graph and it gets a floor, plus a flush roof when the boundary tops are level; breach dissolves the whole room. Room tool (chained placement that auto-stops on enclosure), shared Circle/Rect helpers with wall reuse, and a `BuildLog` panel that says *why* something didn't work. Then two days of playtest-driven fixes: **storeys became real** (room slabs are build surfaces via `WallEdge.baseY`; snaps adopt a host's storey; deck-filtered snapping), the **delete tool was reworked** (paint through nodes, or marquee from empty ground), **corner geometry got honest** (angle arc measures real wall runs, Shift steps relative to them), and tower symmetry landed: a **rotated 3-click rect** with live dimension labels, a **Ctrl+click side-midpoint anchor** that solves length and width so curtain walls meet face centers, and **white alignment guides** whenever a placed point lines up with existing masonry. Last fix of the session: shape figures now capture their locked-top plane at the anchor click, which was silently breaking room-on-room designation by half a step.

## Top priorities

1. **Hand-playtest the day-2 batch** — build a real four-tower curtain castle end to end. Specifically unverified by hand: corner-tower Ctrl-snap feel, paint-through-nodes vs marquee delete, alignment guides (too noisy? right threshold at 0.15m lateral?), dimension labels, and the room-on-room flow that was just fixed. Feel knobs unchanged: `endpointSnapRadius` 0.75, `lengthStep` 0.5, `angleStep` 5 (Shift-only).
2. **Storeys & stairs ⚠ design session** — the deferred design call, now blocking. Owns: the planar graph is **height-blind** (an upper-storey wall crossing a ground wall in XZ still splits it); there are **no stairs** (you can build a keep you can't climb); what a drag leaving a deck edge should mean. Storeys already work structurally — this is about making them coherent.
3. **Phase 1 remainder** (`Docs/Roadmap.md`): wall types ladder (strength progression — feeds procedural detailing), openings as section variants (doors first, no CSG), gates/gatehouse, structural support ⚠ design call (refuse unsupported placement, or let it collapse?).

## Backlog / open questions

- Room stamp tool (user picked "both — rect first" 2026-07-31): copy a finished room's footprint and stamp clones elsewhere, rotation snapping to the wall bearing at the drop point. Lands with Phase 1 openings/storeys where repeated structures multiply.
- Rect length/width solves scan **free (1-valent) wall ends only** — right for curtain ends; revisit if towers should also solve against T-nodes or room corners.
- Lock-top planes are per-gesture: only a snapped *start* adopts a host's plane, and a room's ring includes any reused wall spans (which must be level too). Workflow answer for now — chain the curtain wall from corner to corner so one plane travels the circuit. Worth revisiting if it keeps biting.
- Known graph edges (minor, watch in playtest): 0.75–0.9m dead sliver near node ends where neither snap fires; Alt placement can bury an end inside a wall with no graph connection; 2-valent collinear nodes from deletes never re-merge; drag ghosts from a node overlap the host near the joint (honest — commit does too).
- Refusal gate is deliberately narrow (<3°, <0.15m, >0.6m run) and crossings split at ANY angle — sliver crossings under ~10° may look rough; tune after hand-testing.
- **Procedural detailing** (potential feature, 2026-07-30): the game dresses the player's shapes — crenellations on wall-walk tops, arrow loops / windows / doors as *section mesh variants* (no CSG), towers suggested at corners, chimneys on kitchens. Details are DERIVED (a pure seeded function of edge + room + wall type, re-derived on rebuild like node posts), never stored; the player steers via wall type and room designation. Waits mostly on wall types. Cheap proof: crenellation pass + arrow-loop sections on the current stone wall.
- Vertical delete doesn't span stacked courses — reconsider under graph delete semantics.
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
- Editor gotchas: MCP bridge auto-start is ON; if "Unity session not available" after a relaunch, see memory note `unity-mcp-bridge-recovery`. "PlayerLoop called recursively" console spam = MCP screenshot capture in play mode, benign. Don't leave play mode running across recompiles — a domain reload orphans runtime walls and loses anything built in that session.
