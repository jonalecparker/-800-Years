using System.Collections.Generic;
using UnityEngine;

// The survey: carves the countryside into irregular parcels ONCE, from
// the terrain as it stands at generation time, and never again — the
// parcels are stored facts from then on (CastleSave v7 carries them;
// a loaded save restores its plots and this never runs). Irregular by
// jittered grid: each cell of a coarse grid gets its corners pushed
// around deterministically, so no two parcels match and the patchwork
// reads as fields, not as a board.
//
// The countryside belongs to LORDSHIPS (Docs/Territories.md): every
// point maps to its nearest CastleSite, parcels exist only within
// SurveyReach of their own castle (worked land — the deep country
// between lordships is claimed, unworked and lawless), classification
// is relative per territory, and each castle grows ONE contiguous
// settled demesne — town by the gate, fields around it. The old
// center-distance ownership rings and MaxSurveyRadius are deleted.
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
    static int VillagePlots => LandSurvey.Instance != null
        ? LandSurvey.Instance.villagePlots : 2;
    static int FarmPlots => LandSurvey.Instance != null
        ? LandSurvey.Instance.farmPlots : 5;
    static int PasturePlots => LandSurvey.Instance != null
        ? LandSurvey.Instance.pasturePlots : 3;
    // Corners wander up to this fraction of a cell from their grid seat.
    const float Jitter = 0.35f;
    // Terrain border left unparceled — nobody farms the world's edge.
    const float Margin = 30f;
    // More plots than this is GameObject soup — refuse and say so
    // rather than freeze the editor for minutes.
    const int MaxPlots = 25000;
    static bool warnedTooMany;

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

        Rect bounds = Ground.Bounds();
        float cell = CellSize;
        float reach = SurveyReach;
        int nx = Mathf.FloorToInt((bounds.width - Margin * 2f) / cell);
        int nz = Mathf.FloorToInt((bounds.height - Margin * 2f) / cell);

        // Pre-count what would materialize — a distance check per cell,
        // no height sampling — and refuse GameObject soup with advice
        // instead of a minutes-long freeze.
        int estimate = 0;
        for (int i = 0; i < nx && estimate <= MaxPlots; i++)
            for (int j = 0; j < nz; j++)
            {
                var mid = new Vector2(
                    bounds.xMin + Margin + (i + 0.5f) * cell,
                    bounds.yMin + Margin + (j + 0.5f) * cell);
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
        // (CastleGrounds). Each becomes a parcel in its own right;
        // lattice cells whose centers fall inside stand aside, nearby
        // corners snap to the boundary like they snap to a road, and
        // the castle routes (LandFeatures) already arrive trimmed at
        // it — the trim point is the gate the village grows from.
        var grounds = new List<List<Vector3>>(sites.Count);
        foreach (Site s in sites)
            grounds.Add(CastleGrounds.Outline(s.pos));

        // Jittered corner lattice, one more than the cell count each
        // way — one GLOBAL grid, so gx/gz adjacency never lies, however
        // many lordships materialize cells on it. Corners near a road
        // or waterway then SNAP onto it (the user's call, 2026-08-12):
        // a field ends at the lane and the brook, not across them.
        // Both displacements are bounded under half a cell, so corners
        // can never swap order and a quad can never self-cross.
        var corners = new Vector3[nx + 1, nz + 1];
        for (int i = 0; i <= nx; i++)
            for (int j = 0; j <= nz; j++)
            {
                float x = bounds.xMin + Margin + i * cell
                    + (Hash01(i, j, 1) - 0.5f) * 2f * Jitter * cell;
                float z = bounds.yMin + Margin + j * cell
                    + (Hash01(i, j, 2) - 0.5f) * 2f * Jitter * cell;
                corners[i, j] = new Vector3(x, 0f, z);
            }
        var groundLines = new List<float[]>(grounds.Count);
        foreach (List<Vector3> g in grounds)
        {
            var flat = new float[(g.Count + 1) * 2];
            for (int k = 0; k < g.Count; k++)
            {
                flat[k * 2] = g[k].x;
                flat[k * 2 + 1] = g[k].z;
            }
            flat[g.Count * 2] = g[0].x;
            flat[g.Count * 2 + 1] = g[0].z;
            groundLines.Add(flat);
        }
        SnapCornersToFeatures(corners, nx, nz, cell,
            bounds.xMin + Margin, bounds.yMin + Margin, groundLines);

        // First pass: geometry, relief and territory for every cell
        // near enough to a castle to be worked land; classification
        // needs each territory's whole distribution before any cell
        // can be judged.
        var cells = new List<Cell>();
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < nz; j++)
            {
                var c = new Cell
                {
                    gx = i,
                    gz = j,
                    outline = new List<Vector3>
                    {
                        corners[i, j], corners[i + 1, j],
                        corners[i + 1, j + 1], corners[i, j + 1],
                    },
                };
                Vector3 mid = (c.outline[0] + c.outline[1] + c.outline[2]
                    + c.outline[3]) * 0.25f;
                c.mid = new Vector2(mid.x, mid.z);
                c.territory = TerritoryOf(c.mid, sites);
                if (reach > 0f
                    && (sites[c.territory].pos - c.mid).magnitude > reach)
                    continue;
                // The castle grounds polygon IS the parcel there — a
                // lattice cell centered under it stands aside. (A cell
                // centered just outside may still overlap the boundary a
                // little; its near corners snapped onto the outline, and
                // the sliver is accepted at survey scale.)
                bool underCastle = false;
                foreach (List<Vector3> g in grounds)
                    if (SlabTile.Contains(g,
                        new Vector3(c.mid.x, 0f, c.mid.y)))
                    {
                        underCastle = true;
                        break;
                    }
                if (underCastle)
                    continue;
                float lo = float.MaxValue, hi = float.MinValue;
                void Sample(Vector3 p)
                {
                    float y = Ground.HeightAt(p);
                    lo = Mathf.Min(lo, y);
                    hi = Mathf.Max(hi, y);
                }
                foreach (Vector3 v in c.outline)
                    Sample(v);
                Sample(mid);
                c.relief = hi - lo;
                c.forest = Mathf.PerlinNoise((mid.x + 431f) * ForestScale,
                    (mid.z + 977f) * ForestScale) > ForestThreshold;
                cells.Add(c);
            }

        // The village lies ON the road before the castle: each trimmed
        // route whose end stops within GateReach of a site is that
        // castle's approach, oriented to walk OUT from the gate.
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

        // Each lordship classifies against its own country and grows
        // its own settled demesne.
        float originX = bounds.xMin + Margin;
        float originZ = bounds.yMin + Margin;
        for (int t = 0; t < sites.Count; t++)
            SettleTerritory(sites[t],
                cells.FindAll(c => c.territory == t),
                approaches[t], cell, originX, originZ);

        // The castle parcels themselves — the terrain-fitted polygons.
        // Grid coords are the seat cell under the site, so the frontier
        // rule's one-step adjacency keeps working around the castle.
        for (int t = 0; t < sites.Count; t++)
            LandPlot.Create(grounds[t], LandType.Castle,
                sites[t].home ? LandOwner.Player : LandOwner.Neighbor,
                Mathf.FloorToInt((sites[t].pos.x - originX) / cell),
                Mathf.FloorToInt((sites[t].pos.y - originZ) / cell), t);

        foreach (Cell c in cells)
        {
            LandType type = c.settled
                ?? (c.forest ? LandType.Timber
                : c.relief <= c.farmCut ? LandType.Farm
                : c.relief <= c.pastureCut ? LandType.Pasture
                : LandType.Timber);
            // Your own territory: the settled demesne is yours, the
            // rest of the lordship is yours on parchment and lawless in
            // fact — bandit-held until cleared plot by plot. A
            // neighbor's territory is wholly theirs; their estates are
            // their problem.
            LandOwner owner = !sites[c.territory].home ? LandOwner.Neighbor
                : c.settled != null ? LandOwner.Player : LandOwner.Bandits;
            LandPlot.Create(c.outline, type, owner, c.gx, c.gz, c.territory);
        }
        int trees = TimberWoods.Plant();
        BuildLog.Add($"The estate surveyed — {cells.Count + sites.Count} parcels across"
            + $" {sites.Count} lordship{(sites.Count == 1 ? "" : "s")}"
            + (trees > 0 ? $"; {trees} oaks stand in the timber." : "."));
    }

    // Relief quantiles set this territory's farm and pasture cuts. The
    // castle parcel is the terrain-fitted polygon (created by the
    // caller); the VILLAGE lies on the road before the gate — the
    // cells each approach passes through, claimed walking outward,
    // round-robin so two approaches share the town between them — and
    // fields and pasture grow by grid adjacency around the settled
    // core, flattest first, one contiguous demesne. A territory no
    // road reaches (Spiš) falls back to flatness for the village too.
    static void SettleTerritory(Site site, List<Cell> cells,
        List<float[]> approaches, float cell, float originX, float originZ)
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

        // The castle's seat cell, for adjacency: the demesne stays
        // contiguous with the castle even though the castle parcel
        // itself is a polygon, not a lattice cell.
        int sgx = Mathf.FloorToInt((site.pos.x - originX) / cell);
        int sgz = Mathf.FloorToInt((site.pos.y - originZ) / cell);

        var byCoord = new Dictionary<(int, int), Cell>();
        foreach (Cell c in cells)
            byCoord[(c.gx, c.gz)] = c;

        int village = VillagePlots, farm = FarmPlots,
            pasture = PasturePlots;
        var cluster = new List<Cell>();

        // Village along the roads, nearest the gate first.
        var walks = new List<List<Cell>>(approaches.Count);
        foreach (float[] r in approaches)
            walks.Add(CellsAlong(r, byCoord, cell, originX, originZ));
        int claimed = 0;
        for (int step = 0; claimed < village; step++)
        {
            bool any = false;
            foreach (List<Cell> w in walks)
            {
                if (step >= w.Count)
                    continue;
                any = true;
                Cell c = w[step];
                if (c.settled != null || claimed >= village)
                    continue;
                c.settled = LandType.Living;
                cluster.Add(c);
                claimed++;
            }
            if (!any)
                break;
        }

        bool Touches(Cell c)
        {
            if (Mathf.Abs(c.gx - sgx) + Mathf.Abs(c.gz - sgz) <= 1)
                return true;
            foreach (Cell k in cluster)
                if (Mathf.Abs(c.gx - k.gx) + Mathf.Abs(c.gz - k.gz) == 1)
                    return true;
            return false;
        }
        Cell Next()
        {
            Cell best = null;
            foreach (Cell c in cells)
                if (c.settled == null && Touches(c)
                    && (best == null || c.relief < best.relief))
                    best = c;
            return best;
        }

        // No road reached this castle — the village grows by flatness
        // beside the seat instead.
        while (claimed < village)
        {
            Cell next = Next();
            if (next == null)
                break;
            next.settled = LandType.Living;
            cluster.Add(next);
            claimed++;
        }
        for (int n = 0; n < farm + pasture; n++)
        {
            Cell next = Next();
            if (next == null)
                break;
            next.settled = n < farm ? LandType.Farm : LandType.Pasture;
            cluster.Add(next);
        }
    }

    // The distinct lattice cells a road passes through, in walk order.
    // Cells are looked up by their SEAT (unjittered) coords — a sample
    // near a snapped corner can map to the neighbor quad, accepted at
    // survey scale.
    static List<Cell> CellsAlong(float[] road,
        Dictionary<(int, int), Cell> byCoord, float cell,
        float originX, float originZ)
    {
        var walk = new List<Cell>();
        var seen = new HashSet<(int, int)>();
        float step = cell * 0.2f;
        for (int i = 0; i + 3 < road.Length; i += 2)
        {
            var a = new Vector2(road[i], road[i + 1]);
            var b = new Vector2(road[i + 2], road[i + 3]);
            int n = Mathf.Max(1, Mathf.CeilToInt((b - a).magnitude / step));
            for (int k = 0; k <= n; k++)
            {
                Vector2 p = Vector2.Lerp(a, b, (float)k / n);
                var key = (Mathf.FloorToInt((p.x - originX) / cell),
                    Mathf.FloorToInt((p.y - originZ) / cell));
                if (!seen.Add(key))
                    continue;
                if (byCoord.TryGetValue(key, out Cell c))
                    walk.Add(c);
            }
        }
        return walk;
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

    // A corner within SnapFraction of a cell of a feature line moves to
    // the nearest point ON the line, replacing its jitter — so parcel
    // edges between two snapped corners lie along the road or river
    // (straight chords of it; a tight meander still cuts the corner —
    // accepted at survey scale). Nearest feature wins. The segment
    // spatial hash keeps this O(corners), not O(corners × segments).
    // A snap is accepted only within SeatLimit of the corner's grid
    // SEAT: jitter (0.35) plus snap reach could otherwise push total
    // displacement past half a cell, and two corners walking more than
    // half a cell toward each other can swap order and cross the quad.
    const float SnapFraction = 0.38f;
    const float SeatLimit = 0.45f;

    static void SnapCornersToFeatures(Vector3[,] corners, int nx, int nz,
        float cell, float originX, float originZ, List<float[]> extraLines)
    {
        var lines = new List<float[]>(LandFeatures.RoadLines());
        foreach ((float[] p, float w) in LandFeatures.WaterLines())
            lines.Add(p);
        if (extraLines != null)
            lines.AddRange(extraLines);
        if (lines.Count == 0)
            return;

        // Segments binned by cell-sized tiles over their bounding box.
        float bin = cell;
        var segs = new Dictionary<(int, int), List<(Vector2 a, Vector2 b)>>();
        foreach (float[] p in lines)
            for (int i = 0; i + 3 < p.Length; i += 2)
            {
                var a = new Vector2(p[i], p[i + 1]);
                var b = new Vector2(p[i + 2], p[i + 3]);
                int x0 = Mathf.FloorToInt(Mathf.Min(a.x, b.x) / bin);
                int x1 = Mathf.FloorToInt(Mathf.Max(a.x, b.x) / bin);
                int z0 = Mathf.FloorToInt(Mathf.Min(a.y, b.y) / bin);
                int z1 = Mathf.FloorToInt(Mathf.Max(a.y, b.y) / bin);
                // A pathological segment (bad data, a way running far
                // off the map) would explode the bbox-product insert —
                // skip it rather than hang the survey.
                if (x1 - x0 > 50 || z1 - z0 > 50)
                    continue;
                for (int gx = x0; gx <= x1; gx++)
                    for (int gz = z0; gz <= z1; gz++)
                    {
                        if (!segs.TryGetValue((gx, gz), out var list))
                            segs[(gx, gz)] = list
                                = new List<(Vector2, Vector2)>();
                        list.Add((a, b));
                    }
            }

        float radius = SnapFraction * cell;
        for (int i = 0; i <= nx; i++)
            for (int j = 0; j <= nz; j++)
            {
                var c = new Vector2(corners[i, j].x, corners[i, j].z);
                var seat = new Vector2(originX + i * cell,
                    originZ + j * cell);
                int cx = Mathf.FloorToInt(c.x / bin);
                int cz = Mathf.FloorToInt(c.y / bin);
                float bestSq = radius * radius;
                float seatSq = SeatLimit * cell * SeatLimit * cell;
                Vector2 best = c;
                bool snapped = false;
                for (int dx = -1; dx <= 1; dx++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (!segs.TryGetValue((cx + dx, cz + dz), out var list))
                            continue;
                        foreach ((Vector2 a, Vector2 b) in list)
                        {
                            Vector2 ab = b - a;
                            float t = ab.sqrMagnitude < 1e-6f ? 0f
                                : Mathf.Clamp01(Vector2.Dot(c - a, ab)
                                    / ab.sqrMagnitude);
                            Vector2 q = a + ab * t;
                            float sq = (q - c).sqrMagnitude;
                            if (sq < bestSq
                                && (q - seat).sqrMagnitude <= seatSq)
                            {
                                bestSq = sq;
                                best = q;
                                snapped = true;
                            }
                        }
                    }
                if (snapped)
                    corners[i, j] = new Vector3(best.x, 0f, best.y);
            }
    }

    class Cell
    {
        public int gx, gz, territory;
        public Vector2 mid;
        public List<Vector3> outline;
        public float relief, farmCut, pastureCut;
        public bool forest;
        public LandType? settled;
    }

    // Deterministic per-lattice-point noise — UnityEngine.Random would
    // tie the survey to whatever else rolled dice that frame.
    static float Hash01(int i, int j, int salt)
    {
        uint h = (uint)(i * 73856093 ^ j * 19349663 ^ salt * 83492791);
        h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
        return (h & 0xffffff) / (float)0x1000000;
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
