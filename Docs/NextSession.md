# Next Session

**Where we left off:** The free-building toolkit grew its **connection assists**: exact end snapping (endpoint / cap / flanks, hover and drag far-end), a corner-angle arc + degree readout, gold snapped ghosts that adopt the host's rotation **and exact height** (locked-top hosts match by top elevation; Ctrl+scroll overrides), an options panel with the first toggle (**Lock top height** — level parapet, bottom follows terrain), and **tangent-tied connectors** — snap both drag ends to wall ends and the wall auto-curves to leave one host and arrive at the other straight-on, tying in flush at both (Shift keeps it straight). Supporting work: collinear end-on fuses now trim flush at the host cap (no more stepped gaps), the long-range reach-back is gone (contact-only; snapping replaced it), wall height scrolls smoothly (0.25m/notch), and cut planes sink to curved hosts' real faces (no more daylight wedge where a flat cap meets a curve). All **programmatically verified, still not hand-played**.

## Top priorities

1. **Hand-playtest the toolkit** — now two sessions overdue and twice the surface: draw a real castle layout with corners via end snaps, tangent-tied connectors between offset walls, locked-top parapets on slopes, curves butting curves, the Offset tool, course stacking, deletes. Feel knobs all provisional: `endpointSnapRadius` (0.75), `snapColor` gold, `heightScrollRate` (0.25m/notch), angle-arc radius (1.5m), `lengthStep`/`angleStep`, guide range/colors.
2. **Design structural support (plan Step 4)** — the last pillar of "freedom limited only by physics," riding on the `Links` graph. Needs the design call first: **refuse unsupported placement, or let it collapse?** Vertical rests-on semantics wanted too.
3. **Junction polish by pain** (pick after playtest): **two-plane corner clips** at a host's end — now the only path to clean *deliberate* sharp corners (Shift-straight between snapped ends still shows the two-face artifact); **S-connector** for parallel offset walls (two chained quadratics — tangent tie falls back to straight there); bands aren't height-aware (a wall taller than its host carries the gap full height).
4. **Back to the graybox program**: floors, roofs, one premade room, first-person controller to walk what's built. Round-tower tool (center + radius) is a natural next spline feature — note `FaceRecession` sinks cap at 0.25m, so sub-2m-radius towers meeting thick walls will want the filler-mesh idea.
5. **Art pipeline step 1 (parallel lane, still not started):** buy one realistic modular medieval pack (on sale) → assemble a courtyard scene → HDRP lighting pass. Workflow: user judges the image, Claude supplies/applies settings.

## Backlog / open questions

- Matching height ≠ matching top on slopes (0.5m foundation steps at joints) — inherent; lock-top is the tool for level runs. Watch whether it bothers the user in playtest.
- `hasGaps` walls never rebuild → stale cuts possible; base-wall rebuild under stacked courses can shift cut boundaries slightly on slopes.
- Vertical delete doesn't span stacked spline courses.
- `BuildMenu` needs real categories/items once floors/roofs/rooms exist; options panel has one toggle so far.
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
