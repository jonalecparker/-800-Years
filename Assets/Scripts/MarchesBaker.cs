using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

// The tile bake (Docs/WorldFrame.md): THE TILE IS THE ARTIFACT. Each
// 2km terrain tile bakes independently and deterministically from BNG
// source rasters + stored facts — there is no source terrain, and the
// un-do of anything baked is "re-run the bake". Replaces TerrainTiler
// (split-from-source) and CastleLidar (patch-after-split), both
// deleted, not dormant.
//
// Sources, both BNG + ODN native (no reprojection, no datum fitting):
// - NRW 2m DTM flight strips (TerrainSources/lidar/asc, mm units).
//   Strips OVERLAP: several files can cover one 2km cell with
//   different NODATA holes, so the field MERGES per sample — first
//   valid value across files wins. (The old per-cell dictionary kept
//   one file arbitrarily; the 08-14 bake got lucky at its 3 sites.)
// - OS Terrain 50 (TerrainSources/os-terrain50, metres) — the
//   full-GB fallback wherever the strips have holes. 50m ground is
//   fine for countryside 5km from a castle; the bake logs how much
//   of each tile fell back so a holey tile can't pass silently.
//
// The castle rule: any tile within castlePatchRadius of a CastleSite
// bakes at castleResolution (1.95m — the LIDAR's own density);
// everything else at baseResolution (7.81m). Neighbouring castle
// tiles meet at same-resolution seams; where a castle tile meets a
// base tile, the fine edge is PINNED collinear with the coarse edge
// (every 4th sample copies the neighbour's edge value, the rest
// lerp), so the geometry cannot crack — only LOD hairlines remain,
// accepted until seen.
//
// Rivers carve INSIDE the bake (pass 1 refs, pass 2 cut), because
// carve refs must come from PRE-carve ground and the in-memory base
// field is exactly that — the old "sample the pre-split source"
// trick without the source. The refs are SAVED (MarchesRiverRefs.json)
// so Dress's water ribbons sit at the same levels the carve dug —
// stored, not re-derived from carved ground.
public class MarchesBaker : MonoBehaviour
{
    [Tooltip("NRW 2m DTM ASC folder (mm units), relative to project root.")]
    public string lidarFolder = "TerrainSources/lidar";
    [Tooltip("OS Terrain 50 ASC folder (metre units), relative to project root.")]
    public string t50Folder = "TerrainSources/os-terrain50/asc";

    [Header("The tile lattice (Docs/WorldFrame.md)")]
    public int tilesPerSide = 8;
    public int baseResolution = 257;     // 7.81m over a 2km tile
    public int castleResolution = 1025;  // 1.95m — the LIDAR's density
    [Tooltip("Tiles within this of a CastleSite bake at castleResolution.")]
    public float castlePatchRadius = 600f;
    // Resolution follows FEATURES, not just castles: an 8m channel at
    // 7.8m sampling is one sample wide — an angular zigzag no dressing
    // fixes. Tiles a major river crosses split the difference.
    public int riverResolution = 513;    // 3.9m
    [Tooltip("Waterways at least this wide bump their tiles to riverResolution.")]
    public float riverTileMinWidth = 5f;
    public int alphamapResolution = 512; // ~3.9m/px, the pre-Dress slope paint

    [Header("Render settings stamped on every tile")]
    public float pixelError = 2f;        // the draped-mesh contract
    public float basemapDistance = 1024f;
    public int groupingID = 800;

    public const float CarveStep = 3f;   // channel resample; ribbons MUST match
    public const string TileFolder = "Assets/Terrain/Tiles";
    public const string RiverRefsPath = "Assets/Terrain/MarchesRiverRefs.json";
    const string RootName = "Marches Tiles";
    const string TileBaseName = "Marches"; // Ground.BaseName strips " [SO....]"

    // Saved carve references, index-aligned with the features asset's
    // waters array (same polylines, same CarveStep resampling — Dress
    // consumes them for the ribbon water levels).
    [System.Serializable]
    public class RiverRefs
    {
        // refs: pre-carve bank levels per station. pts: the thalweg-
        // snapped station positions (x,z interleaved, same count) —
        // where the carve ACTUALLY cut, which the ribbons and the bed
        // splat must follow instead of the OSM map line.
        [System.Serializable] public class Row { public float[] refs; public float[] pts; }
        public Row[] waters;
    }

    [System.Serializable] class Fea { public int c; public float w; public float[] p; }
    [System.Serializable] class FeaSet { public Fea[] roads; public Fea[] waters; public Fea[] woods; }

