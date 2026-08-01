# Next Session

> Long arc: `Docs/Roadmap.md` (agreed 2026-07-30) — five phases from
> builder to siege. Phase 1 is current work; its two ⚠ design sessions
> (storeys/stairs, structural support) come before their code.

**Where we left off:** A **playtest-driven fix session** on top of the rooms/storeys work. The user built a curtain wall with four corner towers (came out well) and started a centre castle, which surfaced four things, all shipped: the roof now rests on the **dominant crown** and lets *borrowed* taller walls rise past it (a lean-to against the keep roofs correctly; a wall of the room's own ring left off-level still refuses, by name); **Shift means angle stepping and nothing else** in both the wall tool and the room chain, landing on the host along the stepped ray, with the tangent-tied connector moved to **`C`**; the room tool gained a **Designate** job in the menu (Build is the default) that hands an *existing* enclosure a floor and roof, gated on **provenance** — `WallEdge.roomBuilt`, majority by length — so a bailey never qualifies however room-shaped it looks; and a **modifier panel in the top-right** listing only the keys that do something in the current tool phase (17 contexts, generated from the input code itself).

## Top priorities

1. **Finish the centre-castle playtest** — it was interrupted four times by these fixes and never completed. Specifically unhand-tested: the Shift/`C` split in real drags, the Build/Designate menu buttons, and whether the hint panel reads well or just adds noise. Everything else this session was verified automatically only (see the session log's verification section).
2. **Storeys & stairs ⚠ design session** — still the blocking design call. Owns: the planar graph is **height-blind** (an upper-storey wall crossing a ground wall in XZ still splits it); there are **no stairs** (you can build a keep you can't climb); what a drag leaving a deck edge should mean.
3. **Phase 1 remainder** (`Docs/Roadmap.md`): wall types ladder (strength progression — feeds procedural detailing), openings as section variants (doors first, no CSG), gates/gatehouse, structural support ⚠ design call (refuse unsupported placement, or let it collapse?).

## Backlog / open questions

- **Undesignating a room** — not built, not designed. A sub-face inside a designated room is refused as "already a room", which blocks splitting a hall in two. Needed before room subdivision means anything.
- `WallGraph.FaceAt` traces every edge both ways each hover frame in Designate mode — free at ~40 edges, wants caching in the hundreds.
- Stepped landings shallower than ~40° can't reach the host within the 2m probe and stay free at the stepped bearing — may want an angle-scaled reach if it bites in play.
- Room stamp tool (user picked "both — rect first" 2026-07-31): copy a finished room's footprint and stamp clones elsewhere, rotation snapping to the wall bearing at the drop point. Lands with Phase 1 openings/storeys.
- Rect length/width solves scan **free (1-valent) wall ends only** — right for curtain ends; revisit if towers should also solve against T-nodes or room corners.
- Lock-top planes are per-gesture: only a snapped *start* adopts a host's plane, and a room's ring includes any reused wall spans (which must be level too). Workflow answer for now — chain the curtain wall corner to corner so one plane travels the circuit.
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
- Editor gotchas: the wall graph is **play-mode only** (`OnEnable` registration — edit-mode tests register nothing and leave orphans). MCP bridge auto-start is ON; if "Unity session not available" after a relaunch, see memory note `unity-mcp-bridge-recovery`. "PlayerLoop called recursively" console spam = MCP screenshot capture in play mode, benign. Don't leave play mode running across recompiles — a domain reload orphans runtime walls and loses anything built in that session.
