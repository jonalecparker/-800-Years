# Medieval Castle Lord (working title)

*Design notes — living document. Started 2026-07-21. Expect ~40% of this to change once a prototype exists.*

---

## Core Fantasy

**Live the life of a landed noble at the height of the castle age (~1200 AD Europe).**

You are born into nobility with a castle — but a shabby one. From that starting point, your life is yours to shape: warlord, builder, merchant prince, or anything in between.

This continues the theme of the first game (post-scarcity space wanderer): *experience what life would be like as a particular person in a particular time.* The era is the character. The exact year doesn't matter; the texture of the period does.

## Core Loop

*(Partially defined — the management side is clearer than the first-person side.)*

- **Strategic layer (top-down, city-planner view):** manage your castle and lands. Build and upgrade the castle, buy/sell castles, buy surrounding land, raise armies, run trade. General terrain-shaping of your lands lives here.
- **Personal layer (first-person):** embody your character directly. Walk your own castle, walk around Europe, fight alongside your armies in person.
- **First-person content model (decided):** most castle-walking is *for its own sake* — ambience is the point, per the core fantasy. Real content is concentrated in a small set of functional NPCs you talk to in person:
  - **Blacksmith** → acquire personal gear, equip your army
  - **Head builder** → upgrade the castle; castle-specific terrain shaping (broader land terrain-shaping stays in the top-down view)
  - Others TBD as needed — but deliberately **no real content out on the farms** or scattered across the countryside. The castle is the content hub.

## Design Pillars (draft)

1. **You live a life, you don't just play a mode.** Freedom to choose your path — conquest, construction, commerce — with no path being "the real game."
2. **Non-violence is a first-class path.** You can become wealthy and successful without ever killing anyone. The merchant route isn't a side activity; it's a full way to play.
3. **The period is the star.** If a real 1200s noble could plausibly do it and it's interesting, it belongs in the game's vision (even if not in v1).
4. **Two altitudes, one life.** The top-down lord and the first-person man are the same person. Decisions made at the map should be felt on the ground, and vice versa.
5. **A noble acts through people.** You don't grind skills — you acquire, pay, and manage the people who have them. Skyrim makes you the best blacksmith to get the best armor; here, a noble would never smith his own steel. The best gear comes from employing the best blacksmith. Progression is wealth, retainers, and reputation — not personal perk trees.

## The One Thing

**DECIDED: the first-person experience of *being* a landed noble.** If anything gets cut, the top-down management view goes first — it's "just a cool way to upgrade your castle, manage your vassals, etc.," not the point of the game.

Consequences of this decision:
- The first prototype should be: walk around your small, shabby castle in first person and see if it *feels* like being a noble. If that's compelling, the game works.
- The management layer is an interface, not the game. It can start as simple menus and become a pretty top-down view later.
- Art, sound, and world-texture budget matter more than systems depth — feel is the product.

## Scope Guardrails

*(Mostly warnings at this stage — the vision as stated is enormous.)*

