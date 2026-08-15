using System.Collections.Generic;
using UnityEngine;

// The survey: carves the countryside into irregular parcels ONCE, from
// the terrain as it stands at generation time, and never again — the
// parcels are stored facts from then on (CastleSave v7 carries them;
// a loaded save restores its plots and this never runs).
//
// Parcels GROW, they are not stamped (2026-08-13, the user's call
// after the castle-grounds screenshot): every seed floods outward over
// a cost field AT THE SAME TIME — flat open ground is cheap, steep
// ground is dear, a road or river is dear to CROSS — and each raster
// sample belongs to whichever seed reached it cheapest (multi-source
// Dijkstra). Coverage is total and exclusive BY CONSTRUCTION, so the
// two failure modes of the old jittered-quad lattice (a cell
// overlapping the castle grounds, a stood-aside cell leaving a hole
// beside it) are unrepresentable. Boundaries lie ALONG roads and
// rivers because both competitors stop at the expensive stripe, and
// parcels shape themselves to the terrain because a flood spreads
// across its own flat ground and stalls at the scarp. The castle
// grounds polygon AND each castle's village (grown FIRST by its own
// gate-seeded flood — the village-flood block below) are PRE-CLAIMED,
// FROZEN masks: nothing may enter them, they never grow past their
// own outlines, and their neighbors butt against them exactly. The lattice survives only as the SEED layout —
// seeds keep their gx/gz, so grid adjacency (the frontier rule, the
// demesne growth) never changed meaning. The old corner jitter +
// SnapCornersToFeatures machinery is DELETED, not dormant.
//
// The countryside belongs to LORDSHIPS (Docs/Territories.md): every
// point maps to its nearest CastleSite, parcels exist only within
// SurveyReach of their own castle (worked land — the deep country
// between lordships is claimed, unworked and lawless), classification
// is relative per territory, and each castle grows ONE contiguous
// settled demesne — town by the gate, fields around it.
public static class LandParcels
{
    // The dials live on LandSurvey (inspector, the user's ask); a scene
    // without the component gets these script defaults. Generation-time
    // only — parcels are stored facts once surveyed.
    static float CellSize => LandSurvey.Instance != null
        ? Mathf.Max(20f, LandSurvey.Instance.cellSize) : 60f;
    // Reach 0 = survey the WHOLE map: lordships butt at the seams.
    static float SurveyReach => LandSurvey.Instance != null
        ? LandSurvey.Instance.surveyReach : 1000f;
    static float VillageAcres => LandSurvey.Instance != null
        ? Mathf.Max(1f, LandSurvey.Instance.villageAcres) : 15f;
    static int FarmPlots => LandSurvey.Instance != null
        ? LandSurvey.Instance.farmPlots : 5;
    static int PasturePlots => LandSurvey.Instance != null
        ? LandSurvey.Instance.pasturePlots : 3;
    // Seeds wander up to this fraction of a cell from their grid seat —
    // seed position only starts the flood, so unlike the old corner
    // jitter it can never make quads cross; it just de-grids the map.
    const float Jitter = 0.35f;
    // Terrain border left unparceled — nobody farms the world's edge.
    const float Margin = 30f;
    // More plots than this is GameObject soup — refuse and say so
    // rather than freeze the editor for minutes.
    const int MaxPlots = 25000;
    static bool warnedTooMany;

    // ---- The cost field ----
    // Raster resolution: a parcel should be ~14 samples across so its
    // boundary can follow a meander, clamped so a huge cell doesn't
    // blur the rivers and a tiny cell doesn't explode the raster.
    const float ResDivisor = 14f;
    const float ResMin = 6f, ResMax = 20f;
    // Quadratic slope penalty: cost/m = 1 + SlopeCost * slope². At
    // slope 0.15 travel costs ×2, at 0.3 ×5 — parcels spread over
    // their own flat and stall at the scarp, which also makes the
    // relief classification honest (parcels are internally uniform).
    const float SlopeCost = 45f;
    // Crossing a feature stripe costs this multiple. One ~res-wide
    // stripe ≈ 350 flat-equivalent meters: a parcel crosses a river
    // only when going around would cost more — boundaries land ON the
    // feature, following every meander at raster fidelity.
    const float FeatureCost = 25f;
    const float RoadHalfWidth = 4f;
    const float WaterExtraHalfWidth = 4f;
    // reach > 0 caps each flood at this many cells of GEOMETRIC travel
    // (cost decides ownership, meters decide reach) so frontier seeds
    // don't absorb the whole unworked deep country. reach 0 has no cap:
    // full coverage is the point.
    const float FringeCapCells = 2f;
    // Outline simplification tolerance as a fraction of the raster
    // resolution — enough to melt the stair-steps, under it so a
    // simplified chain stays inside its own sample tube.
    const float SimplifyFraction = 0.8f;

    // ---- The village flood ----
    // The town is GROWN, not picked from cells (2026-08-14, the
    // user's first-principles call: "the town would have grown out
    // from the castle... the general shape of a town is an irregular
    // circle"). The cell-picking tiers of 08-13 are DELETED, not
    // dormant — they could only ever hand the town whole survey
    // cells, and since the main flood's boundaries lie ALONG roads, a
    // town made of cells almost never spanned its own high street
    // (the one-sided-village bug). Instead: a dedicated flood seeds
    // where each trimmed approach road leaves the castle grounds —
    // the GATE — and spreads over the same raster with the road cost
    // INVERTED: for fields a road is a wall, for the town it is a
    // corridor. Slope is as dear as ever, water charges a punishing
    // toll, the grounds are frozen, and the flood stops when the town
    // holds its acres (the villageAcres dial). The historical shape
    // falls out: a blob pressed against the gate, elongated along the
    // streets, terracing over moderate ground, stopped at the river.
    const float AcreM2 = 4047f;
    // Walking ALONG a road (both samples on the stripe) costs this
    // fraction — the town runs out along its streets before it
    // thickens.
    const float VillageRoadDiscount = 0.35f;
    // Crossing water costs this multiple — punishing, not a wall. A
    // hard wall trapped a moated castle's flood between mask and moat
    // (White Castle stalled at 1.6 of 15 acres); a toll lets a trapped
    // town escape the ring while one with any other ground never
    // jumps its river (Dijkstra pays the toll only when everything
    // cheaper is spent).
    const float VillageWaterCost = 80f;
    // A hard geometric leash from the gate, whatever the costs — the
    // guard against string towns on ground where only the road is
    // cheap.
    const float VillageMaxReach = 400f;

    // ---- The settled demesne ----
    // Fields and pasture grow by TRUE adjacency around castle +
    // village, flattest-nearest first: a meter of distance costs this
    // much relief-equivalent, so the fields ring the castle instead
    // of chasing the flattest ground down the valley in a line.
    const float DistanceWeight = 0.02f;

