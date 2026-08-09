# Next Session

> Long arc: `Docs/Roadmap.md` — five phases from builder to siege, plus
> the "Near arc" (agreed 2026-08-05): save → real terrain → permanent
> castle → economy → procedural town. **Steps 1–3 are done** — the user's
> call, 08-09: the builder isn't fully proven, but "I can see how it
> will work, and I think that's enough."

**Where we left off:** The 08-09 session closed out the builder push:
walls snap to the foundation rim (outer face flush with the slab edge),
stairwells came back as **stamped wells** with stair owners that heal on
delete (schema v4), slabs wear the wall's stone, **right-click is undo
everywhere** (chain corners, room legs, wall chain legs, and the Delete
tool via a whole-world snapshot), walls chain like the room tool, the
0.5m height-step mystery was banker's rounding (round-half-up now), and
stairs got a rework slice: 1.8m wide, Ctrl+scroll climb dial, R cycles
to a perpendicular run, and the foot adopts the landing it stands on.
Much of that slice is machine-verified but **not yet hand-tested**.

## Top priority: THE ECONOMY DESIGN SESSION (near-arc step 4)

A design conversation, not code. Design doc open question #16: **how
does a lord make money from the land and people around his castle?**
Demesne produce he sells vs. rents and fees on everyone else's —
historically both, and the split IS the structure (demesne farming,
rents in cash and kind, labor services, mill/oven monopoly fees, court
fines, market tolls).

1. **Start with the c. 1220 landowner deep-research pass** (England/
   France, reference year 1220) — it feeds this design AND the phase-3
   detail rulebook. The design doc's decided material it must plug
   into: stewards (competence tiers, can rebel), the housing loop
   (spend → better workers → more production), workers who *leave*
   rather than revolt, phase 1's inheritance framing (walk your shabby
   castle; first-person-led, not menu-led).
2. **Then the design session proper** — what the player actually
   manages, what the income sources are as systems, what phase 2's
   "make it cost" prices against.
3. **Wall types** (palisade → stone → fortified) are the most
   load-bearing build prerequisite: cost, detailing, and siege HP all
   key off them. Likely the first code the economy design demands.

## Second lane: hand-test the 08-09 batch while building

Untested by hand: slab stone UVs, right-click undo (all four forms),
wall chaining feel, 1.8m stair width, the climb dial + perpendicular
stair, stair foot adoption (point at the tall wall FROM the landing —
the ghost shows the foot; `GhostTopY` tells you what it read).

## Backlog / open questions

- **Any new stored fact must join `CastleSave`** — unserialized fields
  vanish on load. Schema is v4 (wells with stair owners); v2/v3 load.
- **Structural support deliberately absent** (user's call): deleting a
  lower wall leaves masonry hanging. Standing rule: **no new system may
  assume construction is instantaneous in the world.**
- Folded/switchback stair (landings built in) — agreed direction for
  tall walls, not yet speced. Perpendicular stair lands in open air
  unless a floor meets it — by design, worth a feel check.
- Wall-chain undo of a leg later split by another leg is partial;
  delete undo is one step deep — both accepted.
- **Watch-item**: a terrain wall drags through a slab-borne building
  unimpeded (different storeys never meet) — decide if that needs law.
- Graduate the briefs: `Docs/Stairs.md`, `Docs/Doors.md`,
  `Docs/BuildingRebuild.md` fold into CLAUDE.md once playtested. Roofs
  and support rules wait on that verdict.
- Roof decks/wall tops have no parapet; crenellations are on the
  procedural-detailing list (waits on wall types).
- Curved rooms floor chorded — trace-STAMP fill (Enclosure → stored
  polygon) is the agreed second tool, deferred.
- Saves: no rename; slots live in `Saves/` (committed); move to
  persistentDataPath when the game has a front end.
- A doorway with 0.6–2.2m of wall above its sill reads as a gateway;
  circle/rect don't get the skirt; the room chain's live segment is a
  straight chord on curved hosts; stepped landings under ~40° can't
  reach; known graph edges (dead sliver near node ends, Alt-buried
  ends, collinear nodes never re-merge, sliver crossings look rough).
- Level-area ground job on the wishlist — foundations cover most of it.
- `BuildMenu` categories placeholder-ish; wall material lacks
  roughness/AO; dirt tiling repeats; world ends hard at terrain edge.
- Walk speeds/eye height untuned; node posts have no collider.
- Art pipeline step 1 (parallel lane): one realistic modular pack →
  courtyard → HDRP lighting pass. User judges the image.
- Design queue after income: renown (#11), special rooms (#14),
  AI-prop bake-off, walking liveliness (#12), hard-mode death (#4),
  liege (#5), AI nobles (#6), vassal departure (#7).
- Editor gotchas: wall graph is **play-mode only**; `execute_code` is a
  method body; same-call physics tests have unsynced colliders; new
  .cs files need `refresh_unity scope=all`; don't leave play mode
  running across recompiles; public fields are scene-serialized (a
  script-default change alone does nothing). Mouse-look walls up over
  RDP — accepted, don't try again.