- ⚠️ **"Anything a noble could do" is a vision statement, not a feature list.** Great for the pillar, fatal as a v1 scope. v1 needs a short list of verbs that are actually fun.
- ⚠️ **Dual-view games are two games.** Top-down management + first-person embodiment means two camera systems, two control schemes, two content pipelines. *Mitigated by the One Thing decision:* first-person is the game; the management view is subordinate and gets cut/simplified first if scope demands.
- **"Walk around all of Europe" — decided: keep the ambition, eyes open.** Acknowledged as a big lift; the bet is that AI-assisted development makes a very large world giving *the impression* of medieval Europe achievable. Note the framing: the goal is the impression of Europe, not a 1:1 continent — procedural/repeating countryside with handcrafted key locations is the likely shape.
- **Management depth is capped by design:** full-bodied, but deliberately short of a full city-builder. If a management feature starts demanding city-builder complexity, it's out of bounds.
- **No personal skill-grind systems.** Per pillar 5, no Skyrim-style crafting/perk trees for the noble himself. Resist the temptation to add them "because players expect it."
- Not a grand-strategy game about playing a dynasty/kingdom (that's Crusader Kings). You are one person, one life.
- Year is explicitly flexible — no obligation to historical events, dates, or real dynasties.

## Idea Dump

*(Everything mentioned, preserved verbatim in spirit.)*

- Born a noble; start with a castle that "isn't very nice"
- Buy and sell your castle
- Buy more land around your castle
- Raise armies
- Attack and capture other nobles' castles
- Stay home and build your own castle into something great
- Merchant path: get wealthy without killing anyone
- Top-down city-planner view for castle management
- First-person character control
- Fight with your armies in person
- Walk around all of Europe
- Walk around your own castle
- Manage your vassals (via the top-down/management interface)
- Ally with other lords to gain leverage against bigger targets
- Direct farming: choose crops and livestock across your lands
- Specialist vassals: blacksmiths, stable masters, steward who runs things in your absence
- Build houses (costs resources/money) to improve your working population
- Best gear comes from hiring the best blacksmiths, not crafting it yourself
- Additional gear-acquisition mechanics beyond money — wanted, undefined
- Alliances possibly one-time, single-goal arrangements that dissolve when the goal is met
- Stewards with competence tiers, priced to match; imperfect management at lower tiers
- Steward rebellion: can attempt to seize your estate if you're gone too long / renown too low
- Renown stat gating vassal loyalty
- Talk to blacksmith in person → personal gear + army equipment
- Talk to head builder in person → castle upgrades, castle-specific terrain shaping
- General terrain shaping of surrounding lands → top-down management view
- Content concentrated in castle NPCs; farms and countryside are ambient only
- Reference year c. 1220 (peak castle age); peasants demand protection, justice, custom, commons access, famine relief; failure state = workers leave for better lords
- Hard mode: you age and can die of old age — a time cap on your ambitions
- Completely solo game; other castles are AI nobles
- Wealthier/nicer estate attracts more vassals; vassals can leave
- Early threats are bandits only; nobles come after you once you're worth conquering
- Rivals may offer to *buy* your castle — take the money and start over somewhere else
- Gear beyond money: loot from captured castles, gifts/tribute from allies, tournament prizes
- Seasons + visible construction as the baseline of castle life
- Deep research pass planned: real lives of ~1220 landowners
- Castle building as a core system: fun in itself, defensive in sieges, and home to special rooms with in-game functions
- Robust building of stone walls, room designation, moats

## Historical Grounding (~1220 AD)

**Fidelity principle (decided): as realistic as possible, but never at the cost of fun.** Historical accuracy is a strong default, not a straitjacket — when realism and gameplay conflict, gameplay wins. A serious research pass into the actual lives of landowners of the period is planned to get the texture right.

Target texture: **c. 1180–1250, England/France** — stone castles at their peak, chivalry codified, manorial economy booming, before the Black Death and gunpowder. Use **c. 1220** as the reference year for flavor research.

What the people under you demanded in return, historically:

- **Peasants/tenants (the manorial bargain):** protection from raiders and rival lords; functioning justice (the manor court); **respect for custom** — dues were fixed by "custom of the manor" and lords could not arbitrarily raise them without resistance; access to the lord's mill/oven/commons (lords charged fees for these — revenue mechanic); relief of dues in famine years.
- **The historical failure state:** squeezed peasants *ran away* — to a better lord, or to a chartered town ("town air makes you free" after a year and a day). Lords competed for labor. → Game mechanic: unhappy workers leave for rival estates rather than (or before) revolting.
- **Knightly vassals (nobles holding land from you):** the fief itself, protection, justice in your court, and non-arbitrary treatment. Feudalism was an explicit two-way contract — a lord who broke his side gave the vassal legitimate grounds to renounce fealty and defect. (The steward-rebellion mechanic below is essentially this, historically attested.)

## Vassals & Economy

- **Most vassals are farmers.** You direct what crops are grown and what animals are raised — this is the foundation the mercantile system is built on later (you sell what your land produces).
- **Specialist vassals:** blacksmiths, stable masters, and a steward who runs your holdings while you're away. (The steward is load-bearing: it's what frees the player to wander Europe in first person without the castle falling apart.)
- **Stewards (decided):** stewards vary in competence and are priced accordingly. A steward can manage imperfectly (leaks, losses, slow decline) — and can outright **rebel and attempt to seize your estate if you're gone too long or your renown is too low**. Absence and reputation are real risks; hiring cheap has consequences.
- **Renown** (new stat, introduced by the steward mechanic): a measure of your standing that gates loyalty. How it's earned/lost is undefined — see Open Questions.
- **Housing loop:** building houses costs resources and money, but yields a better working population. Spend → better workers → more production → more money.
- **Vassal attraction (decided in principle):** a wealthier, better place to live attracts more (and better) vassals — quality of life is a recruiting mechanic, the flip side of the housing loop. Vassals **can leave** if conditions are poor (historically grounded: peasants ran away to better lords). Exact mechanics of leaving/underperforming still being thought through.
- **Gear progression = wealth progression.** Better gear comes from hiring better blacksmiths, and blacksmiths must be paid. Money is the universal currency of power — it buys armies, land, *and* personal equipment, which keeps the merchant path viable even for eventual conquest.
- **Non-money gear mechanics (decided):** all three candidates are in the vision — **loot from captured castles** (warlord path), **gifts/tribute from allies** (diplomat path), and **tournament prizes**. Nicely, each one feeds a different playstyle.
- **Depth target:** castle and land management should be *full-bodied but not a full city-builder*. Meaningful choices, not Banished-level simulation. (Good scope guardrail — recorded below too.)