    [ContextMenu("Bake Tiles")]
    public void BakeTiles()
    {
#if UNITY_EDITOR
        if (Application.isPlaying)
        {
            Debug.LogError("MarchesBaker: an edit-mode bake — stop play first.");
            return;
        }
        double t0 = EditorApplication.timeSinceStartup;
        int n = Mathf.Max(1, tilesPerSide);
        float tile = WorldFrame.TileSize;

        // The castle rule reads the scene's sites (already in the BNG
        // world frame — the markers move before the first bake).
        var sites = new List<Vector2>();
        foreach (CastleSite s in FindObjectsByType<CastleSite>(
            FindObjectsInactive.Include, FindObjectsSortMode.None))
            sites.Add(s.PositionXZ);
        if (sites.Count == 0)
            Debug.LogWarning("MarchesBaker: no CastleSite markers — every tile bakes at base resolution.");

        // The features drive both the carve and the river-tile rule, so
        // they parse up front (a missing dressing bakes uncarved at base
        // resolution outside the castle patches, loudly).
        var dressing = FindAnyObjectByType<MarchesDressing>(FindObjectsInactive.Include);
        FeaSet set = null;
        if (dressing != null && dressing.features != null)
            set = JsonUtility.FromJson<FeaSet>(dressing.features.text);
        else
            Debug.LogWarning("MarchesBaker: no MarchesDressing/features — tiles bake uncarved, no river refs, no river-resolution rule.");

        var resOf = new int[n, n];
        var castleNames = new List<string>();
        for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++)
            {
                resOf[r, c] = baseResolution;
                var rect = new Rect(c * tile, r * tile, tile, tile);
                foreach (Vector2 s in sites)
                    if (RectDistance(rect, s) <= castlePatchRadius)
                    {
                        resOf[r, c] = castleResolution;
                        WorldFrame.BngFromWorld(
                            new Vector2(c * tile, r * tile), out double e, out double no);
                        castleNames.Add(WorldFrame.GridRefName(e, no));
                        break;
                    }
            }
        int riverTiles = 0;
        if (set != null && set.waters != null)
        {
            const float reach = 30f; // channel + banks + a pin margin
            foreach (Fea f in set.waters)
            {
                if (f.w < riverTileMinWidth)
                    continue;
                List<Vector3> line = MarchesDressing.Resample(
                    MarchesDressing.Unflatten(f.p), 25f);
                foreach (Vector3 p in line)
                {
                    int c0 = Mathf.Clamp(Mathf.FloorToInt((p.x - reach) / tile), 0, n - 1);
                    int c1 = Mathf.Clamp(Mathf.FloorToInt((p.x + reach) / tile), 0, n - 1);
                    int r0 = Mathf.Clamp(Mathf.FloorToInt((p.z - reach) / tile), 0, n - 1);
                    int r1 = Mathf.Clamp(Mathf.FloorToInt((p.z + reach) / tile), 0, n - 1);
                    for (int r = r0; r <= r1; r++)
                        for (int c = c0; c <= c1; c++)
                            if (resOf[r, c] < riverResolution)
                            {
                                resOf[r, c] = riverResolution;
                                riverTiles++;
                            }
                }
            }
        }