    // Classification is RELATIVE to each territory's own terrain: the
    // flattest ~35% of a lordship's parcels are arable, the middle band
    // grazes, the steepest third is timber — absolute slope thresholds
    // on a hill map once yielded 219 timber, 1 farm and no village. A
    // Perlin forest mask still plants woods wherever it falls.
    const float FarmQuantile = 0.35f;
    const float PastureQuantile = 0.70f;
    const float ForestThreshold = 0.62f;
    const float ForestScale = 0.011f;

    // A site for generation and territory queries: real CastleSites
    // where the scene has them, else one synthetic player home at the
    // map center — the Spiš fallback, the old behavior minus neighbors.
    public struct Site
    {
        public string name;
        public Vector2 pos;
        public bool home;
    }

    public static List<Site> Sites()
    {
        var sites = new List<Site>();
        foreach (CastleSite s in CastleSite.Ordered())
            sites.Add(new Site
            {
                name = s.SiteName,
                pos = s.PositionXZ,
                home = s.isPlayerHome,
            });
        if (sites.Count == 0 && Ground.Any)
            sites.Add(new Site
            {
                name = "The Manor",
                pos = Ground.Bounds().center,
                home = true,
            });
        return sites;
    }

    // The territory partition: nearest site by XZ. Every point on the
    // map belongs to some lordship; the Voronoi boundary is the seam
    // where bandit camps belong when combat exists.
    public static int TerritoryOf(Vector2 p, List<Site> sites)
    {
        int best = 0;
        float bestSq = float.MaxValue;
        for (int i = 0; i < sites.Count; i++)
        {
            float sq = (sites[i].pos - p).sqrMagnitude;
            if (sq < bestSq)
            {
                bestSq = sq;
                best = i;
            }
        }
        return best;
    }

    public static string TerritoryName(int territory)
    {
        List<Site> sites = Sites();
        return territory >= 0 && territory < sites.Count
            ? sites[territory].name : "no lordship";
    }

