using System.Collections.Generic;
using UnityEngine;

// Plants terrain trees in Timber parcels (the user's call, 2026-08-12:
// "place trees in the timber sections" — the survey map and the world
// must agree about where the woods are). Runs at survey time, from
// LandParcels.EnsureGenerated and from CastleSave's plot restore, on
// the tiles' SESSION CLONES (TerrainPads.SessionData — never the
// authored asset). Deterministic: the same parcels plant the same
// trees, so a replant after Resurvey or a snapshot undo converges.
//
// Skips: parcels that are already woodland (the OSM woods the Dress
// bake planted — density measured per parcel, not per point, so a real
// wood isn't doubled), points near the castle routes (the same
// CastleRoads set the bake painted, so roads and tree gaps agree) and
// near waterways. A scene with no dressed tiles (Spiš — no tree
// prototypes) plants nothing.
public static class TimberWoods
{
    // Existing trees denser than one per this many m² mean the parcel
    // is already a wood — leave it to the real woods.
    const float WoodedThresholdM2 = 300f;
    // Keep-out spatial hash cell, matching the dressing's road cells.
    const float Cell = 6f;
    // Runaway guard: more than this many planted trees thins by hash —
    // full-map surveys at tight spacing would otherwise double the
    // dressing's quarter-million instances.
    const int MaxPlanted = 250000;

    // First planting per tile records how many instances the dressing
    // gave it; replants truncate back to that before appending, so a
    // Resurvey never stacks trees on trees. Session-scoped like the
    // clones themselves.
    static readonly Dictionary<Terrain, int> baseCounts
        = new Dictionary<Terrain, int>();

    public static int Plant()
    {
        float spacing = LandSurvey.Instance != null
            ? LandSurvey.Instance.timberTreeSpacing : 12f;
        if (spacing < 2f || !Ground.Any)
            return 0;
        Terrain[] tiles = Ground.Tiles();
        if (tiles.Length == 0)
            return 0;
        // The oak is whatever tree prototype the Dress bake set up; an
        // undressed scene has none and plants nothing.
        int proto = OakPrototype(tiles[0].terrainData);
        if (proto < 0)
            return 0;

        var timber = new List<LandPlot>();
        foreach (LandPlot p in LandPlot.All)
            if (p != null && p.type == LandType.Timber)
                timber.Add(p);
        if (timber.Count == 0)
            return 0;

        HashSet<(int, int)> keepOut = KeepOutCells();
        var existing = ExistingTreeBins(tiles);

        var buckets = new Dictionary<Terrain, List<TreeInstance>>();
        int planted = 0;
        foreach (LandPlot plot in timber)
        {
            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            foreach (Vector3 v in plot.verts)
            {
                minX = Mathf.Min(minX, v.x); maxX = Mathf.Max(maxX, v.x);
                minZ = Mathf.Min(minZ, v.z); maxZ = Mathf.Max(maxZ, v.z);
            }
            if (AlreadyWooded(plot, existing, minX, maxX, minZ, maxZ))
                continue;
            for (float x = minX; x <= maxX; x += spacing)
                for (float z = minZ; z <= maxZ; z += spacing)
                {
                    float jx = x + (Hash01(x, z, 61) - 0.5f) * spacing;
                    float jz = z + (Hash01(x, z, 67) - 0.5f) * spacing;
                    var p = new Vector3(jx, 0f, jz);
                    if (!SlabTile.Contains(plot.verts, p))
                        continue;
                    if (NearKeepOut(keepOut, jx, jz))
                        continue;
                    Terrain t = Ground.TileAt(jx, jz);
                    if (t == null)
                        continue;
                    if (!buckets.TryGetValue(t, out List<TreeInstance> list))
                        buckets[t] = list = new List<TreeInstance>();
                    Vector3 origin = t.transform.position;
                    Vector3 size = t.terrainData.size;
                    float h = Hash01(jx, jz, 71);
                    list.Add(new TreeInstance
                    {
                        position = new Vector3((jx - origin.x) / size.x, 0f,
                            (jz - origin.z) / size.z),
                        widthScale = 0.7f + h * 0.5f,
                        heightScale = 0.7f + h * 0.5f,
                        rotation = Hash01(jx, jz, 73) * Mathf.PI * 2f,
                        color = Color.white,
                        lightmapColor = Color.white,
                        prototypeIndex = proto,
                    });
                    planted++;
                }
        }

        if (planted > MaxPlanted)
        {
            float keep = (float)MaxPlanted / planted;
            planted = 0;
            foreach (Terrain t in new List<Terrain>(buckets.Keys))
            {
                var thinned = new List<TreeInstance>();
                List<TreeInstance> b = buckets[t];
                for (int i = 0; i < b.Count; i++)
                    if (Hash01(i, t.transform.position.x, 79) < keep)
                        thinned.Add(b[i]);
                buckets[t] = thinned;
                planted += thinned.Count;
            }
            Debug.LogWarning($"TimberWoods: thinned to {planted} (cap"
                + $" {MaxPlanted}) — widen timberTreeSpacing.");
        }

        // Write through the session-clone door; truncate each tile back
        // to its dressing-time count first so replants never stack. A
        // tile with nothing to add and nothing planted is left alone —
        // no clone forced for it.
        foreach (Terrain t in tiles)
        {
            bool has = buckets.TryGetValue(t, out List<TreeInstance> add);
            int current = t.terrainData.treeInstanceCount;
            if (!baseCounts.TryGetValue(t, out int baseCount))
                baseCounts[t] = baseCount = current;
            if (!has && current == baseCount)
                continue;
            TerrainData data = TerrainPads.SessionData(t);
            var all = new List<TreeInstance>(data.treeInstances);
            all.RemoveRange(baseCount, all.Count - baseCount);
            if (has)
                all.AddRange(add);
            data.SetTreeInstances(all.ToArray(), true);
        }
        return planted;
    }

