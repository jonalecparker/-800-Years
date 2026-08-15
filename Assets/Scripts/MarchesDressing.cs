using System;
using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

// Dresses the Marches terrain with real land cover baked from OpenStreetMap
// (Docs/MarchesRealism.md and Docs/Rivers.md are the briefs): roads as
// worn-track splat paint (the draped road ribbons are gone — the user's
// call: "just the terrain"), water ribbons over the beds MarchesBaker
// carved (the cheap tier of the two-tier water story; RiverWaterLOD
// swaps the near ones for HDRP water), real wood polygons filled with
// terrain trees, bushes as undergrowth and hedging the lanes.
// Everything here is scene DRESSING derived from the features asset — no
// stored gameplay facts, nothing joins CastleSave, and the dressing root
// stays out of splineParent (the cutaway has no business slicing a road,
// the LandPlot precedent). ContextMenu bake like TerrainGenerator: the
// baker bakes tiles (heights + carve), then Dress lays the cover; Clear
// Dressing removes what Dress made. The carve itself lives in
// MarchesBaker now — refs need PRE-carve ground, and only the bake has
// it; ribbons read the water levels from the SAVED refs
// (MarchesRiverRefs.json), never from carved ground. Ribbons carry no
// colliders (the walker must never trip on a road); oak trunks DO collide.
public class MarchesDressing : MonoBehaviour
{
    // World-XZ polylines/polygons preprocessed offline from Overpass with the
    // verified Marches georeferencing — Unity never talks to the network.
    public TextAsset features;

    [Header("Ribbons")]
    // 1220 filter: every road class renders as the same dirt; class sets
    // width only (0 through-road, 1 lane/track, 2 path). Roads render as
    // splat paint alone now, but the widths still drive the paint falloff,
    // the tree keep-out cells and the hedgerows.
    public float[] roadWidths = { 4f, 3f, 1.8f };

    [Header("Roads of 1220")]
    // Keep only the routes that CONNECT the castles (CastleRoads —
    // pairwise shortest paths over the stitched network). The modern
    // density is the lie, not the geometry; kept routes paint as
    // through-roads (class 0) whatever class their pieces were.
    public bool castleRoadsOnly = true;

    [Header("Road Splat")]
    // The road IS this paint now (the ribbons are gone): a third terrain
    // layer along every corridor, full-strength over the roadbed and
    // fading through the verge. Painted at each TILE'S OWN alphamap
    // resolution (the baker's dial — 512 on a 2km tile is ~3.9m/px);
    // a resolution dial here would fight the baker's.
    // Beyond the road's own half-width: full-strength core, then fade.
    // Tuned against the first bake (core 1.5 / fade 4 read as sandy
    // avenues): the control map's own bilinear blur at 3.68m/px adds
    // several meters of softness, so the stored falloff stays tight.
    public float splatCore = 1f;
    public float splatFade = 2.5f;

    [Header("Rivers")]
    // Size sets the cut (Docs/Rivers.md): depth = clamp(width × perWidth,
    // min…max), full across the channel, smoothstepped to zero over the
    // bank — whose width floors at bankWidthMin because the 7.37m
    // heightmap cells can't hold a sharper shoulder.
    public float carveDepthPerWidth = 0.22f;
    public float carveDepthMin = 0.4f;
    public float carveDepthMax = 1.8f;
    public float bankWidthFactor = 1.5f;
    public float bankWidthMin = 6f;
    // How full the channel runs: water level = channel ref − (1 − fill) ×
    // depth. High enough to survive the terrain's distance LOD (see the
    // freeboard comment in BuildWaterRibbons), low enough to stay inside
    // the notch.
    public float waterFill = 0.7f;
    // Water ribbons chunk by spatial cell (not vertex count) so
    // RiverWaterLOD can swap the near ones for HDRP water per cell.
    public float waterCellSize = 300f;

    [Header("Trees")]
    public float treeSpacing = 9f;
    public float bushSpacing = 14f;
    public float roadsideBushStep = 16f;
    // 450k: the 300k cap was set before the hedgebank pass existed —
    // hedges filled it and the global thinning quietly sparsified the
    // woods to make room. Headroom for both; the dial is perf-yours.
    public int maxTreeInstances = 450000;

    [Header("Hedgebanks & Field Paint")]
    // The LIDAR keeps the field-boundary EARTHWORKS but strips the
    // hedges that explain them — naked banks read as artifacts. A
    // curvature pass finds the crests on the fine tiles and plants
    // bushes along them; the mottle breaks up the billiard-table grass.
    [Tooltip("Crest height over the surrounding ring that reads as a hedgebank (m).")]
    public float hedgeBankSignal = 0.22f;
    [Tooltip("Ring radius the crest is measured against (m).")]
    public float hedgeBankRing = 7f;
    [Tooltip("Approximate spacing of hedge bushes along a bank (m).")]
    public float hedgeBushStep = 4f;
    [Tooltip("Hedgebank bushes cap — they yield to woods, not the reverse.")]
    public int maxHedgeInstances = 120000;
    [Tooltip("Grass/dirt noise amplitude in the field paint (0 = off).")]
    public float splatMottle = 0.35f;

    const string DressFolder = "Assets/Terrain/Dressing";
    const string RootName = "Marches Dressing";
    // Spatial-hash cell for "is a road here" tests during scattering; a tree
    // in the same or neighboring cell as a road sample is skipped, so a
    // track through a wood stays walkable.
    const float RoadCell = 6f;

    [Serializable] class Fea { public int c; public float w; public float[] p; }
    [Serializable] class FeaSet { public Fea[] roads; public Fea[] waters; public Fea[] woods; }