    public static void EnsureGenerated()
    {
        if (LandPlot.All.Count > 0)
            return;
        if (!Ground.Any)
            return;
        List<Site> sites = Sites();
        if (sites.Count == 0)
            return;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // The survey is DETERMINISTIC in (terrain, sites, dials), so a
        // fresh play run restores the last generated survey from disk
        // instead of re-running the floods — near-instant. The
        // signature holds every generation input (plus CacheVersion,
        // bumped when the algorithm itself changes); a re-baked
        // terrain, a moved site or a turned dial misses and
        // regenerates. Player earthworks deliberately DON'T invalidate
        // — parcels are stored facts, reshaping ground never
        // re-classifies them. Saves still override wholesale (a loaded
        // save's plots mean this never runs).
        long sig = SurveySignature(sites);
        if (TryLoadCache(sig))
        {
            int trees0 = TimberWoods.Plant();
            BuildLog.Add("The estate recalled from the survey rolls —"
                + $" {LandPlot.All.Count} parcels in"
                + $" {sw.Elapsed.TotalSeconds:0.0}s"
                + (trees0 > 0 ? $"; {trees0} oaks stand in the timber."
                    : "."));
            return;
        }

        Rect bounds = Ground.Bounds();
        float cell = CellSize;
        float reach = SurveyReach;
        float originX = bounds.xMin + Margin;
        float originZ = bounds.yMin + Margin;
        int nx = Mathf.FloorToInt((bounds.width - Margin * 2f) / cell);
        int nz = Mathf.FloorToInt((bounds.height - Margin * 2f) / cell);

        // Pre-count what would materialize — a distance check per seed,
        // no height sampling — and refuse GameObject soup with advice
        // instead of a minutes-long freeze.
        int estimate = 0;
        for (int i = 0; i < nx && estimate <= MaxPlots; i++)
            for (int j = 0; j < nz; j++)
            {
                var mid = new Vector2(
                    originX + (i + 0.5f) * cell,
                    originZ + (j + 0.5f) * cell);
                if (reach <= 0f
                    || (sites[TerritoryOf(mid, sites)].pos - mid).magnitude
                        <= reach)
                    estimate++;
            }
        if (estimate > MaxPlots)
        {
            if (!warnedTooMany)
                BuildLog.Add($"Survey refused — over {MaxPlots} parcels at"
                    + $" {cell:0}m cells. Raise the cell size (or the"
                    + " reach) on Land Survey.");
            warnedTooMany = true;
            return;
        }
        warnedTooMany = false;

        // The castle grounds: terrain-fitted polygons, one per site
        // (CastleGrounds). Each is a frozen region in the flood — its
        // interior belongs to the castle and nothing else may enter —
        // and the castle routes (LandFeatures) already arrive trimmed
        // at its boundary — the trim point is the gate.
        var grounds = new List<List<Vector3>>(sites.Count);
        foreach (Site s in sites)
            grounds.Add(CastleGrounds.Outline(s.pos));

        // ---- The raster ----
        float res = Mathf.Clamp(cell / ResDivisor, ResMin, ResMax);
        int W = Mathf.Max(4, Mathf.FloorToInt((bounds.width - Margin * 2f) / res));
        int H = Mathf.Max(4, Mathf.FloorToInt((bounds.height - Margin * 2f) / res));
        int n = W * H;
        var height = new float[n];
        for (int iz = 0; iz < H; iz++)
            for (int ix = 0; ix < W; ix++)
                height[iz * W + ix] = Ground.HeightAt(
                    originX + (ix + 0.5f) * res,
                    originZ + (iz + 0.5f) * res);

        // Feature stripes, kept as SEPARATE masks because the two
        // floods read them oppositely: the parcel flood pays dearly to
        // cross either (boundaries lie along the lane or the brook),
        // the village flood walks roads at a discount and crosses
        // water only at a punishing toll. Castle routes + waterways
        // via LandFeatures, the ONE runtime door to the polylines.
        var roadAt = new bool[n];
        var waterAt = new bool[n];
        foreach (float[] road in LandFeatures.RoadLines())
            MarkStripe(roadAt, W, H, originX, originZ, res, road,
                RoadHalfWidth);
        foreach ((float[] p, float w) in LandFeatures.WaterLines())
            MarkStripe(waterAt, W, H, originX, originZ, res, p,
                w * 0.5f + WaterExtraHalfWidth);
        var mult = new float[n];
        for (int i = 0; i < n; i++)
            mult[i] = roadAt[i] || waterAt[i] ? FeatureCost : 1f;

        // The frozen castle mask. castleAt gates the flood (never relax
        // INTO a castle sample; castle samples are never pushed, so the
        // castle never grows past its own outline).
        var castleAt = new int[n];
        for (int i = 0; i < n; i++)
            castleAt[i] = -1;
        for (int t = 0; t < sites.Count; t++)
        {
            List<Vector3> g = grounds[t];
            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            foreach (Vector3 v in g)
            {
                minX = Mathf.Min(minX, v.x); maxX = Mathf.Max(maxX, v.x);
                minZ = Mathf.Min(minZ, v.z); maxZ = Mathf.Max(maxZ, v.z);
            }
            int x0 = Mathf.Max(0, Mathf.FloorToInt((minX - originX) / res));
            int x1 = Mathf.Min(W - 1, Mathf.FloorToInt((maxX - originX) / res));
            int z0 = Mathf.Max(0, Mathf.FloorToInt((minZ - originZ) / res));
            int z1 = Mathf.Min(H - 1, Mathf.FloorToInt((maxZ - originZ) / res));
            for (int iz = z0; iz <= z1; iz++)
                for (int ix = x0; ix <= x1; ix++)
                    if (SlabTile.Contains(g, new Vector3(
                        originX + (ix + 0.5f) * res, 0f,
                        originZ + (iz + 0.5f) * res)))
                        castleAt[iz * W + ix] = t;
        }

        // Each castle's approaches: the trimmed routes whose castle
        // end stops within GateReach of the site, oriented to walk
        // OUT from the gate. Needed here (before the floods) because
        // the gate points seed the village.
        var approaches = new List<float[]>[sites.Count];
        for (int t = 0; t < sites.Count; t++)
            approaches[t] = new List<float[]>();
        foreach (float[] r in LandFeatures.RoadLines())
        {
            if (r.Length < 4)
                continue;
            var head = new Vector2(r[0], r[1]);
            var tail = new Vector2(r[r.Length - 2], r[r.Length - 1]);
            for (int t = 0; t < sites.Count; t++)
            {
                if ((head - sites[t].pos).magnitude
                    <= CastleGrounds.GateReach)
                    approaches[t].Add(r);
                else if ((tail - sites[t].pos).magnitude
                    <= CastleGrounds.GateReach)
                    approaches[t].Add(Reversed(r));
            }
        }

        int[] dirX = { 1, -1, 0, 0, 1, 1, -1, -1 };
        int[] dirZ = { 0, 0, 1, -1, 1, -1, 1, -1 };
        float diagStep = res * 1.41421356f;

        // ---- The village flood (the const block explains the why) ----
        // Seeds sit just outside each gate: the trimmed route's castle
        // end is the LAST point inside the grounds, so walk outward to
        // the first sample past the mask. A castle no road reaches
        // (Spiš) seeds the whole ring around its grounds instead — the
        // village hugs the castle rather than a gate.
        var villageAt = new int[n];
        for (int i = 0; i < n; i++)
            villageAt[i] = -1;
        var villageClaims = new int[sites.Count];
        {
            var vdist = new float[n];
            var vgeo = new float[n];
            var vlabel = new int[n];
            var claimGeo = new float[n];
            var banned = new bool[n];
            var gateSeeds = new HashSet<int>[sites.Count];
            for (int t = 0; t < sites.Count; t++)
                gateSeeds[t] = new HashSet<int>();
            void SeedVillage(int s, int t) => gateSeeds[t].Add(s);
            for (int t = 0; t < sites.Count; t++)
            {
                bool any = false;
                foreach (float[] r in approaches[t])
                    for (int i = 0; i + 1 < r.Length; i += 2)
                    {
                        int ix = Mathf.FloorToInt((r[i] - originX) / res);
                        int iz = Mathf.FloorToInt((r[i + 1] - originZ) / res);
                        if (ix < 0 || iz < 0 || ix >= W || iz >= H)
                            continue;
                        int s = iz * W + ix;
                        if (castleAt[s] >= 0)
                            continue;
                        SeedVillage(s, t);
                        any = true;
                        break;
                    }
                if (any)
                    continue;
                for (int i = 0; i < n; i++)
                {
                    if (castleAt[i] != t)
                        continue;
                    int px = i % W, pz = i / W;
                    for (int k = 0; k < 4; k++)
                    {
                        int qx = px + dirX[k], qz = pz + dirZ[k];
                        if (qx < 0 || qz < 0 || qx >= W || qz >= H)
                            continue;
                        int nb = qz * W + qx;
                        if (castleAt[nb] < 0)
                            SeedVillage(nb, t);
                    }
                }
            }
            // Dijkstra with a per-town acre budget, claimed at pop
            // (pop order IS cost order, so the town keeps its
            // cheapest ground and stops mid-frontier when it holds
            // its acres) — in ROUNDS: pruning (below) refunds what it
            // unclaims, and the next round re-floods from the
            // surviving town so the budget is spent on ground that
            // can actually trace. Pruned pinch samples are BANNED so
            // the same corner is never fought over twice; four rounds
            // is a ceiling, one refill is the norm.
            var remaining = new float[sites.Count];
            for (int t = 0; t < sites.Count; t++)
                remaining[t] = VillageAcres * AcreM2;
            float sampleArea = res * res;
            for (int round = 0; round < 4; round++)
            {
            for (int i = 0; i < n; i++)
            {
                vdist[i] = float.PositiveInfinity;
                vlabel[i] = -1;
            }
            var vheap = new MinHeap();
            if (round == 0)
            {
                for (int t = 0; t < sites.Count; t++)
                    foreach (int s in gateSeeds[t])
                        if (vdist[s] > 0f)
                        {
                            vdist[s] = 0f;
                            vgeo[s] = 0f;
                            vlabel[s] = t;
                            vheap.Push(0f, s);
                        }
            }
            else
                for (int i = 0; i < n; i++)
                    if (villageAt[i] >= 0)
                    {
                        vdist[i] = 0f;
                        vgeo[i] = claimGeo[i];
                        vlabel[i] = villageAt[i];
                        vheap.Push(0f, i);
                    }
            while (vheap.Count > 0)
            {
                (float d, int node) = vheap.Pop();
                if (d > vdist[node])
                    continue;
                int t = vlabel[node];
                if (villageAt[node] < 0)
                {
                    if (remaining[t] <= 0f)
                        continue;
                    villageAt[node] = t;
                    villageClaims[t]++;
                    claimGeo[node] = vgeo[node];
                    remaining[t] -= sampleArea;
                }
                if (remaining[t] <= 0f)
                    continue;
                int px = node % W, pz = node / W;
                // 4-neighbor, DELIBERATELY not 8 like the main flood:
                // the cheap road corridors run diagonally, and an
                // 8-neighbor claim strings samples together by their
                // corners — the outline tracer reads a diagonal
                // self-touch as a junction and keeps only the largest
                // loop (Skenfrith traced 9.1 of its 15 claimed acres,
                // White Castle only its corridor stub). Edge-connected
                // claims cannot pinch.
                for (int k = 0; k < 4; k++)
                {
                    int qx = px + dirX[k], qz = pz + dirZ[k];
                    if (qx < 0 || qz < 0 || qx >= W || qz >= H)
                        continue;
                    int nb = qz * W + qx;
                    if (castleAt[nb] >= 0 || banned[nb])
                        continue;
                    float step = res;
                    // Water pays the toll; dry road-to-road travel
                    // earns the discount.
                    float m = Mathf.Max(
                        waterAt[node] ? VillageWaterCost : 1f,
                        waterAt[nb] ? VillageWaterCost : 1f);
                    if (m <= 1f && roadAt[node] && roadAt[nb])
                        m = VillageRoadDiscount;
                    float slope = (height[nb] - height[node]) / step;
                    float nd = d + step
                        * (1f + SlopeCost * slope * slope) * m;
                    if (nd >= vdist[nb])
                        continue;
                    float ng = vgeo[node] + step;
                    if (ng > VillageMaxReach)
                        continue;
                    vdist[nb] = nd;
                    vgeo[nb] = ng;
                    vlabel[nb] = t;
                    vheap.Push(nd, nb);
                }
            }

            // A village must trace as ONE simple loop, so diagonal
            // self-touches are removed before the outline ever forms:
            // a town wrapping the rasterized castle grounds (or
            // growing around a stripe) leaves two of its samples
            // kissing at a corner, the boundary becomes a figure-8,
            // and the tracer keeps one lobe (Skenfrith held 9.4 of
            // its 15 claimed acres this way). Unclaim (and BAN) the
            // costlier of each kissing pair until none remain, then
            // keep each town's largest 4-connected piece — orphaned
            // arms are refunded and re-spent by the next round, and
            // their ground returns to the field flood.
            int cleaned = 0;
            bool pinched = true;
            while (pinched)
            {
                pinched = false;
                for (int iz = 0; iz + 1 < H; iz++)
                    for (int ix = 0; ix + 1 < W; ix++)
                    {
                        int i00 = iz * W + ix;
                        int i10 = i00 + 1;
                        int i01 = i00 + W;
                        int i11 = i01 + 1;
                        int da, db;
                        if (villageAt[i00] >= 0
                            && villageAt[i00] == villageAt[i11]
                            && villageAt[i10] != villageAt[i00]
                            && villageAt[i01] != villageAt[i00])
                        { da = i00; db = i11; }
                        else if (villageAt[i10] >= 0
                            && villageAt[i10] == villageAt[i01]
                            && villageAt[i00] != villageAt[i10]
                            && villageAt[i11] != villageAt[i10])
                        { da = i10; db = i01; }
                        else
                            continue;
                        int drop = vdist[da] >= vdist[db] ? da : db;
                        villageClaims[villageAt[drop]]--;
                        remaining[villageAt[drop]] += sampleArea;
                        villageAt[drop] = -1;
                        banned[drop] = true;
                        pinched = true;
                        cleaned++;
                    }
            }
            var comp = new int[n];
            for (int i = 0; i < n; i++)
                comp[i] = -1;
            var compSize = new List<int>();
            var compSite = new List<int>();
            var stack = new Stack<int>();
            for (int i = 0; i < n; i++)
            {
                if (villageAt[i] < 0 || comp[i] >= 0)
                    continue;
                int id = compSize.Count;
                compSize.Add(0);
                compSite.Add(villageAt[i]);
                comp[i] = id;
                stack.Push(i);
                while (stack.Count > 0)
                {
                    int node = stack.Pop();
                    compSize[id]++;
                    int px = node % W, pz = node / W;
                    for (int k = 0; k < 4; k++)
                    {
                        int qx = px + dirX[k], qz = pz + dirZ[k];
                        if (qx < 0 || qz < 0 || qx >= W || qz >= H)
                            continue;
                        int nb = qz * W + qx;
                        if (comp[nb] >= 0
                            || villageAt[nb] != villageAt[node])
                            continue;
                        comp[nb] = id;
                        stack.Push(nb);
                    }
                }
            }
            var bestComp = new int[sites.Count];
            for (int t = 0; t < sites.Count; t++)
                bestComp[t] = -1;
            for (int id = 0; id < compSize.Count; id++)
                if (bestComp[compSite[id]] < 0
                    || compSize[id] > compSize[bestComp[compSite[id]]])
                    bestComp[compSite[id]] = id;
            for (int i = 0; i < n; i++)
                if (villageAt[i] >= 0 && comp[i] != bestComp[villageAt[i]])
                {
                    villageClaims[villageAt[i]]--;
                    remaining[villageAt[i]] += sampleArea;
                    villageAt[i] = -1;
                    cleaned++;
                }
            if (cleaned == 0)
                break;
            }
        }

        // ---- Seeds ----
        // One per lattice cell within reach, jittered off the grid,
        // skipped where the castle or the village already claims the
        // ground — no hole follows the skip: the neighbors' floods
        // absorb the area the frozen polygons don't cover.
        var cells = new List<Cell>();
        var seedTaken = new HashSet<int>();
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < nz; j++)
            {
                var pos = new Vector2(
                    originX + (i + 0.5f) * cell
                        + (Hash01(i, j, 1) - 0.5f) * 2f * Jitter * cell,
                    originZ + (j + 0.5f) * cell
                        + (Hash01(i, j, 2) - 0.5f) * 2f * Jitter * cell);
                int territory = TerritoryOf(pos, sites);
                if (reach > 0f
                    && (sites[territory].pos - pos).magnitude > reach)
                    continue;
                int ix = Mathf.FloorToInt((pos.x - originX) / res);
                int iz = Mathf.FloorToInt((pos.y - originZ) / res);
                if (ix < 0 || iz < 0 || ix >= W || iz >= H)
                    continue;
                int s = iz * W + ix;
                if (castleAt[s] >= 0 || villageAt[s] >= 0
                    || !seedTaken.Add(s))
                    continue;
                cells.Add(new Cell
                {
                    gx = i,
                    gz = j,
                    territory = territory,
                    mid = pos,
                    region = cells.Count,
                    sample = s,
                });
            }
        int cellRegions = cells.Count;
        // Region ids: [0, cellRegions) cells, then a castle region per
        // site, then a VILLAGE region per site.
        int regionTotal = cellRegions + sites.Count * 2;

