# Next Session

**Where we left off:** The no-code gate was lifted 2026-07-23 — building begins. Design heart is declared complete; art direction is **realistic**; castle building is a major system with fidelity decided (**grid-based modular placement**: walls, roofs, floors, premade rooms). The living design doc is `medieval-castle-lord.md` at the repo root; workflow: you narrate, Claude organizes and flags tensions.

## Top priorities

1. **Create the Unity project and stand up the castle-building graybox.** Unity 6 + HDRP. Core: grid placement, snapping, walls/floors/roofs, one premade room, first-person controller to walk what you place. Graybox core → thin real-asset slice → build out system + kit together (sequence in doc).
2. **Art pipeline step 1 (parallel lane):** buy one realistic modular medieval pack (on sale) → assemble a courtyard scene → HDRP lighting pass. Workflow: you judge the image, Claude supplies/applies settings (see memory + doc).
3. **Design renown (#11)** — still the top undesigned system; may be the spine tying the three paths together.

## Backlog / open questions (see doc for full list)

- Special rooms: which exist and what they do — #14 (feeds from, and into, the 1220 research pass)
- AI-prop bake-off: same 3–4 props through Tripo / Meshy / Rodin, judged in the courtyard
- Character Creator 5 trial — check medieval wardrobe availability before buying
- Walking liveliness beyond seasons+construction baseline — #12
- Hard-mode death: game over vs. heir — #4 remainder
- Liege as a mechanic (fealty, taxes, levies)? — #5 remainder
- What AI nobles actually simulate — #6 remainder
- Vassal departure/underperformance mechanics — #7 remainder (user still thinking)
- Alliances — ⏸ tabled by choice
- Deep-research pass: real lives of ~1220 landowners (doubles as art reference now)

## Standing cautions

- ⚠️ Game one still needs to ship — the gate is gone, but the risk it guarded (game two's shine eating game one's momentum) is not. Keep the split deliberate.
- Dual-session setup is verified (two Editors + MCP multi-instance routing) — each session must `set_active_instance()` before Unity work; keep the MCP package current in both projects.