    [ContextMenu("Dress")]
    public void Dress()
    {
        if (features == null)
        {
            Debug.LogError("MarchesDressing: no features asset assigned.");
            return;
        }
        // Splats are about to be repainted from scratch — restoring the
        // slope-only paint first would be half the bake done twice.
        ClearDressing(restoreSplats: false);
        Terrain[] tiles = Ground.Tiles();
        if (tiles.Length == 0)
        {
            Debug.LogError("MarchesDressing: no active terrain to dress.");
            return;
        }
        // Draped water only stays visible if the rendered terrain stays
        // close to the heightmap the drape sampled — the terrain's LOD mesh
        // deviates by pixelError's worth of screen space (a 0.12m lift
        // vanished entirely at pixelError 5, verified with a probe quad).
        foreach (Terrain t in tiles)
            t.heightmapPixelError = 2f;
        FeaSet set = JsonUtility.FromJson<FeaSet>(features.text);
        if (castleRoadsOnly)
            set.roads = FilterToCastleRoutes(set.roads);

        Transform root = new GameObject(RootName).transform;

        var roadCells = new HashSet<(int, int)>();
        foreach (Fea r in set.roads)
            MarkRoadCells(roadCells, r.p, Mathf.CeilToInt(
                roadWidths[Mathf.Clamp(r.c, 0, roadWidths.Length - 1)] * 0.5f / RoadCell) + 1);
        // Waters keep trees out the same way roads do — the Monnow must
        // not run through trunks now that its bed is carved real.
        if (set.waters != null)
            foreach (Fea wf in set.waters)
                MarkRoadCells(roadCells, wf.p, Mathf.CeilToInt(
                    BankEdge(Mathf.Max(1f, wf.w)) / RoadCell) + 1);

        Material water = LoadOrCreateMaterial("WaterMat",
            new Color(0.14f, 0.22f, 0.26f), 0.92f);
        int waterCount = BuildWaterRibbons(root, water, set.waters);

        int trees = ScatterTrees(set, roadCells);
        PaintRoadSplats(tiles, set);

        Debug.Log($"Marches dressed: {waterCount} water ribbons, {trees} trees/bushes across {tiles.Length} terrain(s); roads are splat paint only.");
#if UNITY_EDITOR
        foreach (Terrain t in tiles)
            EditorUtility.SetDirty(t.terrainData);
        AssetDatabase.SaveAssets();
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(gameObject.scene);
#endif
    }

    [ContextMenu("Clear Dressing")]
    public void ClearDressing() => ClearDressing(restoreSplats: true);

    void ClearDressing(bool restoreSplats)
    {
        GameObject root = GameObject.Find(RootName);
        if (root != null)
        {
            if (Application.isPlaying) Destroy(root);
            else DestroyImmediate(root);
        }
        foreach (Terrain t in Ground.Tiles())
        {
            ClearTrees(t.terrainData);
            if (restoreSplats)
                RestoreSlopeSplats(t.terrainData);
        }
    }

    static void ClearTrees(TerrainData data)
    {
        if (data == null)
            return;
        if (data.treeInstanceCount > 0 || data.treePrototypes.Length > 0)
        {
            data.SetTreeInstances(new TreeInstance[0], false);
            data.treePrototypes = new TreePrototype[0];
        }
    }

    // ---- Rivers ------------------------------------------------------------

    float CarveDepth(float w) =>
        Mathf.Clamp(w * carveDepthPerWidth, carveDepthMin, carveDepthMax);
    float BankEdge(float w) =>
        w * 0.5f + Mathf.Max(w * bankWidthFactor, bankWidthMin);

    // The saved carve refs (MarchesBaker wrote them beside the carve
    // itself) — the PRE-carve channel levels the water sits against.
    // Editor-only on purpose: Dress is an editor bake.
    static MarchesBaker.RiverRefs LoadRiverRefs()
    {
#if UNITY_EDITOR
        var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(
            MarchesBaker.RiverRefsPath);
        return asset == null ? null
            : JsonUtility.FromJson<MarchesBaker.RiverRefs>(asset.text);
#else
        return null;
#endif
    }

    // ---- Water ribbons -----------------------------------------------------

    // The cheap tier of the two-tier water (Docs/Rivers.md): draped ribbons
    // sitting FLAT across the width at the water level — carved bed +
    // waterFill × depth, smoothed so the surface doesn't copy every
    // heightmap wobble of the bed — and cut wider than the channel so the
    // edges bury themselves in the rising banks (terrain hides what's
    // underground; no shoreline gap). Chunked by SPATIAL CELL, one GO per
    // cell carrying a RiverChunk with the bake-time water-level range, so
    // RiverWaterLOD can swap the near cells for HDRP water surfaces.
    int BuildWaterRibbons(Transform root, Material mat, Fea[] feas)
    {
        if (feas == null)
            return 0;
        MarchesBaker.RiverRefs refs = LoadRiverRefs();
        if (refs == null || refs.waters == null
            || refs.waters.Length != feas.Length)
        {
            // The refs are the bake's stored fact; deriving levels from
            // CARVED ground here would re-invent the ratchet. Refuse
            // loudly rather than build wrong water.
            Debug.LogError("MarchesDressing: river refs missing or stale"
                + $" ({MarchesBaker.RiverRefsPath}) — run MarchesBaker's"
                + " Bake Tiles first; skipping water ribbons.");
            return 0;
        }
        var group = new GameObject("Waters").transform;
        group.SetParent(root, false);
        var cells = new Dictionary<(int, int), CellBuilder>();
        float cellSize = Mathf.Max(50f, waterCellSize);
        int built = 0;

        for (int fi = 0; fi < feas.Length; fi++)
        {
            Fea f = feas[fi];
            // The SAME resampling the baker used for the refs — the
            // features asset is identical, so the counts align exactly.
            List<Vector3> line = Resample(Unflatten(f.p), MarchesBaker.CarveStep);
            float[] channelRefs = refs.waters[fi]?.refs;
            if (line.Count < 2)
                continue;
            if (channelRefs == null || channelRefs.Length != line.Count)
            {
                Debug.LogError($"MarchesDressing: river refs for waterway {fi}"
                    + $" carry {channelRefs?.Length ?? 0} samples, the line"
                    + $" {line.Count} — stale bake, skipping this waterway.");
                continue;
            }
            // The carve thalweg-snaps its stations off the OSM line onto
            // the real channel; the water must follow the channel
            // AS-BUILT, so the saved station positions override the map.
            float[] snapped = refs.waters[fi].pts;
            if (snapped != null && snapped.Length == line.Count * 2)
                for (int i = 0; i < line.Count; i++)
                    line[i] = new Vector3(snapped[i * 2], 0f, snapped[i * 2 + 1]);
            float w = Mathf.Max(1f, f.w);
            float depth = CarveDepth(w);
            float halfWChannel = w * 0.5f;
            float hw = halfWChannel + (BankEdge(w) - halfWChannel) * 0.6f;
            // Water sits a FREEBOARD below the channel reference, not a
            // fill above the bed: near grade, the ribbon survives the
            // terrain's distance LOD — a sub-cell trench gets simplified
            // away and the risen ground z-killed bed-level water (the
            // user's "ribbon only loads in when close") — and the
            // freeboard still tucks it inside the notch up close.
            var ys = new float[line.Count];
            for (int i = 0; i < line.Count; i++)
                ys[i] = channelRefs[i] - depth * (1f - waterFill);
            for (int pass = 0; pass < 2; pass++)
                for (int i = 1; i < line.Count - 1; i++)
                    ys[i] = (ys[i - 1] + ys[i] + ys[i + 1]) / 3f;
            // Never below the carved bed — a submerged run vanishes
            // underground and shreds the surface into shards.
            for (int i = 0; i < line.Count; i++)
                ys[i] = Mathf.Max(ys[i], Ground.HeightAt(line[i]) + 0.12f);

            (int, int) CellOf(Vector3 p) => (
                Mathf.FloorToInt(p.x / cellSize), Mathf.FloorToInt(p.z / cellSize));
            int runStart = 0;
            (int, int) runCell = CellOf(line[0]);
            for (int i = 1; i < line.Count; i++)
            {
                (int, int) c = CellOf(line[i]);
                bool last = i == line.Count - 1;
                if (c != runCell || last)
                {
                    // The boundary sample belongs to BOTH runs — no gap.
                    EmitRun(cells, runCell, line, ys, runStart,
                        c != runCell && last ? i - 1 : i, hw, w);
                    if (c != runCell && last)
                        EmitRun(cells, c, line, ys, i - 1, i, hw, w);
                    runStart = i;
                    runCell = c;
                }
            }
            built++;
        }

        foreach (KeyValuePair<(int, int), CellBuilder> kv in cells)
            FlushCell(group, mat, kv.Key, kv.Value);
        return built;
    }

