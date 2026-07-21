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

## Historical Grounding (~1220 AD)

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
- **Gear progression = wealth progression.** Better gear comes from hiring better blacksmiths, and blacksmiths must be paid. Money is the universal currency of power — it buys armies, land, *and* personal equipment, which keeps the merchant path viable even for eventual conquest. Additional non-money gear mechanics are wanted but undefined (loot from captured castles? gifts from allies? tournament prizes?).
- **Depth target:** castle and land management should be *full-bodied but not a full city-builder*. Meaningful choices, not Banished-level simulation. (Good scope guardrail — recorded below too.)

## Progression & Pressure (decided in part)

The engine of the game is **ambition, not survival**. You start with the smallest castle in the region. Everyone wants a bigger one. The bigger castles around you are unattainable at first:

- Walk up and attack → their armies destroy you.
- Try to buy → you can't afford it.

So the ladder is: **raise money, raise armies, and/or ally with other lords** until you have the leverage to take (or buy) something bigger. The three paths (merchant, warlord, diplomat) are three routes up the same ladder, and they can mix.

Open sub-question: this is all *pull* (your ambition) and no *push* (the world coming at you). Do rival lords ever attack **you** unprovoked? Does your liege demand taxes or levies? A world that only waits to be conquered can feel static; a little unprovoked pressure — even rare — makes the quiet moments feel earned. Flagged, not decided.

## Open Questions

1. ~~Which layer is the game?~~ **Resolved: first-person is the game; management view is subordinate.** (See "The One Thing.")
2. ~~What's the moment-to-moment loop?~~ **Mostly resolved:** castle-walking is primarily ambient/for-fun, with real functions concentrated in a few key NPCs (blacksmith, head builder, more TBD); no content scattered on farms. Still open: what makes the ambient walking *stay* fun after hour ten — life, motion, and change in the castle (see question 11).
3. ~~What pushes back?~~ **Resolved: ambition-driven progression ladder** (see "Progression & Pressure") — with the open sub-question of whether the world ever attacks you first.
4. **Is there time pressure / a life span?** Do you age? Can you die and lose? Is there a win state, or is it a sandbox like the first game ("do whatever you would wanna do")?
5. **How historical is it?** Plausible-medieval flavor, or actual mechanics for feudal obligations (you presumably owe fealty to someone — is your liege in the game)?
6. **Multiplayer or solo?** Other castles are owned by someone — AI nobles presumably? Do they play the same game you do (buying land, raising armies)?
7. ~~Who are your vassals?~~ **Resolved:** mostly farmers, plus specialists (blacksmiths, stable masters, steward). See "Vassals & Economy." Still open: what vassals *demand* from you (just wages? protection? fair treatment?), and whether unhappy vassals can leave or underperform.
8. **How do alliances work?** ⏸ **Tabled by choice** — full alliance systems get very complex and easy to break. Current lean: one-time alliances formed for one specific goal, dissolved when it's met. Revisit after core loop exists.
9. **What are the non-money gear mechanics?** Money → blacksmith → gear is the main path; what else? (Candidates to explore: loot from captured castles, gifts/tribute, tournament prizes.)
10. ~~How does the steward actually work?~~ **Resolved:** competence tiers priced accordingly; can mismanage; can rebel and attempt to seize the estate if you're absent too long or renown is too low. (See Vassals & Economy.)
11. **What is renown and how do you earn/lose it?** Introduced by the steward-rebellion mechanic. Battles won? Castle grandeur? Wealth displayed? Tournaments? Generosity in famine? This could become the game's reputation spine — worth designing deliberately.
12. **What keeps ambient castle-walking fun over time?** Since walking is mostly for its own sake, the castle needs visible life and change: seasons, construction actually underway, feasts, markets, your upgrades physically appearing. The reward for good management is what you *see* on your walk.

## Differences From Game One (worth tracking)

- Game one: post-scarcity (no economy pressure). This game is *about* economy, land, and power — nearly the opposite. Expect the systems design to be much heavier.
- Both share: "experience a life in a time," sandbox freedom, no forced path.