        // ---- The flood: multi-source Dijkstra, 8-neighbor ----
        var dist = new float[n];
        var geo = new float[n];
        var label = new int[n];
        for (int i = 0; i < n; i++)
        {
            dist[i] = float.PositiveInfinity;
            label[i] = -1;
        }
        for (int i = 0; i < n; i++)
            if (castleAt[i] >= 0)
            {
                label[i] = cellRegions + castleAt[i];
                dist[i] = 0f;
            }
            else if (villageAt[i] >= 0)
            {
                label[i] = cellRegions + sites.Count + villageAt[i];
                dist[i] = 0f;
            }
        var heap = new MinHeap();
        foreach (Cell c in cells)
        {
            dist[c.sample] = 0f;
            label[c.sample] = c.region;
            heap.Push(0f, c.sample);
        }
        float cap = reach > 0f ? cell * FringeCapCells : 0f;
        while (heap.Count > 0)
        {
            (float d, int node) = heap.Pop();
            if (d > dist[node])
                continue;
            int px = node % W, pz = node / W;
            for (int k = 0; k < 8; k++)
            {
                int qx = px + dirX[k], qz = pz + dirZ[k];
                if (qx < 0 || qz < 0 || qx >= W || qz >= H)
                    continue;
                int nb = qz * W + qx;
                if (castleAt[nb] >= 0 || villageAt[nb] >= 0)
                    continue;
                bool diag = k >= 4;
                float step = diag ? diagStep : res;
                float m = Mathf.Max(mult[node], mult[nb]);
                // A diagonal hop must pay the corner samples too, or a
                // one-sample stripe leaks at every diagonal pinch.
                if (diag)
                {
                    m = Mathf.Max(m, mult[pz * W + qx]);
                    m = Mathf.Max(m, mult[qz * W + px]);
                }
                float slope = (height[nb] - height[node]) / step;
                float nd = d + step * (1f + SlopeCost * slope * slope) * m;
                if (nd >= dist[nb])
                    continue;
                float ng = geo[node] + step;
                if (cap > 0f && ng > cap)
                    continue;
                dist[nb] = nd;
                geo[nb] = ng;
                label[nb] = label[node];
                heap.Push(nd, nb);
            }
        }