    static int OakPrototype(TerrainData data)
    {
        TreePrototype[] protos = data.treePrototypes;
        for (int i = 0; i < protos.Length; i++)
            if (protos[i].prefab != null
                && protos[i].prefab.name.Contains("Oak"))
                return i;
        return protos.Length > 0 ? 0 : -1;
    }

    // The same castle routes the Dress bake painted, plus waterways —
    // through LandFeatures, the one runtime door to the feature lines.
    static HashSet<(int, int)> KeepOutCells()
    {
        var cells = new HashSet<(int, int)>();
        foreach (float[] road in LandFeatures.RoadLines())
            MarkLine(cells, road, 2);
        foreach ((float[] p, float w) in LandFeatures.WaterLines())
            MarkLine(cells, p, Mathf.CeilToInt((w * 0.5f + 8f) / Cell));
        return cells;
    }

    static void MarkLine(HashSet<(int, int)> cells, float[] p, int radius)
    {
        for (int i = 0; i + 1 < p.Length; i += 2)
        {
            int cx = Mathf.FloorToInt(p[i] / Cell);
            int cz = Mathf.FloorToInt(p[i + 1] / Cell);
            for (int dx = -radius; dx <= radius; dx++)
                for (int dz = -radius; dz <= radius; dz++)
                    cells.Add((cx + dx, cz + dz));
        }
    }

    static bool NearKeepOut(HashSet<(int, int)> cells, float x, float z)
    {
        int cx = Mathf.FloorToInt(x / Cell);
        int cz = Mathf.FloorToInt(z / Cell);
        for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++)
                if (cells.Contains((cx + dx, cz + dz)))
                    return true;
        return false;
    }

    // Existing DRESSING trees (each tile's first baseCount instances)
    // binned into coarse world cells, for the already-wooded test.
    static Dictionary<(int, int), int> ExistingTreeBins(Terrain[] tiles)
    {
        var bins = new Dictionary<(int, int), int>();
        foreach (Terrain t in tiles)
        {
            TreeInstance[] all = t.terrainData.treeInstances;
            int count = baseCounts.TryGetValue(t, out int baseCount)
                ? Mathf.Min(baseCount, all.Length) : all.Length;
            Vector3 origin = t.transform.position;
            Vector3 size = t.terrainData.size;
            for (int i = 0; i < count; i++)
            {
                float x = origin.x + all[i].position.x * size.x;
                float z = origin.z + all[i].position.z * size.z;
                var key = (Mathf.FloorToInt(x / 30f), Mathf.FloorToInt(z / 30f));
                bins.TryGetValue(key, out int n);
                bins[key] = n + 1;
            }
        }
        return bins;
    }

    static bool AlreadyWooded(LandPlot plot,
        Dictionary<(int, int), int> bins,
        float minX, float maxX, float minZ, float maxZ)
    {
        int trees = 0;
        for (int cx = Mathf.FloorToInt(minX / 30f);
            cx <= Mathf.FloorToInt(maxX / 30f); cx++)
            for (int cz = Mathf.FloorToInt(minZ / 30f);
                cz <= Mathf.FloorToInt(maxZ / 30f); cz++)
                if (bins.TryGetValue((cx, cz), out int n))
                    trees += n;
        return trees * WoodedThresholdM2 > plot.AreaM2;
    }

    static float Hash01(float a, float b, int salt)
    {
        uint h = (uint)(Mathf.RoundToInt(a * 8f) * 73856093
            ^ Mathf.RoundToInt(b * 8f) * 19349663 ^ salt * 83492791);
        h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
        return (h & 0xffffff) / (float)0x1000000;
    }
}