    class CellBuilder
    {
        public readonly List<Vector3> verts = new List<Vector3>();
        public readonly List<int> tris = new List<int>();
        public float minY = float.MaxValue, maxY = float.MinValue;
        public float maxW;
    }

    void EmitRun(Dictionary<(int, int), CellBuilder> cells, (int, int) cell,
        List<Vector3> line, float[] ys, int s, int e, float hw, float width)
    {
        if (e - s < 1)
            return;
        if (!cells.TryGetValue(cell, out CellBuilder b))
            cells[cell] = b = new CellBuilder();
        b.maxW = Mathf.Max(b.maxW, width);
        int start = b.verts.Count;
        for (int i = s; i <= e; i++)
        {
            // Tangent from the WHOLE line's neighbors, so direction is
            // seamless across the cell boundary the run was cut at.
            Vector3 dir = line[Mathf.Min(i + 1, line.Count - 1)]
                - line[Mathf.Max(i - 1, 0)];
            dir.y = 0f;
            if (dir.sqrMagnitude < 1e-6f)
                dir = Vector3.forward;
            dir.Normalize();
            var perp = new Vector3(-dir.z, 0f, dir.x) * hw;
            b.verts.Add(new Vector3(line[i].x - perp.x, ys[i], line[i].z - perp.z));
            b.verts.Add(new Vector3(line[i].x + perp.x, ys[i], line[i].z + perp.z));
            b.minY = Mathf.Min(b.minY, ys[i]);
            b.maxY = Mathf.Max(b.maxY, ys[i]);
        }
        for (int i = 0; i < e - s; i++)
        {
            // Winding order matters and is easy to get mirrored (the
            // LandPlot lesson: a downward face is unhoverable AND invisible
            // from above): with -perp added before +perp, THIS order faces
            // up — verified by reading the baked normals back.
            int a = start + i * 2;
            b.tris.AddRange(new[] { a, a + 1, a + 2, a + 1, a + 3, a + 2 });
        }
    }