        // Relief from every claimed sample — the whole parcel judges
        // itself, not five probe points.
        var reliefLo = new float[cellRegions];
        var reliefHi = new float[cellRegions];
        for (int r = 0; r < cellRegions; r++)
        {
            reliefLo[r] = float.MaxValue;
            reliefHi[r] = float.MinValue;
        }
        for (int i = 0; i < n; i++)
        {
            int r = label[i];
            if (r < 0 || r >= cellRegions)
                continue;
            reliefLo[r] = Mathf.Min(reliefLo[r], height[i]);
            reliefHi[r] = Mathf.Max(reliefHi[r], height[i]);
        }

        // TRUE adjacency between regions — a shared raster boundary,
        // not lattice arithmetic: the flood shapes regions organically,
        // so "touching" must be read off the map the flood actually
        // drew (gx/gz Manhattan once strung the demesne out in a line
        // where lattice neighbors weren't real neighbors). The STORED
        // frontier rule (TouchesPlayerLand) keeps gx/gz — post-load
        // there is no raster. Castle regions participate: adjacency to
        // cellRegions+t is "borders the castle grounds".
        var adj = new HashSet<long>();
        for (int iz = 0; iz < H; iz++)
            for (int ix = 0; ix < W; ix++)
            {
                int a = label[iz * W + ix];
                if (ix + 1 < W)
                    LinkAdj(adj, a, label[iz * W + ix + 1]);
                if (iz + 1 < H)
                    LinkAdj(adj, a, label[(iz + 1) * W + ix]);
            }

        // ---- Outlines from the label raster ----
        // Shared boundary chains, simplified ONCE each, so both
        // neighbors keep identical geometry — the parcel map tiles
        // exactly, which is the whole point of the rebuild.
        List<Vector3>[] outlines = TraceOutlines(label, W, H,
            originX, originZ, res, regionTotal);

        // Cells whose outline failed to close (pinch-point stitches —
        // rare, cosmetic) are dropped; their ground stays claimed on
        // the raster but unparceled, accepted at survey scale.
        var kept = new List<Cell>();
        foreach (Cell c in cells)
        {
            c.outline = outlines[c.region];
            if (c.outline == null || c.outline.Count < 3)
                continue;
            c.relief = reliefHi[c.region] >= reliefLo[c.region]
                ? reliefHi[c.region] - reliefLo[c.region] : 0f;
            c.area = Mathf.Abs(SlabTile.SignedArea(c.outline));
            c.forest = Mathf.PerlinNoise((c.mid.x + 431f) * ForestScale,
                (c.mid.y + 977f) * ForestScale) > ForestThreshold;
            kept.Add(c);
        }

        // Each lordship classifies against its own country and grows
        // its own settled demesne around castle + village.
        for (int t = 0; t < sites.Count; t++)
            SettleTerritory(sites[t], cellRegions + t,
                cellRegions + sites.Count + t,
                kept.FindAll(c => c.territory == t), adj);

        // The castle parcels themselves. The STORED parcel is the
        // traced raster outline — the same boundary its neighbors
        // traced, so the map tiles; the smooth CastleGrounds polygon
        // remains the generator's mask and the road-trim line. Grid
        // coords are the seat cell under the site, so the frontier
        // rule's one-step adjacency keeps working around the castle.
        for (int t = 0; t < sites.Count; t++)
        {
            List<Vector3> co = outlines[cellRegions + t];
            if (co == null || co.Count < 3)
                co = grounds[t];
            LandPlot.Create(co, LandType.Castle,
                sites[t].home ? LandOwner.Player : LandOwner.Neighbor,
                Mathf.FloorToInt((sites[t].pos.x - originX) / cell),
                Mathf.FloorToInt((sites[t].pos.y - originZ) / cell), t);
        }

        // The village parcels: the flood-grown town, ONE Living
        // polygon per lordship, traced from the same label raster as
        // everything else so the map tiles. Grid coords come from the
        // outline's centroid — the frontier rule's one-step adjacency
        // keeps working around the town.
        for (int t = 0; t < sites.Count; t++)
        {
            List<Vector3> vo = outlines[cellRegions + sites.Count + t];
            if (vo == null || vo.Count < 3)
                continue;
            // The traced polygon must hold what the flood claimed —
            // a shortfall means the outline fragmented (the 8-neighbor
            // corner-pinch bug wore this disguise once).
            float tracedAcres = Mathf.Abs(SlabTile.SignedArea(vo)) / AcreM2;
            float claimedAcres = villageClaims[t] * res * res / AcreM2;
            if (tracedAcres < claimedAcres * 0.8f)
                Debug.LogWarning($"{sites[t].name}: village outline holds"
                    + $" {tracedAcres:0.0} of {claimedAcres:0.0} claimed"
                    + " acres — fragmented trace.");
            var mid = Vector2.zero;
            foreach (Vector3 v in vo)
                mid += new Vector2(v.x, v.z);
            mid /= vo.Count;
            LandPlot.Create(vo, LandType.Living,
                sites[t].home ? LandOwner.Player : LandOwner.Neighbor,
                Mathf.FloorToInt((mid.x - originX) / cell),
                Mathf.FloorToInt((mid.y - originZ) / cell), t);
        }

