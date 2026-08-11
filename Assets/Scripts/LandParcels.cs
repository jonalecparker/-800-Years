using System.Collections.Generic;
using UnityEngine;

// The survey: carves the countryside into irregular parcels ONCE, from
// the terrain as it stands at generation time, and never again — the
// parcels are stored facts from then on (CastleSave v6 carries them;
// a loaded save restores its plots and this never runs). Irregular by
// jittered grid: each cell of a coarse grid gets its corners pushed
// around deterministically, so no two parcels match and the patchwork
// reads as fields, not as a board. Classification is slope plus a
// forest mask; ownership is distance from the map's heart — your
// demesne at the center, a bandit ring around it, neighbors beyond
// (the design doc's land ladder).
public static class LandParcels
{
    public const float CellSize = 60f;
    // Corners wander up to this fraction of a cell from their grid seat.
    const float Jitter = 0.35f;
    // Terrain border left unparceled — nobody farms the world's edge.
    const float Margin = 30f;
    // TEMPORARY until castle territories exist: parcels only within this
    // radius of the map center. On the 15km Marches terrain an unbounded
    // survey is 62,000 parcels — GameObject soup and a bloated save for
    // land no system can reason about yet. Territories will replace the
    // radius with per-castle reach.
    const float MaxSurveyRadius = 1500f;

    // Classification is RELATIVE to the terrain: the flattest ~35% of
    // parcels are arable, the middle band grazes, the steepest third is
    // timber — absolute slope thresholds on a hill map once yielded 219
    // timber, 1 farm and no village at all. A Perlin forest mask still
    // plants woods wherever it falls.
    const float FarmQuantile = 0.35f;
    const float PastureQuantile = 0.70f;
    const float ForestThreshold = 0.62f;
    const float ForestScale = 0.011f;
    // The demesne is SETTLED land: people farmed the heart for
    // generations before any castle stood on it, so the flattest of
    // the starting parcels are the village, fields and pasture whatever
    // the wild classification says (a hilltop start once dealt the lord
    // ten woods and no fields). The steepest few stay wild.
    const int VillagePlots = 2;
    const int FarmPlots = 5;
    const int PasturePlots = 3;

    // Ownership rings, from the map center.
    const float DemesneRadius = 110f;
    const float BanditRadius = 240f;

    public static void EnsureGenerated()
    {
        if (LandPlot.All.Count > 0)
            return;
        Terrain terrain = Terrain.activeTerrain;
        if (terrain == null)
            return;

        Vector3 origin = terrain.transform.position;
        Vector3 size = terrain.terrainData.size;
        Vector2 center = new Vector2(origin.x + size.x * 0.5f,
            origin.z + size.z * 0.5f);
        int nx = Mathf.FloorToInt((size.x - Margin * 2f) / CellSize);
        int nz = Mathf.FloorToInt((size.z - Margin * 2f) / CellSize);

        // Jittered corner lattice, one more than the cell count each way.
        var corners = new Vector3[nx + 1, nz + 1];
        for (int i = 0; i <= nx; i++)
            for (int j = 0; j <= nz; j++)
            {
                float x = origin.x + Margin + i * CellSize
                    + (Hash01(i, j, 1) - 0.5f) * 2f * Jitter * CellSize;
                float z = origin.z + Margin + j * CellSize
                    + (Hash01(i, j, 2) - 0.5f) * 2f * Jitter * CellSize;
                corners[i, j] = new Vector3(x, 0f, z);
            }

        // First pass: geometry and relief for every cell; classification
        // needs the whole distribution before any cell can be judged.
        var cells = new List<Cell>(nx * nz);
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
                float lo = float.MaxValue, hi = float.MinValue;
                void Sample(Vector3 p)
                {
                    float y = terrain.SampleHeight(p) + origin.y;
                    lo = Mathf.Min(lo, y);
                    hi = Mathf.Max(hi, y);
                }
                foreach (Vector3 v in c.outline)
                    Sample(v);
                Sample(mid);
                c.relief = hi - lo;
                c.fromCenter = Vector2.Distance(center,
                    new Vector2(mid.x, mid.z));
                if (c.fromCenter > MaxSurveyRadius)
                    continue;
                c.forest = Mathf.PerlinNoise((mid.x + 431f) * ForestScale,
                    (mid.z + 977f) * ForestScale) > ForestThreshold;
                cells.Add(c);
            }

        // Relief quantiles set the farm and pasture cuts for THIS map.
        var sorted = new List<float>(cells.Count);
        foreach (Cell c in cells)
            sorted.Add(c.relief);
        sorted.Sort();
        float farmCut = sorted[(int)(sorted.Count * FarmQuantile)];
        float pastureCut = sorted[(int)(sorted.Count * PastureQuantile)];

        // Settle the demesne: flattest parcels first — village, then
        // fields, then pasture; the steepest stay wild.
        var demesne = cells.FindAll(c => c.fromCenter < DemesneRadius);
        demesne.Sort((a, b) => a.relief.CompareTo(b.relief));
        for (int v = 0; v < demesne.Count; v++)
            demesne[v].settled =
                v < VillagePlots ? LandType.Living
                : v < VillagePlots + FarmPlots ? LandType.Farm
                : v < VillagePlots + FarmPlots + PasturePlots
                    ? LandType.Pasture : (LandType?)null;

        foreach (Cell c in cells)
        {
            LandType type = c.settled
                ?? (c.forest ? LandType.Timber
                : c.relief <= farmCut ? LandType.Farm
                : c.relief <= pastureCut ? LandType.Pasture
                : LandType.Timber);
            LandOwner owner = c.fromCenter < DemesneRadius
                ? LandOwner.Player
                : c.fromCenter < BanditRadius
                    ? LandOwner.Bandits : LandOwner.Neighbor;
            LandPlot.Create(c.outline, type, owner, c.gx, c.gz);
        }
        BuildLog.Add($"The estate surveyed — {cells.Count} parcels on the books.");
    }

    class Cell
    {
        public int gx, gz;
        public List<Vector3> outline;
        public float relief, fromCenter;
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

    // The frontier rule: land is taken from your own borders outward.
    // Adjacency is a survey-grid fact (one step apart), not geometry.
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