    void FlushCell(Transform group, Material mat, (int, int) cell, CellBuilder b)
    {
        if (b.verts.Count == 0)
            return;
        var mesh = new Mesh
        {
            name = $"WatersCell_{cell.Item1}_{cell.Item2}",
            indexFormat = UnityEngine.Rendering.IndexFormat.UInt32,
        };
        mesh.SetVertices(b.verts);
        mesh.SetTriangles(b.tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        SaveMeshAsset(mesh);
        var go = new GameObject(mesh.name);
        go.transform.SetParent(group, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var rend = go.AddComponent<MeshRenderer>();
        rend.sharedMaterial = mat;
        rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        var chunk = go.AddComponent<RiverChunk>();
        chunk.minWaterY = b.minY;
        chunk.maxWaterY = b.maxY;
        chunk.maxWidth = b.maxW;
    }

    // The 1220 filter applied: castle-connecting routes only, painted
    // as through-roads. CastleSite markers are read directly (their
    // play-mode registry doesn't run in edit mode); fewer than two
    // castles means nothing to connect — keep the full network and say
    // so rather than bake an empty countryside silently.
    Fea[] FilterToCastleRoutes(Fea[] roads)
    {
        var sites = FindObjectsByType<CastleSite>(FindObjectsSortMode.None);
        if (roads == null || sites.Length < 2)
        {
            Debug.LogWarning("MarchesDressing: castleRoadsOnly needs 2+"
                + " CastleSite markers — keeping the full road network.");
            return roads;
        }
        var polylines = new List<float[]>(roads.Length);
        foreach (Fea r in roads)
            polylines.Add(r.p);
        var castles = new List<Vector2>(sites.Length);
        foreach (CastleSite s in sites)
            castles.Add(s.PositionXZ);
        // Trimmed at each castle's grounds boundary — the painted road
        // ends at the gate, same outlines the runtime survey uses.
        List<float[]> routes = CastleRoads.Routes(polylines, castles,
            CastleGrounds.Outlines(castles));
        var kept = new Fea[routes.Count];
        for (int i = 0; i < routes.Count; i++)
            kept[i] = new Fea { c = 0, w = roadWidths[0], p = routes[i] };
        Debug.Log($"CastleRoads: {roads.Length} ways filtered to"
            + $" {routes.Count} castle routes.");
        return kept;
    }

    public static List<Vector3> Unflatten(float[] p)
    {
        var pts = new List<Vector3>(p.Length / 2);
        for (int i = 0; i + 1 < p.Length; i += 2)
            pts.Add(new Vector3(p[i], 0f, p[i + 1]));
        return pts;
    }

    // Every original vertex survives (a winding lane keeps its shape); long
    // segments are subdivided so the ribbon follows the ground between them.
    // PUBLIC because the baker's carve and the ribbons here must resample
    // identically — one definition is what makes the refs index-align.
    public static List<Vector3> Resample(List<Vector3> pts, float step)
    {
        var outPts = new List<Vector3>();
        for (int i = 0; i < pts.Count - 1; i++)
        {
            Vector3 a = pts[i], b = pts[i + 1];
            int n = Mathf.Max(1, Mathf.CeilToInt((b - a).magnitude / step));
            for (int k = 0; k < n; k++)
                outPts.Add(Vector3.Lerp(a, b, (float)k / n));
        }
        if (pts.Count > 0)
            outPts.Add(pts[pts.Count - 1]);
        return outPts;
    }

    // ---- Trees -------------------------------------------------------------

    int ScatterTrees(FeaSet set, HashSet<(int, int)> roadCells)
    {
        GameObject oak = EnsureTreePrefab(true);
        GameObject bush = EnsureTreePrefab(false);
        if (oak == null || bush == null)
            return 0;
        Terrain[] tiles = Ground.Tiles();
        if (tiles.Length == 0)
            return 0;

        // Tree instances are tile-normalized, so each scatter point is
        // bucketed into the tile that contains it.
        var buckets = new Dictionary<Terrain, List<TreeInstance>>();
        foreach (Terrain t in tiles)
            buckets[t] = new List<TreeInstance>();
        int total = 0;

        void Add(float x, float z, int proto, float s)
        {
            Terrain t = Ground.TileAt(x, z);
            if (t == null)
                return;
            Vector3 origin = t.transform.position;
            Vector3 size = t.terrainData.size;
            float nx = (x - origin.x) / size.x;
            float nz = (z - origin.z) / size.z;
            if (nx < 0f || nx > 1f || nz < 0f || nz > 1f)
                return;
            buckets[t].Add(new TreeInstance
            {
                position = new Vector3(nx, 0f, nz),
                widthScale = s,
                heightScale = s,
                rotation = Hash01(x, z, 5) * Mathf.PI * 2f,
                color = Color.white,
                lightmapColor = Color.white,
                prototypeIndex = proto,
            });
            total++;
        }

        bool NearRoad(float x, float z)
        {
            int cx = Mathf.FloorToInt(x / RoadCell);
            int cz = Mathf.FloorToInt(z / RoadCell);
            for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                    if (roadCells.Contains((cx + dx, cz + dz)))
                        return true;
            return false;
        }

        if (set.woods != null)
            foreach (Fea wood in set.woods)
            {
                List<Vector3> poly = Unflatten(wood.p);
                float minX = float.MaxValue, maxX = float.MinValue;
                float minZ = float.MaxValue, maxZ = float.MinValue;
                foreach (Vector3 v in poly)
                {
                    minX = Mathf.Min(minX, v.x); maxX = Mathf.Max(maxX, v.x);
                    minZ = Mathf.Min(minZ, v.z); maxZ = Mathf.Max(maxZ, v.z);
                }
                ScatterInPoly(poly, minX, maxX, minZ, maxZ, treeSpacing, 11,
                    (x, z, h) =>
                    {
                        if (!NearRoad(x, z))
                            Add(x, z, 0, 0.75f + h * 0.6f);
                    });
                ScatterInPoly(poly, minX, maxX, minZ, maxZ, bushSpacing, 23,
                    (x, z, h) =>
                    {
                        if (h < 0.5f && !NearRoad(x, z))
                            Add(x, z, 1, 0.8f + h);
                    });
            }

        // Hedging: bushes strung loosely along lanes and through-roads —
        // cheap, and it reads as the Marches' hedgerow lattice.
        if (set.roads != null)
            foreach (Fea r in set.roads)
            {
                if (r.c > 1)
                    continue;
                float off = roadWidths[r.c] * 0.5f + 1.5f;
                List<Vector3> line = Resample(Unflatten(r.p), roadsideBushStep);
                for (int i = 1; i < line.Count - 1; i++)
                {
                    float h = Hash01(line[i].x, line[i].z, 31);
                    if (h > 0.45f)
                        continue;
                    Vector3 dir = line[i + 1] - line[i - 1];
                    dir.y = 0f;
                    if (dir.sqrMagnitude < 1e-6f)
                        continue;
                    dir.Normalize();
                    var perp = new Vector3(-dir.z, 0f, dir.x)
                        * (h < 0.225f ? off : -off);
                    Vector3 p = line[i] + perp;
                    Add(p.x, p.z, 1, 0.7f + h);
                }
            }

        PlantHedgeBanks(set, Add, NearRoad);

        if (total > maxTreeInstances)
        {
            float keep = (float)maxTreeInstances / total;
            int before = total;
            total = 0;
            foreach (Terrain t in tiles)
            {
                List<TreeInstance> bucket = buckets[t];
                var thinned = new List<TreeInstance>(
                    Mathf.CeilToInt(bucket.Count * keep));
                for (int i = 0; i < bucket.Count; i++)
                    if (Hash01(i, t.transform.position.x, 41) < keep)
                        thinned.Add(bucket[i]);
                buckets[t] = thinned;
                total += thinned.Count;
            }
            Debug.LogWarning($"Marches dressing: thinned {before} tree instances to {total} (cap {maxTreeInstances}).");
        }
        foreach (Terrain t in tiles)
        {
            TerrainData data = t.terrainData;
            data.treePrototypes = new[]
            {
                new TreePrototype { prefab = oak },
                new TreePrototype { prefab = bush },
            };
            data.SetTreeInstances(buckets[t].ToArray(), true);
            // Mesh trees don't billboard (the Nature-shader console warning
            // is that fact, not a bug) — distance culling is the whole perf
            // story, and THREE dials govern it. treeBillboardDistance
            // defaults to 50m and treeMaximumFullLODCount to 50: past
            // either limit Unity swaps to billboards, which a plain mesh
            // prefab doesn't have, so trees just vanished ~50m out (the
            // user's first hand-test caught it).
            t.treeDistance = 2500f;
            t.treeBillboardDistance = 2500f;
            t.treeCrossFadeLength = 30f;
            t.treeMaximumFullLODCount = 200000;
        }
        return total;
    }

    // The hedgebank pass: the LIDAR keeps the field-boundary earthworks
    // but strips the hedges that explain them, so naked banks read as
    // artifacts. Walk each FINE tile's heightmap (banks don't survive
    // the coarse tiles' box-average, so those skip by construction),
    // flag samples standing proud of the surrounding ring, and plant a
    // bush on a loose lattice along the crests. Keep-outs: roads and
    // waters (the shared cell mask — carved channel banks read as
    // crests too, and rivers must not grow a hedge), woods (dense trees
    // already; bushes there just burn the instance cap) and castle
    // grounds. Deterministic like every scatter here.
    int PlantHedgeBanks(FeaSet set,
        Action<float, float, int, float> add, Func<float, float, bool> nearRoad)
    {
        if (hedgeBankSignal <= 0f || maxHedgeInstances <= 0)
            return 0;

        var woodPolys = new List<(List<Vector3> poly, float minX, float maxX, float minZ, float maxZ)>();
        if (set.woods != null)
            foreach (Fea wf in set.woods)
            {
                List<Vector3> poly = Unflatten(wf.p);
                float minX = float.MaxValue, maxX = float.MinValue;
                float minZ = float.MaxValue, maxZ = float.MinValue;
                foreach (Vector3 v in poly)
                {
                    minX = Mathf.Min(minX, v.x); maxX = Mathf.Max(maxX, v.x);
                    minZ = Mathf.Min(minZ, v.z); maxZ = Mathf.Max(maxZ, v.z);
                }
                woodPolys.Add((poly, minX, maxX, minZ, maxZ));
            }
        var castles = new List<Vector2>();
        foreach (CastleSite s in FindObjectsByType<CastleSite>(FindObjectsSortMode.None))
            castles.Add(s.PositionXZ);
        List<List<Vector3>> grounds = castles.Count > 0
            ? CastleGrounds.Outlines(castles) : new List<List<Vector3>>();

        bool Excluded(float x, float z)
        {
            if (nearRoad(x, z))
                return true;
            var p = new Vector3(x, 0f, z);
            foreach ((List<Vector3> poly, float minX, float maxX, float minZ, float maxZ) in woodPolys)
                if (x >= minX && x <= maxX && z >= minZ && z <= maxZ
                    && SlabTile.Contains(poly, p))
                    return true;
            foreach (List<Vector3> g in grounds)
                if (SlabTile.Contains(g, p))
                    return true;
            return false;
        }

        // Collect first, thin after: the cap used to be first-come in
        // tile order, which spent the whole budget on the southern
        // tiles and left Grosmont's row bare (caught on the first
        // hand-test). A global hash-thin spreads the survivors evenly.
        var cands = new List<(float x, float z, float s)>();
        foreach (Terrain t in Ground.Tiles())
        {
            TerrainData td = t.terrainData;
            int res = td.heightmapResolution;
            float cell = td.size.x / (res - 1);
            if (cell > 5f)
                continue;   // coarse tile — the box-average smeared the banks away
            float[,] h = td.GetHeights(0, 0, res, res);
            float sy = td.size.y;
            Vector3 origin = t.transform.position;
            int rc = Mathf.Max(1, Mathf.RoundToInt(hedgeBankRing / cell));
            int rd = Mathf.Max(1, Mathf.RoundToInt(hedgeBankRing * 0.707f / cell));
            int stride = Mathf.Max(1, Mathf.RoundToInt(hedgeBushStep / cell));
            for (int iz = rc; iz < res - rc; iz += stride)
                for (int ix = rc; ix < res - rc; ix += stride)
                {
                    float h0 = h[iz, ix] * sy;
                    float sum = 0f, lo = float.MaxValue, hi = float.MinValue;
                    void Ring(int dz, int dx)
                    {
                        float v = h[iz + dz, ix + dx] * sy;
                        sum += v;
                        lo = Mathf.Min(lo, v);
                        hi = Mathf.Max(hi, v);
                    }
                    Ring(rc, 0); Ring(-rc, 0); Ring(0, rc); Ring(0, -rc);
                    Ring(rd, rd); Ring(rd, -rd); Ring(-rd, rd); Ring(-rd, -rd);
                    if (h0 - sum / 8f < hedgeBankSignal)
                        continue;
                    // A ring spanning metres is a hillside or a motte,
                    // not a hedgebank — leave it bare.
                    if (hi - lo > 4f)
                        continue;
                    float wx = origin.x + ix * cell
                        + (Hash01(ix, iz, 57) - 0.5f) * cell;
                    float wz = origin.z + iz * cell
                        + (Hash01(ix, iz, 58) - 0.5f) * cell;
                    if (Excluded(wx, wz))
                        continue;
                    cands.Add((wx, wz, 0.6f + Hash01(ix, iz, 59) * 0.5f));
                }
        }
        float keep = cands.Count > maxHedgeInstances
            ? (float)maxHedgeInstances / cands.Count : 1f;
        int planted = 0;
        foreach ((float x, float z, float s) in cands)
        {
            if (keep < 1f && Hash01(x, z, 61) >= keep)
                continue;
            add(x, z, 1, s);
            planted++;
        }
        Debug.Log($"Marches dressing: {planted} hedgebank bushes from"
            + $" {cands.Count} bank candidates on the fine tiles"
            + (keep < 1f ? $" (thinned ×{keep:F2} to the {maxHedgeInstances} cap)." : "."));
        return planted;
    }

    void ScatterInPoly(List<Vector3> poly, float minX, float maxX,
        float minZ, float maxZ, float spacing, int salt,
        Action<float, float, float> emit)
    {
        int i0 = Mathf.FloorToInt(minX / spacing), i1 = Mathf.CeilToInt(maxX / spacing);
        int j0 = Mathf.FloorToInt(minZ / spacing), j1 = Mathf.CeilToInt(maxZ / spacing);
        for (int i = i0; i <= i1; i++)
            for (int j = j0; j <= j1; j++)
            {
                float x = i * spacing + (Hash01(i, j, salt) - 0.5f) * spacing * 0.9f;
                float z = j * spacing + (Hash01(i, j, salt + 1) - 0.5f) * spacing * 0.9f;
                if (SlabTile.Contains(poly, new Vector3(x, 0f, z)))
                    emit(x, z, Hash01(i, j, salt + 2));
            }
    }

    void MarkRoadCells(HashSet<(int, int)> cells, float[] p, int radius)
    {
        List<Vector3> line = Resample(Unflatten(p), RoadCell * 0.8f);
        foreach (Vector3 v in line)
        {
            int cx = Mathf.FloorToInt(v.x / RoadCell);
            int cz = Mathf.FloorToInt(v.z / RoadCell);
            for (int dx = -radius + 1; dx < radius; dx++)
                for (int dz = -radius + 1; dz < radius; dz++)
                    cells.Add((cx + dx, cz + dz));
        }
    }

    // Deterministic scatter noise — the LandParcels convention: Random would
    // tie the bake to whatever else rolled dice that frame.
    static float Hash01(float a, float b, int salt)
    {
        uint h = (uint)(Mathf.RoundToInt(a * 8f) * 73856093
            ^ Mathf.RoundToInt(b * 8f) * 19349663 ^ salt * 83492791);
        h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
        return (h & 0xffffff) / (float)0x1000000;
    }

    // ---- Road splat --------------------------------------------------------

    // The grass/dirt slope thresholds — TerrainGenerator's rule where a
    // scene carries one (Spiš), the shared defaults otherwise (the
    // Marches has no TerrainGenerator since the BNG rebuild; 24/38 are
    // the values its source terrain used). Public: the baker's initial
    // slope paint uses the same numbers.
    public static (float start, float full) SlopeNumbers()
    {
        var gen = FindAnyObjectByType<TerrainGenerator>(FindObjectsInactive.Include);
        return (gen != null ? gen.dirtSlopeStart : 24f,
                gen != null ? gen.dirtSlopeFull : 38f);
    }

    // Paints the worn-track layer along every road corridor AND the
    // riverbed layer along every waterway: per tile, weight masks
    // rasterized from the polylines (full strength over the core, smooth
    // fade to the edge), combined with the slope rule — grass and dirt
    // split what the paint leaves. Layer 2 is the road, layer 3 the bed;
    // where they meet the RIVER wins (a ford's channel is wet mud, not
    // worn track). TerrainPads.RefreshSurface preserves layers past the
    // first two, so earthworks keep both paints.
    void PaintRoadSplats(Terrain[] tiles, FeaSet set)
    {
        if (set.roads == null || set.roads.Length == 0)
            return;
        TerrainLayer roadLayer = LoadOrCreatePaintLayer(
            "Assets/Terrain/RoadLayer.terrainlayer", new Vector3(1f, 0.93f, 0.8f));
        // Dark wet brown against both grass and slope-dirt: the bed shows
        // through the translucent ribbon tier and under the HDRP water's
        // refraction, so it has to read as mud at a glance.
        TerrainLayer bedLayer = LoadOrCreatePaintLayer(
            "Assets/Terrain/RiverbedLayer.terrainlayer", new Vector3(0.52f, 0.46f, 0.38f));
        (float slopeStart, float slopeFull) = SlopeNumbers();

        // Resample every polyline once; each sample stamps a disc.
        var roadLines = new List<(List<Vector3> pts, float core, float edge)>();
        foreach (Fea r in set.roads)
        {
            float hw = roadWidths[Mathf.Clamp(r.c, 0, roadWidths.Length - 1)] * 0.5f;
            roadLines.Add((Resample(Unflatten(r.p), 1.5f), hw + splatCore, hw + splatCore + splatFade));
        }
        var bedLines = new List<(List<Vector3> pts, float core, float edge)>();
        if (set.waters != null)
        {
            // The mud must lie under the water: where the carve snapped
            // its stations onto the real channel, the bed paint follows
            // the saved as-built stations, not the OSM map line.
            MarchesBaker.RiverRefs bedRefs = LoadRiverRefs();
            for (int fi = 0; fi < set.waters.Length; fi++)
            {
                Fea f = set.waters[fi];
                float w = Mathf.Max(1f, f.w);
                float halfW = w * 0.5f, edge = BankEdge(w);
                List<Vector3> pts = Resample(Unflatten(f.p), 1.5f);
                float[] snapped = bedRefs != null && bedRefs.waters != null
                    && fi < bedRefs.waters.Length
                    ? bedRefs.waters[fi]?.pts : null;
                if (snapped != null && snapped.Length >= 4)
                {
                    var chan = new List<Vector3>(snapped.Length / 2);
                    for (int i = 0; i + 1 < snapped.Length; i += 2)
                        chan.Add(new Vector3(snapped[i], 0f, snapped[i + 1]));
                    pts = Resample(chan, 1.5f);
                }
                // Full strength past the water ribbon's own edge so no
                // grass survives underwater; fade dies at the bank line.
                bedLines.Add((pts,
                    halfW + (edge - halfW) * 0.6f + 0.5f, edge + 1f));
            }
        }

        foreach (Terrain t in tiles)
        {
            TerrainData data = t.terrainData;
            TerrainLayer[] layers = data.terrainLayers;
            if (layers == null || layers.Length < 2)
                continue;   // not the grass/dirt convention — leave alone
            int res = Mathf.Max(64, data.alphamapResolution);
            Vector3 o = t.transform.position;
            Vector3 size = data.size;
            float pxPerM = (res - 1) / size.x;

            void Stamp(float[,] mask, List<(List<Vector3> pts, float core, float edge)> lines)
            {
                foreach ((List<Vector3> pts, float core, float edge) in lines)
                {
                    foreach (Vector3 p in pts)
                    {
                        if (p.x < o.x - edge || p.x > o.x + size.x + edge
                            || p.z < o.z - edge || p.z > o.z + size.z + edge)
                            continue;
                        int x0 = Mathf.Max(0, Mathf.FloorToInt((p.x - edge - o.x) * pxPerM));
                        int x1 = Mathf.Min(res - 1, Mathf.CeilToInt((p.x + edge - o.x) * pxPerM));
                        int z0 = Mathf.Max(0, Mathf.FloorToInt((p.z - edge - o.z) * pxPerM));
                        int z1 = Mathf.Min(res - 1, Mathf.CeilToInt((p.z + edge - o.z) * pxPerM));
                        for (int iz = z0; iz <= z1; iz++)
                        {
                            float wz = o.z + iz / pxPerM;
                            for (int ix = x0; ix <= x1; ix++)
                            {
                                float wx = o.x + ix / pxPerM;
                                float dx = wx - p.x, dz = wz - p.z;
                                float d = Mathf.Sqrt(dx * dx + dz * dz);
                                float w = 1f - Mathf.SmoothStep(0f, 1f,
                                    Mathf.InverseLerp(core, edge, d));
                                if (w > mask[iz, ix])
                                    mask[iz, ix] = w;
                            }
                        }
                    }
                }
            }

            var roadMask = new float[res, res];   // [row = z, col = x]
            var bedMask = new float[res, res];
            Stamp(roadMask, roadLines);
            Stamp(bedMask, bedLines);

            data.alphamapResolution = res;
            data.terrainLayers = new[] { layers[0], layers[1], roadLayer, bedLayer };
            var alphas = new float[res, res, 4];
            for (int iz = 0; iz < res; iz++)
            {
                float wz = o.z + iz / pxPerM;
                for (int ix = 0; ix < res; ix++)
                {
                    float wx = o.x + ix / pxPerM;
                    float steep = data.GetSteepness(
                        (float)ix / (res - 1), (float)iz / (res - 1));
                    float dirt = Mathf.InverseLerp(slopeStart, slopeFull, steep);
                    // Field mottle: two octaves of world-space noise
                    // lean the grass/dirt mix around so open country
                    // isn't one billiard-table material — the uniform
                    // grass made every LIDAR crease read as an artifact.
                    // World-anchored, so tile seams can't show.
                    if (splatMottle > 0f)
                    {
                        float m = Mathf.PerlinNoise(
                                wx * 0.011f + 31.7f, wz * 0.011f + 12.3f) * 0.65f
                            + Mathf.PerlinNoise(
                                wx * 0.047f + 5.1f, wz * 0.047f + 77.9f) * 0.35f;
                        dirt = Mathf.Clamp01(dirt + (m - 0.5f) * splatMottle);
                    }
                    float bed = bedMask[iz, ix];
                    float road = roadMask[iz, ix] * (1f - bed);
                    float rem = 1f - bed - road;
                    alphas[iz, ix, 0] = rem * (1f - dirt);
                    alphas[iz, ix, 1] = rem * dirt;
                    alphas[iz, ix, 2] = road;
                    alphas[iz, ix, 3] = bed;
                }
            }
            data.SetAlphamaps(0, 0, alphas);
        }
    }

    // Clear Dressing's splat half: back to the two-layer slope-only paint.
    static void RestoreSlopeSplats(TerrainData data)
    {
        TerrainLayer[] layers = data.terrainLayers;
        if (layers == null || layers.Length <= 2)
            return;
        (float slopeStart, float slopeFull) = SlopeNumbers();
        data.terrainLayers = new[] { layers[0], layers[1] };
        int res = data.alphamapResolution;
        var alphas = new float[res, res, 2];
        for (int iz = 0; iz < res; iz++)
        {
            for (int ix = 0; ix < res; ix++)
            {
                float steep = data.GetSteepness(
                    (float)ix / (res - 1), (float)iz / (res - 1));
                float dirt = Mathf.InverseLerp(slopeStart, slopeFull, steep);
                alphas[iz, ix, 0] = 1f - dirt;
                alphas[iz, ix, 1] = dirt;
            }
        }
        data.SetAlphamaps(0, 0, alphas);
    }

    // Road and riverbed paint layers, differing only in remap tint.
    // Borrow the dirt layer's REAL textures: a flat white diffuse carries
    // alpha = 1, and HDRP TerrainLit reads smoothness from diffuse alpha —
    // the first road bake's lanes shone like wet sand. The tint is what
    // says "worn track" or "wet mud" against slope-dirt.
    TerrainLayer LoadOrCreatePaintLayer(string path, Vector3 tint)
    {
#if UNITY_EDITOR
        TerrainLayer layer = AssetDatabase.LoadAssetAtPath<TerrainLayer>(path);
        if (layer == null)
        {
            layer = new TerrainLayer();
            AssetDatabase.CreateAsset(layer, path);
        }
#else
        var layer = new TerrainLayer();
#endif
        TerrainLayer dirt = null;
#if UNITY_EDITOR
        dirt = AssetDatabase.LoadAssetAtPath<TerrainLayer>(
            "Assets/Terrain/DirtLayer.terrainlayer");
#endif
        if (dirt != null && dirt.diffuseTexture != null)
        {
            layer.diffuseTexture = dirt.diffuseTexture;
            layer.normalMapTexture = dirt.normalMapTexture;
            layer.tileSize = dirt.tileSize;
            layer.diffuseRemapMax = new Vector4(
                tint.x, tint.y, tint.z, dirt.diffuseRemapMax.w);
        }
        else
        {
            layer.diffuseTexture = Texture2D.whiteTexture;
            layer.tileSize = Vector2.one * 10f;
            // Kill the white texture's alpha-borne smoothness via the remap.
            layer.diffuseRemapMax = new Vector4(
                tint.x * 0.4f, tint.y * 0.4f, tint.z * 0.4f, 0f);
        }
        layer.smoothness = 0f;
        return layer;
    }

    // ---- Assets ------------------------------------------------------------

    // Graybox oak/bush built from primitive meshes, combined to one mesh with
    // two submeshes, saved as a prefab asset — terrain trees need a persisted
    // prefab, and the oak's trunk gets a collider (TerrainCollider bakes tree
    // colliders from the prototype, so the walker can't ghost through it).
    GameObject EnsureTreePrefab(bool oak)
    {
#if UNITY_EDITOR
        string path = DressFolder + (oak ? "/GrayboxOak.prefab" : "/GrayboxBush.prefab");
        var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (existing != null)
            return existing;
        EnsureFolder();

        // Hand-built low-poly geometry, NOT primitives: the primitive sphere
        // is ~760 tris, and a quarter-million instances of that is a couple
        // hundred million triangles. An icosahedron blob + prism trunk is
        // ~60 tris per oak.
        var combines = new List<CombineInstance>();
        if (oak)
            combines.Add(new CombineInstance
            {
                mesh = TrunkMesh(0.3f, 3.4f, 6),
                transform = Matrix4x4.identity,
            });
        combines.Add(new CombineInstance
        {
            mesh = oak
                ? CanopyMesh(new Vector3(0f, 4.6f, 0f), new Vector3(2.6f, 2.1f, 2.6f), 3)
                : CanopyMesh(new Vector3(0f, 0.55f, 0f), new Vector3(0.85f, 0.6f, 0.85f), 9),
            transform = Matrix4x4.identity,
        });
        var mesh = new Mesh { name = oak ? "GrayboxOakMesh" : "GrayboxBushMesh" };
        mesh.CombineMeshes(combines.ToArray(), false, true);
        mesh.RecalculateBounds();
        AssetDatabase.CreateAsset(mesh, DressFolder + "/" + mesh.name + ".asset");

        Material bark = LoadOrCreateMaterial("BarkMat", new Color(0.32f, 0.24f, 0.17f), 0.2f);
        Material leaf = LoadOrCreateMaterial(oak ? "LeafMat" : "BushLeafMat",
            oak ? new Color(0.22f, 0.36f, 0.16f) : new Color(0.26f, 0.38f, 0.20f), 0.25f);

        var go = new GameObject(oak ? "GrayboxOak" : "GrayboxBush");
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var rend = go.AddComponent<MeshRenderer>();
        rend.sharedMaterials = oak ? new[] { bark, leaf } : new[] { leaf };
        if (oak)
        {
            var cap = go.AddComponent<CapsuleCollider>();
            cap.center = new Vector3(0f, 1.5f, 0f);
            cap.height = 3f;
            cap.radius = 0.35f;
        }
        GameObject prefab = PrefabUtility.SaveAsPrefabAsset(go, path);
        DestroyImmediate(go);
        return prefab;
#else
        return null;
#endif
    }

#if UNITY_EDITOR
    // Icosahedron stretched to the given radii, vertices jittered by hash so
    // no two prototypes read identical. Shared verts + RecalculateNormals =
    // a soft-shaded blob, which is what distant foliage wants.
    static Mesh CanopyMesh(Vector3 center, Vector3 radii, int salt)
    {
        float t = (1f + Mathf.Sqrt(5f)) / 2f;
        Vector3[] ico =
        {
            new(-1, t, 0), new(1, t, 0), new(-1, -t, 0), new(1, -t, 0),
            new(0, -1, t), new(0, 1, t), new(0, -1, -t), new(0, 1, -t),
            new(t, 0, -1), new(t, 0, 1), new(-t, 0, -1), new(-t, 0, 1),
        };
        int[] tris =
        {
            0,11,5, 0,5,1, 0,1,7, 0,7,10, 0,10,11,
            1,5,9, 5,11,4, 11,10,2, 10,7,6, 7,1,8,
            3,9,4, 3,4,2, 3,2,6, 3,6,8, 3,8,9,
            4,9,5, 2,4,11, 6,2,10, 8,6,7, 9,8,1,
        };
        var verts = new Vector3[12];
        for (int i = 0; i < 12; i++)
        {
            float jitter = 0.85f + Hash01(i, salt, 7) * 0.3f;
            verts[i] = center + Vector3.Scale(ico[i].normalized, radii) * jitter;
        }
        var m = new Mesh { vertices = verts, triangles = tris };
        m.RecalculateNormals();
        m.RecalculateBounds();
        return m;
    }

    // Open-ended tapered prism — the canopy hides the top, the ground the
    // bottom.
    static Mesh TrunkMesh(float radius, float height, int sides)
    {
        var verts = new List<Vector3>();
        var tris = new List<int>();
        for (int i = 0; i < sides; i++)
        {
            float a0 = i * 2f * Mathf.PI / sides;
            float a1 = (i + 1) * 2f * Mathf.PI / sides;
            int b = verts.Count;
            verts.Add(new Vector3(Mathf.Cos(a0) * radius, 0f, Mathf.Sin(a0) * radius));
            verts.Add(new Vector3(Mathf.Cos(a1) * radius, 0f, Mathf.Sin(a1) * radius));
            verts.Add(new Vector3(Mathf.Cos(a0) * radius * 0.7f, height, Mathf.Sin(a0) * radius * 0.7f));
            verts.Add(new Vector3(Mathf.Cos(a1) * radius * 0.7f, height, Mathf.Sin(a1) * radius * 0.7f));
            tris.AddRange(new[] { b, b + 2, b + 1, b + 1, b + 2, b + 3 });
        }
        var m = new Mesh();
        m.SetVertices(verts);
        m.SetTriangles(tris, 0);
        m.RecalculateNormals();
        m.RecalculateBounds();
        return m;
    }
#endif

    Material LoadOrCreateMaterial(string name, Color color, float smoothness)
    {
#if UNITY_EDITOR
        string path = DressFolder + "/" + name + ".mat";
        Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            EnsureFolder();
            mat = new Material(Shader.Find("HDRP/Lit")) { name = name };
            AssetDatabase.CreateAsset(mat, path);
        }
#else
        Material mat = new Material(Shader.Find("HDRP/Lit")) { name = name };
#endif
        mat.SetColor("_BaseColor", color);
        mat.SetFloat("_Smoothness", smoothness);
        // A quarter-million tree instances live or die by instancing.
        mat.enableInstancing = true;
        return mat;
    }

    void SaveMeshAsset(Mesh mesh)
    {
#if UNITY_EDITOR
        EnsureFolder();
        string path = DressFolder + "/" + mesh.name + ".asset";
        AssetDatabase.DeleteAsset(path);
        AssetDatabase.CreateAsset(mesh, path);
#endif
    }

#if UNITY_EDITOR
    static void EnsureFolder()
    {
        if (!AssetDatabase.IsValidFolder(DressFolder))
            AssetDatabase.CreateFolder("Assets/Terrain", "Dressing");
    }
#endif
}