## Progression & Pressure (decided in part)

The engine of the game is **ambition, not survival**. You start with the smallest castle in the region. Everyone wants a bigger one. The bigger castles around you are unattainable at first:

- Walk up and attack → their armies destroy you.
- Try to buy → you can't afford it.

So the ladder is: **raise money, raise armies, and/or ally with other lords** until you have the leverage to take (or buy) something bigger. The three paths (merchant, warlord, diplomat) are three routes up the same ladder, and they can mix.

**Push (decided in design, deferred in implementation):** the world does come at you, and the threat scales with your stature:

- **Early game:** you're not worth a noble's attention. Your only real threats are **bandits**.
- **As you grow:** other lords may try to **conquer you** — or make an **offer to buy your castle**. Accepting a buyout is a legitimate move: take the money and start over somewhere else. (This turns even losing ground into a choice, not just a defeat.)
- ⏱ **Implementation priority: one of the last things built.** The pull ladder comes first; defense and rival aggression layer on late.

## Castle Building (major system — direction set 2026-07-21)

**Castle building is a big part of the game.** Three jobs in one system:
1. **Creative:** building a great castle is fun in itself (the builder path made concrete).
2. **Defensive:** walls, towers, and moats matter when armies attack — your layout is your siege answer.
3. **Functional:** special rooms that do special things in the game (kinds TBD).

Needs a robust way to build stone walls, designate rooms, and dig moats.

**Fidelity (DECIDED): grid-based modular placement.** A few handfuls of object types — **walls, roofs, floors, and premade rooms** — accomplishes the intended look. Freeform drawing and preset-only upgrades both rejected.

Two happy consequences of this exact shape:
- **Premade rooms delete room detection** — the hardest tech in building systems. A room *is* the placed module, with its function attached; no inferring "this enclosure is a kitchen" from arbitrary walls.
- **Explicit roof pieces delete auto-roof generation** — the other classic time sink.

**Asset spec preview** (for the eventual Blender kit): tiling wall/floor/roof pieces on a fixed grid + room modules as larger set-pieces. Exact grid size etc. still comes from the graybox prototype.