        string projectRoot = System.IO.Path.GetDirectoryName(Application.dataPath);
        GriddedField lidar, t50;
        var grids = new float[n, n][,];
        long t50Fill = 0, holes = 0;
        var tileFallback = new List<string>();
        try
        {
            EditorUtility.DisplayProgressBar("Marches bake", "Loading NRW 2m LIDAR…", 0.02f);
            lidar = GriddedField.Load(
                System.IO.Path.Combine(projectRoot, lidarFolder), 0.001f,
                0.0, 0.0, n * tile, n * tile);
            EditorUtility.DisplayProgressBar("Marches bake", "Loading OS Terrain 50…", 0.25f);
            t50 = GriddedField.Load(
                System.IO.Path.Combine(projectRoot, t50Folder), 1f,
                0.0, 0.0, n * tile, n * tile);
            if (lidar.TileCount == 0)
            {
                Debug.LogError($"MarchesBaker: no LIDAR ASC tiles under {lidarFolder} — nothing to bake from.");
                return;
            }

            // ---- Base heights, tile by tile ----
            for (int r = 0; r < n; r++)
                for (int c = 0; c < n; c++)
                {
                    EditorUtility.DisplayProgressBar("Marches bake",
                        $"Heights [{r},{c}]…", 0.3f + 0.35f * (r * n + c) / (n * n));
                    int res = resOf[r, c];
                    float cell = tile / (res - 1);
                    float half = cell * 0.5f;
                    var g = new float[res, res];
                    long fills = 0, dead = 0;
                    for (int iz = 0; iz < res; iz++)
                        for (int ix = 0; ix < res; ix++)
                        {
                            double e = WorldFrame.OriginEasting + c * tile + ix * cell;
                            double no = WorldFrame.OriginNorthing + r * tile + iz * cell;
                            float v = res == castleResolution
                                ? lidar.Bilinear(e, no)
                                : lidar.BoxAverage(e, no, half, 0.75f);
                            if (float.IsNaN(v))
                            {
                                v = t50.Bilinear(e, no);
                                fills++;
                                if (float.IsNaN(v))
                                {
                                    // Full-GB coverage should make this
                                    // unreachable; a hole must be LOUD,
                                    // not a quiet sea-level pit.
                                    v = 60f;
                                    dead++;
                                }
                            }
                            g[iz, ix] = v;
                        }
                    grids[r, c] = g;
                    t50Fill += fills;
                    holes += dead;
                    float frac = (float)fills / (res * res);
                    if (frac > 0.02f)
                        tileFallback.Add($"[{r},{c}] {frac:P0}");
                }

            // ---- Pin fine edges to coarse neighbours ----
            // A finer tile's edge against a coarser neighbour copies the
            // neighbour's exact edge samples at the shared lattice and
            // lerps between — collinear by construction, so the seam
            // geometry cannot crack whatever the LOD does to it. With
            // three resolutions the pins run COARSEST-FIRST (river tiles
            // pin to base neighbours before castle tiles pin to either),
            // so a castle tile pinned to a river tile reads the river
            // tile's POST-pin edge and every corner agrees with the
            // coarsest tile touching it.
            foreach (int tier in new[] { riverResolution, castleResolution })
                for (int r = 0; r < n; r++)
                    for (int c = 0; c < n; c++)
                    {
                        if (resOf[r, c] != tier)
                            continue;
                        int mine = resOf[r, c];
                        if (c > 0 && resOf[r, c - 1] < mine)
                            PinEdge(grids[r, c], grids[r, c - 1],
                                (mine - 1) / (resOf[r, c - 1] - 1), west: true);
                        if (c < n - 1 && resOf[r, c + 1] < mine)
                            PinEdge(grids[r, c], grids[r, c + 1],
                                (mine - 1) / (resOf[r, c + 1] - 1), west: false);
                        if (r > 0 && resOf[r - 1, c] < mine)
                            PinEdgeRow(grids[r, c], grids[r - 1, c],
                                (mine - 1) / (resOf[r - 1, c] - 1), south: true);
                        if (r < n - 1 && resOf[r + 1, c] < mine)
                            PinEdgeRow(grids[r, c], grids[r + 1, c],
                                (mine - 1) / (resOf[r + 1, c] - 1), south: false);
                    }

            // Corner consensus: an interior lattice corner belongs to
            // FOUR tiles, and with three resolutions the column-then-row
            // pin order can leave the four copies split by a few cm —
            // a real pinhole crack. The coarsest tile at the corner is
            // the authority; all four copy it.
            for (int r = 1; r < n; r++)
                for (int c = 1; c < n; c++)
                {
                    int rb = r, cb = c, best = int.MaxValue;
                    for (int dr = 0; dr <= 1; dr++)
                        for (int dc = 0; dc <= 1; dc++)
                            if (resOf[r - dr, c - dc] < best)
                            {
                                best = resOf[r - dr, c - dc];
                                rb = r - dr;
                                cb = c - dc;
                            }
                    float[,] ga = grids[rb, cb];
                    int ra = ga.GetLength(0);
                    float v = ga[rb < r ? ra - 1 : 0, cb < c ? ra - 1 : 0];
                    for (int dr = 0; dr <= 1; dr++)
                        for (int dc = 0; dc <= 1; dc++)
                        {
                            float[,] g = grids[r - dr, c - dc];
                            int res = g.GetLength(0);
                            g[dr == 1 ? res - 1 : 0, dc == 1 ? res - 1 : 0] = v;
                        }
                }

            // ---- Carve rivers (refs first, then the cut) ----
            EditorUtility.DisplayProgressBar("Marches bake", "Carving rivers…", 0.7f);
            CarveIntoGrids(grids, set, dressing, n, tile);
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        // ---- Emit the tiles ----
        ClearTiles();
        var grass = AssetDatabase.LoadAssetAtPath<TerrainLayer>(
            "Assets/Terrain/GrassLayer.terrainlayer");
        var dirt = AssetDatabase.LoadAssetAtPath<TerrainLayer>(
            "Assets/Terrain/DirtLayer.terrainlayer");
        var mat = AssetDatabase.LoadAssetAtPath<Material>(
            "Assets/Terrain/CountrysideTerrainMat.mat");
        if (grass == null || dirt == null)
            Debug.LogWarning("MarchesBaker: Grass/Dirt terrain layers missing — tiles bake unlayered.");
        if (!AssetDatabase.IsValidFolder("Assets/Terrain"))
            AssetDatabase.CreateFolder("Assets", "Terrain");
        if (!AssetDatabase.IsValidFolder(TileFolder))
            AssetDatabase.CreateFolder("Assets/Terrain", "Tiles");

        var root = new GameObject(RootName).transform;
        try
        {
            for (int r = 0; r < n; r++)
                for (int c = 0; c < n; c++)
                {
                    EditorUtility.DisplayProgressBar("Marches bake",
                        $"Tiles [{r},{c}]…", 0.75f + 0.25f * (r * n + c) / (n * n));
                    float[,] g = grids[r, c];
                    int res = g.GetLength(0);
                    var td = new TerrainData();
                    td.heightmapResolution = res;
                    td.size = new Vector3(tile, WorldFrame.HeightRange, tile);
                    var h = new float[res, res];
                    for (int iz = 0; iz < res; iz++)
                        for (int ix = 0; ix < res; ix++)
                            h[iz, ix] = Mathf.Clamp01(
                                g[iz, ix] / WorldFrame.HeightRange);
                    td.SetHeights(0, 0, h);
                    if (grass != null && dirt != null)
                    {
                        td.terrainLayers = new[] { grass, dirt };
                        ApplySlopeAlphamap(td, alphamapResolution);
                    }

                    WorldFrame.BngFromWorld(new Vector2(c * tile, r * tile),
                        out double e, out double no);
                    string name = $"{TileBaseName} [{WorldFrame.GridRefName(e, no)}]";
                    string path = $"{TileFolder}/{name}.asset";
                    AssetDatabase.DeleteAsset(path);
                    AssetDatabase.CreateAsset(td, path);

                    GameObject go = Terrain.CreateTerrainGameObject(td);
                    go.name = name;
                    go.transform.SetParent(root, false);
                    go.transform.localPosition = new Vector3(c * tile, 0f, r * tile);
                    var t = go.GetComponent<Terrain>();
                    if (mat != null)
                        t.materialTemplate = mat;
                    t.heightmapPixelError = pixelError;
                    t.basemapDistance = basemapDistance;
                    t.drawInstanced = true;
                    t.groupingID = groupingID;
                    t.allowAutoConnect = true;
                }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        AssetDatabase.SaveAssets();
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(gameObject.scene);
        string fallbackNote = tileFallback.Count > 0
            ? $" T50-fallback tiles: {string.Join(", ", tileFallback)}."
            : " No tile needed >2% T50 fill.";
        Debug.Log($"MarchesBaker: {n}×{n} tiles baked in"
            + $" {EditorApplication.timeSinceStartup - t0:F0}s —"
            + $" {castleNames.Count} castle tiles ({string.Join(", ", castleNames)})"
            + $" at {castleResolution}, {riverTiles} river tiles at"
            + $" {riverResolution}, rest at {baseResolution}."
            + $" {t50Fill} samples filled from Terrain 50, {holes} unfillable."
            + fallbackNote);
        if (holes > 0)
            Debug.LogError($"MarchesBaker: {holes} samples had NO source data"
                + " (flat-60m stand-in) — is the Terrain 50 folder populated?");
#else
        Debug.LogError("MarchesBaker: editor-only bake.");
#endif
    }

    // The one-time scene swap out of the Mercator frame (run BEFORE the
    // first bake): moves every world-placed object through the legacy
    // chain, adopts the dressing component, and deletes the old world —
    // the source terrain, its 4×4 tiles, their assets, the dressing
    // output and the stale survey cache. Guarded by the old source's
    // presence so a second run refuses instead of double-transforming.
    [ContextMenu("Migrate Scene From Mercator Frame")]
    public void MigrateSceneFromMercatorFrame()
    {
#if UNITY_EDITOR
        if (Application.isPlaying)
        {
            Debug.LogError("MarchesBaker: migrate in edit mode.");
            return;
        }
        TerrainGenerator src = FindAnyObjectByType<TerrainGenerator>(
            FindObjectsInactive.Include);
        if (src == null)
        {
            Debug.LogError("MarchesBaker: no source terrain in the scene —"
                + " already migrated, or the wrong scene.");
            return;
        }

        // The dressing rides the old terrain object; its serialized
        // fields (features asset, every dial) come across to this one.
        var oldDress = src.GetComponent<MarchesDressing>();
        if (oldDress != null)
        {
            var dress = gameObject.GetComponent<MarchesDressing>();
            if (dress == null)
                dress = gameObject.AddComponent<MarchesDressing>();
            EditorUtility.CopySerialized(oldDress, dress);
        }

        // World-placed objects: castle sites and the neighbor-castle
        // graybox. XZ through the legacy chain, Y lifted to ODN, and
        // rotated by the local frame rotation (grid convergence, ~0.7°)
        // so masonry keeps its bearing relative to the land.
        int moved = 0;
        foreach (CastleSite s in FindObjectsByType<CastleSite>(
            FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            MoveLegacy(s.transform, rotate: false);
            moved++;
        }
        GameObject neighbors = GameObject.Find("Neighbor Castles");
        if (neighbors != null)
            foreach (Transform child in neighbors.transform)
            {
                MoveLegacy(child, rotate: true);
                moved++;
            }

        // The old world, out: dressing output, tile GameObjects, the
        // source terrain object, their assets, the stale survey cache.
        foreach (string name in new[] { "Marches Dressing", src.name + " Tiles" })
        {
            GameObject go = GameObject.Find(name);
            if (go != null)
                DestroyImmediate(go);
        }
        if (AssetDatabase.IsValidFolder(TileFolder))
            foreach (string guid in AssetDatabase.FindAssets(
                "t:TerrainData", new[] { TileFolder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (System.IO.Path.GetFileName(path).StartsWith(src.name + " ["))
                    AssetDatabase.DeleteAsset(path);
            }
        var srcData = src.GetComponent<Terrain>()?.terrainData;
        string srcDataPath = srcData != null
            ? AssetDatabase.GetAssetPath(srcData) : null;
        TextAsset dem = src.demHeightmap;
        string demPath = dem != null ? AssetDatabase.GetAssetPath(dem) : null;
        DestroyImmediate(src.gameObject);
        if (!string.IsNullOrEmpty(srcDataPath))
            AssetDatabase.DeleteAsset(srcDataPath);
        if (!string.IsNullOrEmpty(demPath))
            AssetDatabase.DeleteAsset(demPath);
        AssetDatabase.DeleteAsset("Assets/Terrain/SurveyCache-Terrain.json");

        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(gameObject.scene);
        Debug.Log($"MarchesBaker: scene migrated to the BNG frame —"
            + $" {moved} placed objects moved, the old terrain and its"
            + " assets deleted. Bake Tiles next, then Dress.");
#endif
    }

#if UNITY_EDITOR
    static void MoveLegacy(Transform t, bool rotate)
    {
        Vector3 p = t.position;
        Vector2 xz = WorldFrame.WorldFromOldWorld(new Vector2(p.x, p.z));
        t.position = new Vector3(xz.x, p.y + WorldFrame.LegacyLift, xz.y);
        if (!rotate)
            return;
        // The frame map's local rotation: where the old +x axis lands.
        Vector2 dx = WorldFrame.WorldFromOldWorld(new Vector2(p.x + 1f, p.z))
            - WorldFrame.WorldFromOldWorld(new Vector2(p.x, p.z));
        float theta = Mathf.Atan2(dx.y, dx.x) * Mathf.Rad2Deg;
        // Unity yaw is clockwise from above: a CCW map rotation of θ is
        // a yaw of −θ, composed onto whatever the object had.
        t.rotation = Quaternion.Euler(0f, -theta, 0f) * t.rotation;
    }
#endif

    [ContextMenu("Clear Tiles")]
    public void ClearTiles()
    {
        GameObject root = GameObject.Find(RootName);
        if (root != null)
        {
            if (Application.isPlaying) Destroy(root);
            else DestroyImmediate(root);
        }
#if UNITY_EDITOR
        if (AssetDatabase.IsValidFolder(TileFolder))
            foreach (string guid in AssetDatabase.FindAssets(
                "t:TerrainData", new[] { TileFolder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (System.IO.Path.GetFileName(path).StartsWith(TileBaseName + " ["))
                    AssetDatabase.DeleteAsset(path);
            }
#endif
    }

#if UNITY_EDITOR
    // ---- Edge pinning ----

    // West/east edges: column 0 or res−1 of the fine grid, pinned to
    // the coarse neighbour's opposite edge column.
    static void PinEdge(float[,] fine, float[,] coarse, int span, bool west)
    {
        int fres = fine.GetLength(0), cres = coarse.GetLength(0);
        int fx = west ? 0 : fres - 1;
        int cx = west ? cres - 1 : 0;
        for (int iz = 0; iz < fres; iz++)
        {
            int k = iz / span, m = iz % span;
            float a = coarse[k, cx];
            float b = coarse[Mathf.Min(k + 1, cres - 1), cx];
            fine[iz, fx] = m == 0 ? a : Mathf.Lerp(a, b, (float)m / span);
        }
    }

    // South/north edges: row 0 or res−1.
    static void PinEdgeRow(float[,] fine, float[,] coarse, int span, bool south)
    {
        int fres = fine.GetLength(0), cres = coarse.GetLength(0);
        int fz = south ? 0 : fres - 1;
        int cz = south ? cres - 1 : 0;
        for (int ix = 0; ix < fres; ix++)
        {
            int k = ix / span, m = ix % span;
            float a = coarse[cz, k];
            float b = coarse[cz, Mathf.Min(k + 1, cres - 1)];
            fine[fz, ix] = m == 0 ? a : Mathf.Lerp(a, b, (float)m / span);
        }
    }

    // ---- The carve ----

    struct CarveStamp { public float x, z, bank, depth, halfW, edge; }

    // Bilinear over the in-memory grids at a world point — the
    // PRE-carve reference ground. Shared edges are identical either
    // side, so tile choice at a boundary doesn't matter.
    static float FieldSample(float[,][,] grids, int n, float tile, float x, float z)
    {
        int c = Mathf.Clamp(Mathf.FloorToInt(x / tile), 0, n - 1);
        int r = Mathf.Clamp(Mathf.FloorToInt(z / tile), 0, n - 1);
        float[,] g = grids[r, c];
        int res = g.GetLength(0);
        float fx = Mathf.Clamp((x - c * tile) / tile, 0f, 1f) * (res - 1);
        float fz = Mathf.Clamp((z - r * tile) / tile, 0f, 1f) * (res - 1);
        int x0 = Mathf.Min((int)fx, res - 2), z0 = Mathf.Min((int)fz, res - 2);
        float tx = fx - x0, tz = fz - z0;
        return Mathf.Lerp(
            Mathf.Lerp(g[z0, x0], g[z0, x0 + 1], tx),
            Mathf.Lerp(g[z0 + 1, x0], g[z0 + 1, x0 + 1], tx), tz);
    }

    // How far the real channel may stray off the OSM centerline (a
    // generalized map line) before we stop believing the dip is ours.
    const float ThalwegStray = 8f;
    // A corridor minimum must sit at least this far below its own banks
    // to count as a real channel worth snapping to — anything shallower
    // is terrain noise and the carve stays on the map line.
    const float ThalwegMinIncision = 0.3f;

    void CarveIntoGrids(float[,][,] grids, FeaSet set, MarchesDressing dressing,
        int n, float tile)
    {
        if (set == null || dressing == null)
            return;
        if (set.waters == null || set.waters.Length == 0)
            return;

        // Pass 1: channel refs — the min of the two samples JUST
        // OUTSIDE the channel (close-in bounds cross-slope drag; the
        // far-out version gashed hillsides — Docs/Rivers.md). The
        // CENTERLINE is deliberately NOT in the min anymore: LIDAR
        // ground carries real incised channels, and a ref pulled down
        // to the real bed made the carve re-dig what already exists.
        // Against edge-level refs, an already-deep channel is left
        // alone (the cut is min(current, target)) and a washed-out
        // one is restored to depth.
        //
        // THALWEG SNAP (2026-08-15): the OSM line and the LIDAR's real
        // channel meander independently by ±5–10m; carving on the map
        // line where they diverge cut a braided SECOND trench beside
        // the real one. Each station scans across the corridor for the
        // lowest existing ground, and if that low sits a real channel's
        // depth below its own banks, the station snaps sideways onto it
        // — smoothed along the run so the carve doesn't jitter. The
        // snapped stations are SAVED with the refs so the water ribbons
        // and the bed splat follow the channel as-built, not the map.
        var refSet = new RiverRefs { waters = new RiverRefs.Row[set.waters.Length] };
        var stamps = new List<CarveStamp>();
        for (int fi = 0; fi < set.waters.Length; fi++)
        {
            Fea f = set.waters[fi];
            float w = Mathf.Max(1f, f.w);
            float depth = Mathf.Clamp(w * dressing.carveDepthPerWidth,
                dressing.carveDepthMin, dressing.carveDepthMax);
            float halfW = w * 0.5f;
            float edge = halfW + Mathf.Max(w * dressing.bankWidthFactor,
                dressing.bankWidthMin);
            List<Vector3> line = MarchesDressing.Resample(
                MarchesDressing.Unflatten(f.p), CarveStep);
            int count = line.Count;
            var perps = new Vector2[count];
            var offs = new float[count];
            float range = halfW + ThalwegStray;
            for (int i = 0; i < count; i++)
            {
                Vector3 dir = line[Mathf.Min(i + 1, count - 1)]
                    - line[Mathf.Max(i - 1, 0)];
                dir.y = 0f;
                if (dir.sqrMagnitude < 1e-6f)
                    dir = Vector3.forward;
                dir.Normalize();
                perps[i] = new Vector2(-dir.z, dir.x);
                float bestO = 0f, bestH = float.MaxValue;
                for (float o = -range; o <= range; o += 1f)
                {
                    float h = FieldSample(grids, n, tile,
                        line[i].x + perps[i].x * o, line[i].z + perps[i].y * o);
                    if (h < bestH)
                    {
                        bestH = h;
                        bestO = o;
                    }
                }
                float bankL = FieldSample(grids, n, tile,
                    line[i].x + perps[i].x * (bestO - (halfW + 1.5f)),
                    line[i].z + perps[i].y * (bestO - (halfW + 1.5f)));
                float bankR = FieldSample(grids, n, tile,
                    line[i].x + perps[i].x * (bestO + (halfW + 1.5f)),
                    line[i].z + perps[i].y * (bestO + (halfW + 1.5f)));
                offs[i] = Mathf.Min(bankL, bankR) - bestH >= ThalwegMinIncision
                    ? bestO : 0f;
            }
            // Two box passes so the snapped centerline stays a river,
            // not a per-station scribble.
            for (int pass = 0; pass < 2; pass++)
                for (int i = 1; i < count - 1; i++)
                    offs[i] = (offs[i - 1] + offs[i] + offs[i + 1]) / 3f;

            var refs = new float[count];
            var pts = new float[count * 2];
            for (int i = 0; i < count; i++)
            {
                float sx = line[i].x + perps[i].x * offs[i];
                float sz = line[i].z + perps[i].y * offs[i];
                pts[i * 2] = sx;
                pts[i * 2 + 1] = sz;
                var perp = perps[i] * (halfW + 1.5f);
                refs[i] = Mathf.Min(
                    FieldSample(grids, n, tile, sx - perp.x, sz - perp.y),
                    FieldSample(grids, n, tile, sx + perp.x, sz + perp.y));
                stamps.Add(new CarveStamp { x = sx, z = sz,
                    bank = refs[i], depth = depth, halfW = halfW, edge = edge });
            }
            refSet.waters[fi] = new RiverRefs.Row { refs = refs, pts = pts };
        }

        // Pass 2: the cut — min(current, ref − depth × profile), in
        // ODN metres directly. Shared edge samples get identical
        // world-space targets on both tiles, so seams hold.
        long moved = 0;
        const float Quantum = WorldFrame.HeightRange / 65535f;
        for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++)
            {
                float[,] g = grids[r, c];
                int res = g.GetLength(0);
                float cell = tile / (res - 1);
                float ox = c * tile, oz = r * tile;
                foreach (CarveStamp s in stamps)
                {
                    if (s.x < ox - s.edge || s.x > ox + tile + s.edge
                        || s.z < oz - s.edge || s.z > oz + tile + s.edge)
                        continue;
                    int x0 = Mathf.Max(0, Mathf.FloorToInt((s.x - s.edge - ox) / cell));
                    int x1 = Mathf.Min(res - 1, Mathf.CeilToInt((s.x + s.edge - ox) / cell));
                    int z0 = Mathf.Max(0, Mathf.FloorToInt((s.z - s.edge - oz) / cell));
                    int z1 = Mathf.Min(res - 1, Mathf.CeilToInt((s.z + s.edge - oz) / cell));
                    for (int iz = z0; iz <= z1; iz++)
                    {
                        float wz = oz + iz * cell;
                        for (int ix = x0; ix <= x1; ix++)
                        {
                            float wx = ox + ix * cell;
                            float dx = wx - s.x, dz = wz - s.z;
                            float dist = Mathf.Sqrt(dx * dx + dz * dz);
                            if (dist >= s.edge)
                                continue;
                            float profile = dist <= s.halfW ? 1f
                                : 1f - Mathf.SmoothStep(0f, 1f,
                                    Mathf.InverseLerp(s.halfW, s.edge, dist));
                            float cand = s.bank - s.depth * profile;
                            if (cand < g[iz, ix] - Quantum)
                            {
                                g[iz, ix] = cand;
                                moved++;
                            }
                        }
                    }
                }
            }

        System.IO.File.WriteAllText(
            System.IO.Path.Combine(System.IO.Path.GetDirectoryName(
                Application.dataPath), RiverRefsPath),
            JsonUtility.ToJson(refSet));
        AssetDatabase.ImportAsset(RiverRefsPath);
        Debug.Log($"MarchesBaker: carved {stamps.Count} channel samples,"
            + $" {moved} heightmap samples lowered; river refs saved.");
    }

    // The two-layer slope paint (TerrainGenerator's rule) — the
    // pre-Dress state; Dress repaints with roads and riverbed on top.
    static void ApplySlopeAlphamap(TerrainData td, int resolution)
    {
        (float slopeStart, float slopeFull) = MarchesDressing.SlopeNumbers();
        int res = Mathf.Max(64, resolution);
        td.alphamapResolution = res;
        var alphas = new float[res, res, 2];
        for (int iz = 0; iz < res; iz++)
            for (int ix = 0; ix < res; ix++)
            {
                float steep = td.GetSteepness(
                    (float)ix / (res - 1), (float)iz / (res - 1));
                float d = Mathf.InverseLerp(slopeStart, slopeFull, steep);
                alphas[iz, ix, 0] = 1f - d;
                alphas[iz, ix, 1] = d;
            }
        td.SetAlphamaps(0, 0, alphas);
    }

    static float RectDistance(Rect r, Vector2 p)
    {
        float dx = Mathf.Max(r.xMin - p.x, 0f, p.x - r.xMax);
        float dy = Mathf.Max(r.yMin - p.y, 0f, p.y - r.yMax);
        return Mathf.Sqrt(dx * dx + dy * dy);
    }

    // ---- The gridded ASC field ----
    // A continuous BNG height field over a folder of Esri ASCII grids:
    // uniform cell size within a source, cell-center registration,
    // heights scaled by unitScale (mm → m for the NRW strips), NODATA
    // → NaN. Cross-file bilinear works because samples are addressed
    // by GLOBAL cell index, each corner routed to whichever file has
    // it — a game-tile edge lying exactly on an ASC boundary reads
    // identical values from both sides (the clamped-within-one-file
    // version could not promise that). Overlapping flight strips
    // merge: first valid value across a cell's files wins.
    class GriddedField
    {
        class Src
        {
            public double xll, yll;
            public int ncols, nrows;
            public float[] h; // metres; NaN = NODATA
        }

        double cell;          // sample spacing (uniform per source)
        double tileW, tileH;  // file extent (uniform per source)
        readonly Dictionary<(int, int), List<Src>> files
            = new Dictionary<(int, int), List<Src>>();
        public int TileCount { get; private set; }

        public static GriddedField Load(string folder, float unitScale,
            double minE, double minN, double maxE, double maxN)
        {
            var f = new GriddedField();
            if (!System.IO.Directory.Exists(folder))
            {
                Debug.LogWarning($"MarchesBaker: no ASC folder at {folder}");
                return f;
            }
            // Window in WORLD terms → BNG for the intersect test.
            double e0 = WorldFrame.OriginEasting + minE;
            double n0 = WorldFrame.OriginNorthing + minN;
            double e1 = WorldFrame.OriginEasting + maxE;
            double n1 = WorldFrame.OriginNorthing + maxN;
            foreach (string path in System.IO.Directory.GetFiles(
                folder, "*.asc", System.IO.SearchOption.AllDirectories))
            {
                using var reader = new System.IO.StreamReader(path);
                double xll = 0, yll = 0, cs = 0;
                int ncols = 0, nrows = 0;
                float nodata = -9999f;
                // Header lines until the first data row (OS T50 has
                // extra header keys; stop at the first numeric row).
                string line;
                string[] first = null;
                while ((line = reader.ReadLine()) != null)
                {
                    string[] kv = line.Split((char[])null,
                        System.StringSplitOptions.RemoveEmptyEntries);
                    if (kv.Length == 0)
                        continue;
                    switch (kv[0].ToLowerInvariant())
                    {
                        case "ncols": ncols = int.Parse(kv[1]); continue;
                        case "nrows": nrows = int.Parse(kv[1]); continue;
                        case "xllcorner": xll = double.Parse(kv[1]); continue;
                        case "yllcorner": yll = double.Parse(kv[1]); continue;
                        case "cellsize": cs = double.Parse(kv[1]); continue;
                        case "nodata_value": nodata = float.Parse(kv[1]); continue;
                    }
                    first = kv;
                    break;
                }
                if (ncols == 0 || nrows == 0 || cs <= 0)
                    continue;
                if (xll >= e1 || xll + ncols * cs <= e0
                    || yll >= n1 || yll + nrows * cs <= n0)
                    continue;
                if (f.cell == 0)
                {
                    f.cell = cs;
                    f.tileW = ncols * cs;
                    f.tileH = nrows * cs;
                }
                else if (System.Math.Abs(cs - f.cell) > 1e-6)
                {
                    Debug.LogWarning($"MarchesBaker: {path} cell {cs} ≠ {f.cell} — skipped.");
                    continue;
                }
                var s = new Src { xll = xll, yll = yll, ncols = ncols, nrows = nrows };
                s.h = new float[ncols * nrows];
                int i = 0;
                // The first data row may already be in `first`.
                while (true)
                {
                    string[] vals = first ?? (reader.ReadLine()?.Split((char[])null,
                        System.StringSplitOptions.RemoveEmptyEntries));
                    first = null;
                    if (vals == null)
                        break;
                    foreach (string v in vals)
                    {
                        if (i >= s.h.Length)
                            break;
                        float raw = float.Parse(v);
                        s.h[i++] = raw == nodata ? float.NaN : raw * unitScale;
                    }
                    if (i >= s.h.Length)
                        break;
                }
                var key = ((int)System.Math.Floor(xll / f.tileW),
                    (int)System.Math.Floor(yll / f.tileH));
                if (!f.files.TryGetValue(key, out List<Src> list))
                    f.files[key] = list = new List<Src>();
                list.Add(s);
                f.TileCount++;
            }
            return f;
        }

        // Global cell (gx, gy): center at easting = (gx + 0.5) × cell,
        // northing = (gy + 0.5) × cell. Rows in the files start at the
        // NORTH edge. First valid value across the cell's files wins.
        float CellValue(long gx, long gy)
        {
            if (cell == 0)
                return float.NaN;
            double e = (gx + 0.5) * cell, n = (gy + 0.5) * cell;
            var key = ((int)System.Math.Floor(e / tileW),
                (int)System.Math.Floor(n / tileH));
            if (!files.TryGetValue(key, out List<Src> list))
                return float.NaN;
            foreach (Src s in list)
            {
                int col = (int)System.Math.Floor((e - s.xll) / cell);
                int rowS = (int)System.Math.Floor((n - s.yll) / cell);
                if (col < 0 || col >= s.ncols || rowS < 0 || rowS >= s.nrows)
                    continue;
                float v = s.h[(s.nrows - 1 - rowS) * s.ncols + col];
                if (!float.IsNaN(v))
                    return v;
            }
            return float.NaN;
        }

        public float Bilinear(double e, double n)
        {
            if (cell == 0)
                return float.NaN;
            double fx = e / cell - 0.5, fy = n / cell - 0.5;
            long x0 = (long)System.Math.Floor(fx), y0 = (long)System.Math.Floor(fy);
            float tx = (float)(fx - x0), ty = (float)(fy - y0);
            float h00 = CellValue(x0, y0), h10 = CellValue(x0 + 1, y0);
            float h01 = CellValue(x0, y0 + 1), h11 = CellValue(x0 + 1, y0 + 1);
            if (float.IsNaN(h00) || float.IsNaN(h10)
                || float.IsNaN(h01) || float.IsNaN(h11))
                return float.NaN;
            return Mathf.Lerp(Mathf.Lerp(h00, h10, tx),
                Mathf.Lerp(h01, h11, tx), ty);
        }

        // Mean of the source cells whose centers fall inside the box —
        // the anti-alias downsample for base tiles. A pure function of
        // the query point, so both tiles at a seam compute identical
        // edge values. NaN when less than minFrac of the box is valid.
        public float BoxAverage(double e, double n, float half, float minFrac)
        {
            if (cell == 0)
                return float.NaN;
            long x0 = (long)System.Math.Ceiling((e - half) / cell - 0.5);
            long x1 = (long)System.Math.Floor((e + half) / cell - 0.5);
            long y0 = (long)System.Math.Ceiling((n - half) / cell - 0.5);
            long y1 = (long)System.Math.Floor((n + half) / cell - 0.5);
            double sum = 0;
            int valid = 0, total = 0;
            for (long gy = y0; gy <= y1; gy++)
                for (long gx = x0; gx <= x1; gx++)
                {
                    total++;
                    float v = CellValue(gx, gy);
                    if (float.IsNaN(v))
                        continue;
                    sum += v;
                    valid++;
                }
            if (total == 0 || valid < total * minFrac)
                return float.NaN;
            return (float)(sum / valid);
        }
    }
#endif
}