        foreach (Cell c in kept)
        {
            LandType type = c.settled
                ?? (c.forest ? LandType.Timber
                : c.relief <= c.farmCut ? LandType.Farm
                : c.relief <= c.pastureCut ? LandType.Pasture
                : LandType.Timber);
            // Bandits are STOOD DOWN for now (the user's call,
            // 2026-08-13): the whole home lordship starts in the
            // player's hands instead of bandit-held-until-cleared. The
            // enum, the frontier rule and the Survey tool's clearing
            // all stay in place for their return. A neighbor's
            // territory is wholly theirs; their estates are their
            // problem.
            LandOwner owner = sites[c.territory].home
                ? LandOwner.Player : LandOwner.Neighbor;
            LandPlot.Create(c.outline, type, owner, c.gx, c.gz, c.territory);
        }
        SaveCache(sig);
        int trees = TimberWoods.Plant();
        BuildLog.Add($"The estate surveyed — {kept.Count + sites.Count}"
            + $" parcels across {sites.Count}"
            + $" lordship{(sites.Count == 1 ? "" : "s")} in"
            + $" {sw.Elapsed.TotalSeconds:0.0}s"
            + (trees > 0 ? $"; {trees} oaks stand in the timber." : "."));
    }

    // ---- The survey cache ----
    // One file per terrain beside the features asset: the generated
    // plots verbatim, stamped with the input signature. Derived data,
    // never authoritative — delete it freely (Resurvey does).
    const int CacheVersion = 1;

    [System.Serializable]
    class CachedPlot
    {
        public float[] v;
        public int type, owner, gx, gz, terr;
    }

    [System.Serializable]
    class SurveyCacheFile
    {
        public long sig;
        public CachedPlot[] plots;
    }

    static string CachePath => System.IO.Path.Combine(
        Application.dataPath, "Terrain",
        "SurveyCache-" + Ground.BaseName + ".json");

    // FNV-1a over every generation input: dials, sites, and a coarse
    // 33×33 sweep of the heightmap (a re-bake — Split, Carve, Dress —
    // moves samples; a player bench during play does too, but
    // generation happens before any of those run in a fresh session).
    static long SurveySignature(List<Site> sites)
    {
        unchecked
        {
            ulong h = 1469598103934665603UL;
            void Mix(int q) { h = (h ^ (uint)q) * 1099511628211UL; }
            void MixF(float f) { Mix(Mathf.RoundToInt(f * 100f)); }
            Mix(CacheVersion);
            MixF(CellSize);
            MixF(SurveyReach);
            MixF(VillageAcres);
            Mix(FarmPlots);
            Mix(PasturePlots);
            LandSurvey s = LandSurvey.Instance;
            MixF(s != null ? s.castleGroundsMin : 60f);
            MixF(s != null ? s.castleGroundsMax : 140f);
            MixF(s != null ? s.castleGroundsRelief : 6f);
            foreach (Site st in sites)
            {
                foreach (char c in st.name)
                    Mix(c);
                MixF(st.pos.x);
                MixF(st.pos.y);
                Mix(st.home ? 1 : 0);
            }
            Rect b = Ground.Bounds();
            for (int i = 0; i <= 32; i++)
                for (int j = 0; j <= 32; j++)
                    MixF(Ground.HeightAt(
                        b.xMin + b.width * i / 32f,
                        b.yMin + b.height * j / 32f));
            return (long)h;
        }
    }

    static bool TryLoadCache(long sig)
    {
        try
        {
            string path = CachePath;
            if (!System.IO.File.Exists(path))
                return false;
            var file = JsonUtility.FromJson<SurveyCacheFile>(
                System.IO.File.ReadAllText(path));
            if (file == null || file.sig != sig || file.plots == null
                || file.plots.Length == 0)
                return false;
            var outline = new List<Vector3>();
            foreach (CachedPlot cp in file.plots)
            {
                outline.Clear();
                for (int i = 0; i + 1 < cp.v.Length; i += 2)
                    outline.Add(new Vector3(cp.v[i], 0f, cp.v[i + 1]));
                LandPlot.Create(outline, (LandType)cp.type,
                    (LandOwner)cp.owner, cp.gx, cp.gz, cp.terr);
            }
            return true;
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("Survey cache unreadable — regenerating: "
                + e.Message);
            // A partial restore would double up with the regeneration.
            foreach (LandPlot p in LandPlot.All.ToArray())
                if (p != null)
                    Object.DestroyImmediate(p.gameObject);
            return false;
        }
    }

    static void SaveCache(long sig)
    {
        try
        {
            var plots = new List<CachedPlot>(LandPlot.All.Count);
            foreach (LandPlot p in LandPlot.All)
            {
                if (p == null)
                    continue;
                var v = new float[p.verts.Count * 2];
                for (int i = 0; i < p.verts.Count; i++)
                {
                    v[i * 2] = p.verts[i].x;
                    v[i * 2 + 1] = p.verts[i].z;
                }
                plots.Add(new CachedPlot
                {
                    v = v,
                    type = (int)p.type,
                    owner = (int)p.owner,
                    gx = p.gx,
                    gz = p.gz,
                    terr = p.territory,
                });
            }
            System.IO.Directory.CreateDirectory(
                System.IO.Path.GetDirectoryName(CachePath));
            System.IO.File.WriteAllText(CachePath, JsonUtility.ToJson(
                new SurveyCacheFile { sig = sig, plots = plots.ToArray() }));
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("Survey cache not written: " + e.Message);
        }
    }

    // Resurvey's door: with the cache gone, the next EnsureGenerated
    // truly re-runs — dial tuning and algorithm changes both land.
    public static void InvalidateCache()
    {
        try
        {
            if (System.IO.File.Exists(CachePath))
                System.IO.File.Delete(CachePath);
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("Survey cache not deleted: " + e.Message);
        }
    }

    // Paints a feature stripe into a mask: samples whose center lies
    // within halfWidth (+ half a sample, so a narrow lane still lands
    // a full stripe) of the polyline are flagged.
    static void MarkStripe(bool[] flag, int W, int H,
        float originX, float originZ, float res, float[] p, float halfWidth)
    {
        float reach = halfWidth + res * 0.5f;
        int rad = Mathf.Max(1, Mathf.CeilToInt(reach / res));
        float step = res * 0.5f;
        for (int i = 0; i + 3 < p.Length; i += 2)
        {
            var a = new Vector2(p[i], p[i + 1]);
            var b = new Vector2(p[i + 2], p[i + 3]);
            int steps = Mathf.Max(1, Mathf.CeilToInt((b - a).magnitude / step));
            for (int k = 0; k <= steps; k++)
            {
                Vector2 q = Vector2.Lerp(a, b, (float)k / steps);
                int cx = Mathf.FloorToInt((q.x - originX) / res);
                int cz = Mathf.FloorToInt((q.y - originZ) / res);
                for (int dx = -rad; dx <= rad; dx++)
                    for (int dz = -rad; dz <= rad; dz++)
                    {
                        int ix = cx + dx, iz = cz + dz;
                        if (ix < 0 || iz < 0 || ix >= W || iz >= H)
                            continue;
                        var center = new Vector2(
                            originX + (ix + 0.5f) * res,
                            originZ + (iz + 0.5f) * res);
                        if ((center - q).sqrMagnitude <= reach * reach)
                            flag[iz * W + ix] = true;
                    }
            }
        }
    }

    // ---- Outline tracing ----
    // The label raster's boundary edges (between differing labels;
    // off-raster and unclaimed count as -1) chain between JUNCTION
    // vertices (degree ≠ 2). Each chain borders exactly two labels and
    // is simplified once — both regions then assemble their loops from
    // the same simplified chains, so shared boundaries are identical
    // by construction. A region's outline is its largest closed loop;
    // smaller fragments (pinch points) are dropped, accepted at
    // survey scale.
    class Chain
    {
        public int v0, v1, lo, hi;
        public bool closed;
        public List<Vector2> pts;
    }

    static List<Vector3>[] TraceOutlines(int[] label, int W, int H,
        float originX, float originZ, float res, int regionTotal)
    {
        // Boundary edges on the vertex grid (W+1)×(H+1).
        var edgeA = new List<int>();
        var edgeB = new List<int>();
        var edgeLo = new List<int>();
        var edgeHi = new List<int>();
        var byVertex = new Dictionary<int, List<int>>();
        void AddEdge(int a, int b, int l0, int l1)
        {
            int e = edgeA.Count;
            edgeA.Add(a); edgeB.Add(b);
            edgeLo.Add(Mathf.Min(l0, l1)); edgeHi.Add(Mathf.Max(l0, l1));
            if (!byVertex.TryGetValue(a, out var la))
                byVertex[a] = la = new List<int>(2);
            la.Add(e);
            if (!byVertex.TryGetValue(b, out var lb))
                byVertex[b] = lb = new List<int>(2);
            lb.Add(e);
        }
        for (int iz = 0; iz < H; iz++)
            for (int ix = 0; ix <= W; ix++)
            {
                int l = ix > 0 ? label[iz * W + ix - 1] : -1;
                int r = ix < W ? label[iz * W + ix] : -1;
                if (l != r)
                    AddEdge(iz * (W + 1) + ix, (iz + 1) * (W + 1) + ix, l, r);
            }
        for (int iz = 0; iz <= H; iz++)
            for (int ix = 0; ix < W; ix++)
            {
                int d = iz > 0 ? label[(iz - 1) * W + ix] : -1;
                int u = iz < H ? label[iz * W + ix] : -1;
                if (d != u)
                    AddEdge(iz * (W + 1) + ix, iz * (W + 1) + ix + 1, d, u);
            }

        // Chains: walk from junctions first, then sweep pure loops.
        var used = new bool[edgeA.Count];
        var chains = new List<Chain>();
        float tol = res * SimplifyFraction;
        Vector2 World(int v) => new Vector2(
            originX + (v % (W + 1)) * res,
            originZ + (v / (W + 1)) * res);
        void WalkFrom(int e0, int start)
        {
            var verts = new List<int> { start };
            int e = e0, cur = start;
            while (true)
            {
                used[e] = true;
                int nxt = edgeA[e] == cur ? edgeB[e] : edgeA[e];
                verts.Add(nxt);
                cur = nxt;
                if (nxt == verts[0])
                    break;
                List<int> inc = byVertex[nxt];
                if (inc.Count != 2)
                    break;
                int e2 = inc[0] == e ? inc[1] : inc[0];
                if (used[e2])
                    break;
                e = e2;
            }
            var pts = new List<Vector2>(verts.Count);
            foreach (int v in verts)
                pts.Add(World(v));
            bool closed = verts[0] == verts[verts.Count - 1];
            Simplify(pts, tol, closed);
            chains.Add(new Chain
            {
                v0 = verts[0],
                v1 = verts[verts.Count - 1],
                lo = edgeLo[e0],
                hi = edgeHi[e0],
                closed = closed,
                pts = pts,
            });
        }
        for (int e = 0; e < edgeA.Count; e++)
        {
            if (used[e])
                continue;
            int a = edgeA[e], b = edgeB[e];
            bool ja = byVertex[a].Count != 2, jb = byVertex[b].Count != 2;
            if (ja || jb)
                WalkFrom(e, ja ? a : b);
        }
        for (int e = 0; e < edgeA.Count; e++)
            if (!used[e])
                WalkFrom(e, edgeA[e]);

        // Assemble each region's loops from its chains; keep the
        // largest by area as the outline.
        var chainsOf = new List<int>[regionTotal];
        for (int c = 0; c < chains.Count; c++)
        {
            Chain ch = chains[c];
            if (ch.lo >= 0 && ch.lo < regionTotal)
                (chainsOf[ch.lo] ??= new List<int>()).Add(c);
            if (ch.hi >= 0 && ch.hi < regionTotal && ch.hi != ch.lo)
                (chainsOf[ch.hi] ??= new List<int>()).Add(c);
        }
        var outlines = new List<Vector3>[regionTotal];
        var loop = new List<Vector2>();
        for (int r = 0; r < regionTotal; r++)
        {
            if (chainsOf[r] == null)
                continue;
            var usedC = new HashSet<int>();
            var endMap = new Dictionary<int, List<int>>();
            foreach (int ci in chainsOf[r])
            {
                Chain ch = chains[ci];
                if (ch.closed)
                    continue;
                (endMap.TryGetValue(ch.v0, out var l0)
                    ? l0 : endMap[ch.v0] = new List<int>(2)).Add(ci);
                (endMap.TryGetValue(ch.v1, out var l1)
                    ? l1 : endMap[ch.v1] = new List<int>(2)).Add(ci);
            }
            List<Vector2> best = null;
            float bestArea = 0f;
            foreach (int ci in chainsOf[r])
            {
                if (usedC.Contains(ci))
                    continue;
                Chain first = chains[ci];
                usedC.Add(ci);
                loop.Clear();
                bool ok = true;
                if (first.closed)
                {
                    for (int i = 0; i < first.pts.Count - 1; i++)
                        loop.Add(first.pts[i]);
                }
                else
                {
                    loop.AddRange(first.pts);
                    int startV = first.v0, cur = first.v1;
                    while (cur != startV)
                    {
                        int next = -1;
                        if (endMap.TryGetValue(cur, out var cand))
                            foreach (int cj in cand)
                                if (!usedC.Contains(cj))
                                {
                                    next = cj;
                                    break;
                                }
                        if (next < 0)
                        {
                            ok = false;
                            break;
                        }
                        Chain ch = chains[next];
                        usedC.Add(next);
                        if (ch.v0 == cur)
                        {
                            for (int i = 1; i < ch.pts.Count; i++)
                                loop.Add(ch.pts[i]);
                            cur = ch.v1;
                        }
                        else
                        {
                            for (int i = ch.pts.Count - 2; i >= 0; i--)
                                loop.Add(ch.pts[i]);
                            cur = ch.v0;
                        }
                    }
                    // The stitch closes on its start vertex — the final
                    // point duplicates the first; drop it.
                    if (ok && loop.Count > 1)
                        loop.RemoveAt(loop.Count - 1);
                }
                if (!ok || loop.Count < 3)
                    continue;
                float area = Mathf.Abs(Shoelace(loop));
                if (area > bestArea)
                {
                    bestArea = area;
                    best = new List<Vector2>(loop);
                }
            }
            if (best != null)
            {
                var o = new List<Vector3>(best.Count);
                foreach (Vector2 v in best)
                    o.Add(new Vector3(v.x, 0f, v.y));
                outlines[r] = o;
            }
        }
        return outlines;
    }

    static float Shoelace(List<Vector2> pts)
    {
        float a = 0f;
        for (int i = 0; i < pts.Count; i++)
        {
            Vector2 p = pts[i], q = pts[(i + 1) % pts.Count];
            a += p.x * q.y - q.x * p.y;
        }
        return a * 0.5f;
    }

    // Douglas-Peucker in place. Endpoints always survive; a closed
    // chain also anchors its midpoint so a small enclave can't
    // collapse to a line.
    static void Simplify(List<Vector2> pts, float tol, bool closed)
    {
        if (pts.Count < 3)
            return;
        var keep = new bool[pts.Count];
        keep[0] = keep[pts.Count - 1] = true;
        if (closed && pts.Count > 3)
        {
            int mid = pts.Count / 2;
            keep[mid] = true;
            DPRange(pts, 0, mid, tol * tol, keep);
            DPRange(pts, mid, pts.Count - 1, tol * tol, keep);
        }
        else
            DPRange(pts, 0, pts.Count - 1, tol * tol, keep);
        int w = 0;
        for (int i = 0; i < pts.Count; i++)
            if (keep[i])
                pts[w++] = pts[i];
        pts.RemoveRange(w, pts.Count - w);
    }

    static void DPRange(List<Vector2> pts, int i0, int i1, float tolSq,
        bool[] keep)
    {
        if (i1 - i0 < 2)
            return;
        Vector2 a = pts[i0], b = pts[i1];
        Vector2 ab = b - a;
        float len = ab.sqrMagnitude;
        float worst = tolSq;
        int at = -1;
        for (int i = i0 + 1; i < i1; i++)
        {
            Vector2 p = pts[i];
            float d;
            if (len < 1e-9f)
                d = (p - a).sqrMagnitude;
            else
            {
                float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / len);
                d = (p - (a + ab * t)).sqrMagnitude;
            }
            if (d > worst)
            {
                worst = d;
                at = i;
            }
        }
        if (at < 0)
            return;
        keep[at] = true;
        DPRange(pts, i0, at, tolSq, keep);
        DPRange(pts, at, i1, tolSq, keep);
    }

    // Relief quantiles set this territory's farm and pasture cuts.
    // The castle parcel and the flood-grown village are created by
    // the caller; fields and pasture grow by TRUE adjacency around
    // both, flattest-nearest first (relief plus a distance penalty),
    // one contiguous demesne ringing the castle. The village TIER
    // system that used to live here is DELETED, not dormant — the
    // village flood replaced it (the const block explains).
    static void SettleTerritory(Site site, int castleRegion,
        int villageRegion, List<Cell> cells, HashSet<long> adj)
    {
        if (cells.Count == 0)
            return;
        var sorted = new List<float>(cells.Count);
        foreach (Cell c in cells)
            sorted.Add(c.relief);
        sorted.Sort();
        float farmCut = sorted[Mathf.Min(sorted.Count - 1,
            (int)(sorted.Count * FarmQuantile))];
        float pastureCut = sorted[Mathf.Min(sorted.Count - 1,
            (int)(sorted.Count * PastureQuantile))];
        foreach (Cell c in cells)
        {
            c.farmCut = farmCut;
            c.pastureCut = pastureCut;
        }

        int farm = FarmPlots, pasture = PasturePlots;
        var cluster = new List<Cell>();
        bool Touches(Cell c)
        {
            if (Adjacent(adj, c.region, castleRegion)
                || Adjacent(adj, c.region, villageRegion))
                return true;
            foreach (Cell k in cluster)
                if (Adjacent(adj, c.region, k.region))
                    return true;
            return false;
        }
        Cell Next()
        {
            Cell best = null;
            float bestScore = 0f;
            foreach (Cell c in cells)
            {
                if (c.settled != null || !Touches(c))
                    continue;
                float score = c.relief
                    + DistanceWeight * (c.mid - site.pos).magnitude;
                if (best == null || score < bestScore)
                {
                    best = c;
                    bestScore = score;
                }
            }
            return best;
        }
        for (int m = 0; m < farm + pasture; m++)
        {
            Cell next = Next();
            if (next == null)
                break;
            next.settled = m < farm ? LandType.Farm : LandType.Pasture;
            cluster.Add(next);
        }
    }

    static float[] Reversed(float[] p)
    {
        var r = new float[p.Length];
        for (int i = 0; i + 1 < p.Length; i += 2)
        {
            r[p.Length - 2 - i] = p[i];
            r[p.Length - 1 - i] = p[i + 1];
        }
        return r;
    }

    class Cell
    {
        public int gx, gz, territory, region, sample;
        public Vector2 mid;
        public List<Vector3> outline;
        public float relief, area, farmCut, pastureCut;
        public bool forest;
        public LandType? settled;
    }

    // Region adjacency as packed label pairs — 4-neighbor only: a
    // diagonal touch is a point, not a shared boundary.
    static void LinkAdj(HashSet<long> adj, int a, int b)
    {
        if (a < 0 || b < 0 || a == b)
            return;
        adj.Add(a < b ? ((long)a << 32) | (uint)b
            : ((long)b << 32) | (uint)a);
    }

    static bool Adjacent(HashSet<long> adj, int a, int b)
    {
        if (a < 0 || b < 0 || a == b)
            return false;
        return adj.Contains(a < b ? ((long)a << 32) | (uint)b
            : ((long)b << 32) | (uint)a);
    }

    // Deterministic per-lattice-point noise — UnityEngine.Random would
    // tie the survey to whatever else rolled dice that frame.
    static float Hash01(int i, int j, int salt)
    {
        uint h = (uint)(i * 73856093 ^ j * 19349663 ^ salt * 83492791);
        h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
        return (h & 0xffffff) / (float)0x1000000;
    }

    // Binary min-heap for the flood — closed-form arrays, lazy
    // deletion (stale pops are skipped by the dist check).
    class MinHeap
    {
        float[] k = new float[4096];
        int[] v = new int[4096];
        public int Count;

        public void Push(float key, int val)
        {
            if (Count == k.Length)
            {
                System.Array.Resize(ref k, Count * 2);
                System.Array.Resize(ref v, Count * 2);
            }
            int i = Count++;
            while (i > 0)
            {
                int p = (i - 1) >> 1;
                if (k[p] <= key)
                    break;
                k[i] = k[p]; v[i] = v[p];
                i = p;
            }
            k[i] = key; v[i] = val;
        }

        public (float, int) Pop()
        {
            var top = (k[0], v[0]);
            Count--;
            float lk = k[Count];
            int lv = v[Count], i = 0;
            while (true)
            {
                int c = i * 2 + 1;
                if (c >= Count)
                    break;
                if (c + 1 < Count && k[c + 1] < k[c])
                    c++;
                if (k[c] >= lk)
                    break;
                k[i] = k[c]; v[i] = v[c];
                i = c;
            }
            if (Count > 0)
            {
                k[i] = lk; v[i] = lv;
            }
            return top;
        }
    }

    // The frontier rule: bandit land is cleared outward from your own
    // borders — adjacency is a survey-grid fact (one step apart), not
    // geometry. Buying from a neighbor is negotiation, not conquest,
    // and doesn't come through here (Docs/Territories.md).
    public static bool TouchesPlayerLand(LandPlot plot)
    {
        foreach (LandPlot p in LandPlot.All)
        {
            if (p == null || p.owner != LandOwner.Player)
                continue;
            if (Mathf.Abs(p.gx - plot.gx) + Mathf.Abs(p.gz - plot.gz) == 1)
                return true;
        }
        return false;
    }
}