**Tensions & synergies:**
- ⚠️ Tests the spirit of the "short of a city-builder" guardrail — biggest single system in the game; decide fidelity deliberately.
- Must reconcile with the head-builder decision. Likely shape: *plan* in top-down build mode, head builder is the in-person interface, construction then visibly proceeds in-world (which also feeds the seasons/construction walking baseline).
- Feeds three threads at once: #12 (you walk your *own creation* as it changes), the defense push (walls/moats vs. sieges), and first-person functional destinations (special rooms).

**Art consequence:** the castle kit becomes a *system-spec'd custom kit*, not pack assets (see Art Direction & Pipeline).

## Time, Lifespan & Difficulty

- **Lifespan is a hard-mode feature (decided).** In hard mode you age and can die of old age — a real cap on how much time you have to achieve your goals. Realistic, and deliberately punishing.
- **Default mode** stays closer to the first game's sandbox: no old-age clock pressuring you.
- Open sub-question: what does dying of old age in hard mode actually mean — game over, or continue as an heir? (Heir succession drifts toward Crusader Kings dynasty territory, which is explicitly out of scope — but "game over, roll credits on the life you lived" fits the "one person, one life" frame.)

## Open Questions

1. ~~Which layer is the game?~~ **Resolved: first-person is the game; management view is subordinate.** (See "The One Thing.")
2. ~~What's the moment-to-moment loop?~~ **Mostly resolved:** castle-walking is primarily ambient/for-fun, with real functions concentrated in a few key NPCs (blacksmith, head builder, more TBD); no content scattered on farms. Still open: what makes the ambient walking *stay* fun after hour ten — life, motion, and change in the castle (see question 11).
3. ~~What pushes back?~~ **Resolved:** ambition-driven progression ladder, plus threat that scales with stature — bandits early, rival lords (conquest or buyout offers) later. Defense implemented late. (See "Progression & Pressure.")
4. ~~Is there time pressure / a life span?~~ **Resolved:** lifespan/aging/death by old age is a **hard-mode feature**; default mode stays sandbox. (See "Time, Lifespan & Difficulty.") Still open: what death means in hard mode (game over vs. heir), and whether default mode has any win state at all.
5. ~~How historical is it?~~ **Resolved as a principle:** as realistic as possible without sacrificing fun; deep research pass into real landowners' lives planned. (See "Historical Grounding.") Still open: is your liege actually in the game as a mechanic (fealty, taxes, levies)?
6. ~~Multiplayer or solo?~~ **Resolved: completely solo.** Other castles are AI nobles. Still open: how much do AI lords actually simulate (do they play your game — buying land, raising armies — or just appear to)?
7. ~~Who are your vassals?~~ **Resolved:** mostly farmers, plus specialists (blacksmiths, stable masters, steward). **Also resolved in principle:** a wealthier, nicer estate attracts vassals, and unhappy vassals can leave. Still open: the exact demands/leave/underperform mechanics — user is still thinking on this.
8. **How do alliances work?** ⏸ **Tabled by choice** — full alliance systems get very complex and easy to break. Current lean: one-time alliances formed for one specific goal, dissolved when it's met. Revisit after core loop exists.
9. ~~What are the non-money gear mechanics?~~ **Resolved:** all three — loot from captured castles, gifts/tribute from allies, tournament prizes. Each feeds a different path (warlord/diplomat/renown?).
10. ~~How does the steward actually work?~~ **Resolved:** competence tiers priced accordingly; can mismanage; can rebel and attempt to seize the estate if you're absent too long or renown is too low. (See Vassals & Economy.)
11. **What is renown and how do you earn/lose it?** Introduced by the steward-rebellion mechanic. Battles won? Castle grandeur? Wealth displayed? Tournaments? Generosity in famine? This could become the game's reputation spine — worth designing deliberately. (Note: tournaments now exist for gear prizes via #9 — a natural renown source too.)
12. **What keeps ambient castle-walking fun over time?** **Baseline decided: seasons and visible construction.** Still open: what else — feasts, markets, festivals, population growth you can see? The reward for good management is what you *see* on your walk. (Strengthened by the castle-building decision: you're walking your *own creation*.)
13. ~~Castle building fidelity?~~ **Resolved: grid-based modular placement** — walls, roofs, floors, premade rooms. (See "Castle Building.")
14. **What are the special rooms and what do they do?** Functional rooms are decided in principle; the list (armory? chapel? treasury? great hall? dungeon?) and their game effects are undefined. Historical research pass should feed this.

## Build Phases (draft)

**Status of the design (decided 2026-07-21): the heart of the game is written.** Remaining open questions (renown tuning, vassal-departure mechanics, castle liveliness beyond the baseline) are expected to fall into place *during building*, not before. Further doc-only design is diminishing returns.

**Phase 1 — "Day One": get your estate operational.**
The opening scenario of a new game, built as the first vertical slice:

- You have just **inherited** a small castle, surrounding farms, a few vassals, and a limited pool of inherited resources.
- The goal of the phase: spend those limited resources to get the estate running more productively — first crop/livestock direction, first housing/repair decisions, first hires.
- Everything built for this phase doubles as the game's opening hour/tutorial.
- Deliberately excludes all deferred systems: armies, rivals, defense, alliances, buyouts.

⚠️ **Framing guardrail (per The One Thing):** Phase 1 must be first-person-led, not menu-led. Day one starts with *walking your shabby castle* and making those first decisions through people (steward, blacksmith, head builder) — the phase tests the game's riskiest bet ("does walking your castle feel like being a noble?") at the same time as the economy basics. If Phase 1 turns into a spreadsheet with a camera bolted on, it's testing the subordinate layer first.

*(The no-code gate was lifted 2026-07-23 — development may begin. First code target: the castle-building graybox prototype, not Phase 1 itself; Phase 1 remains the first full vertical slice.)*

## Art Direction & Pipeline

**Context:** solo dev, little/no art-creation skill — art will be heavily AI-assisted and pack-based. Experience from game one: free assets + purchased Synty packs + experiments with **Tripo** (AI 3D generation — mixed results so far, but judged to still have real potential).

**Art direction (DECIDED): realistic.** "I'd only really be happy with this if we went with a realistic style." This is the vision-aligned call — "the period is the star" and grounded realism (Kingdom Come: Deliverance as the reference point) is what makes living-in-1220 land. Stylized low-poly was considered as the pragmatic option and rejected: it keeps the player at arm's length from the exact thing the game is selling.

Consequences, eyes open:

- **Characters are the #1 art risk.** Realistic environments are achievable solo (scan-based libraries + good lighting do the heavy lifting); realistic *humans* — faces, skin, animation — are the uncanny-valley trap, and the castle must be full of vassals. The pipeline test lives or dies here.
- **Consistency now comes from lighting, post-processing, and materials**, not a shared shape language. Realism exposes seams between asset sources that low-poly would hide. The unifying pass is systematic work, not artistic skill — doable, but mandatory for everything that ships.
- **Performance/scope pressure rises.** Realistic fidelity strains "the impression of Europe" harder; the procedural/repeating-countryside framing becomes even more load-bearing.
- **Tripo gets *more* viable, not less.** AI 3D generators trend realistic by default — the style-mismatch problem from the Synty era partially dissolves. Existing Synty packs are effectively off the table for this game.

**Strategy (agreed): prove the pipeline, not the inventory.** Do *not* produce the game's full art up front — ~40% of the design will change, and pre-gameplay art misses requirements (scale, modularity, performance, what's actually on camera). But because the One Thing is *feel*, this game needs real art earlier than most projects:

1. **Collect only the Phase 1 art set:** one shabby castle (modular, for the upgrade system), surrounding farms, a handful of vassal NPCs — realistic packs, scan libraries, and AI generation.
2. **Pipeline test = one scene:** a realistic castle courtyard with **one convincing NPC in it**, assembled from scans/packs/Tripo and unified by a lighting-and-post pass. If that scene feels like 1220, the game works. Success = "I know how to make more," which is the actual risk to retire.

**Pipeline lanes (researched 2026-07-21):**

| Lane | Tooling | Notes |
|---|---|---|
| Environments | Unity Asset Store realistic packs (Meshingun Medieval Village Megapack; Hivemind Medieval Kingdom / Ultimate Bundle; Art Equilibrium Medieval Castle) + Quixel Megascans via Fab | Buy the backbone; modular kits serve the castle-upgrade system. Megascans no longer free — Fab pricing/subscription. |
| Bespoke props | AI 3D gen: Tripo (fast, Unity plugin, v3.0 Ultra), Rodin Gen-2 (highest fidelity), Meshy (most polished platform) | No consensus winner — run a same-props bake-off and pick. Prior Tripo frustration predates v3.0. |
| Characters (#1 risk) | Reallusion pipeline: Character Creator 5 → iClone 8 → Unity auto-setup; MakeHuman for background NPCs | The established solo-dev route to realistic rigged humans; iClone AI video mocap for animations. ~a few hundred dollars. |
| Unification | Unity **HDRP** + deliberate lighting/fog/post pass | Under realism, lighting/post IS the art style. **Workflow (decided 2026-07-22):** user does NOT front-load HDRP settings knowledge — user judges the image ("the eye"/taste, the irreplaceable part), Claude supplies and applies concrete settings (via Unity MCP where possible), explaining only what's touched. Vocabulary (~10 concepts: exposure, tonemapping, baked vs. realtime, volumes…) absorbed as it comes up. |

**The castle kit (REVISED after the castle-building decision):** with player-facing castle building (see "Castle Building"), the castle itself moves *out* of the buy-a-pack lane. A building system needs assets authored to its spec — exact grid dimensions, socket conventions, pieces that corner/cap cleanly, watertight interiors under arbitrary room layouts, construction-in-progress states, siege damage states. Packs are sculptures, not LEGO; AI 3D gen is at its weakest here too (one-off meshes, no shared dimensions/pivots/tiling). Plan:
1. **System before art:** the building system defines the asset spec (grid size, wall heights, snapping) — prototype it in graybox first. Sequence decided 2026-07-23: **graybox core** (placement, snapping, walls/floors/roofs, one premade room, walk it in first person) → **thin real-asset slice** (one wall/corner/floor/room module through the system, to let asset realities push back on the spec) → **build out system and kit together**. Not 100% system then 100% art.
2. **Then a scoped Blender effort, architecture only:** castle geometry is primitive (boxes, cylinders, arches); Megascans materials + lighting carry the realism. Weeks-not-years tier. Promoted from fallback to the likely plan.
3. **Packs still cover everything the player doesn't build:** village, countryside, interior clutter/furnishings — and the courtyard test still proves look/lighting/characters/props (just not castle construction).

**Starter sequence (pre-code, allowed now):**
1. Throwaway Unity HDRP project purely for art tests (not the game project).
2. One castle pack (buy on sale) → assemble a small courtyard → Megascans surfaces → HDRP lighting pass via tutorial.
3. AI-prop bake-off: same 3–4 props (anvil, trough, hay cart, barrel) through Tripo/Meshy/Rodin; judge them *in the courtyard*, next to pack assets.
4. Character Creator trial: one vassal in period clothing into the scene.
5. Verdict: does the courtyard feel like 1220? (= the pipeline test defined above.)

## Differences From Game One (worth tracking)

- Game one: post-scarcity (no economy pressure). This game is *about* economy, land, and power — nearly the opposite. Expect the systems design to be much heavier.
- Both share: "experience a life in a time," sandbox freedom, no forced path.
