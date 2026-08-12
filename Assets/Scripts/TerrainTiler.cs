using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

// Splits a single Unity terrain into an N×N grid of terrain tiles
// (Docs/TerrainTiles.md): one terrain caps at a 4097 heightmap / 4096
// alphamap, which over 15km is ~3.7m a sample — roads painted into the
// ground and rivers carved into it need a grid whose tiles can each
// carry their own resolution. An editor bake like TerrainGenerator and
// MarchesDressing, never a runtime system: Split reads the CURRENT
// terrain's data, emits child tiles with per-tile TerrainData assets,
// and deactivates the source in place (kept as the re-bake source);
// Clear Tiles reverses the whole thing.
//
// Tiles share edge samples by construction — tile [r,c] takes source
// sample range [k*r .. k*r+k] where k = (res-1)/N — so adjacent tiles
// carry IDENTICAL edge rows and seams cannot open. Auto-connect (one
// grouping ID) stitches their LODs. Tiles are named "<source> [r,c]";
// Ground.BaseName strips the suffix so old saves keep matching.
[RequireComponent(typeof(Terrain))]
public class TerrainTiler : MonoBehaviour
{
    public int tilesPerSide = 4;
    // Per-tile alphamap resolution — the splat dial. 1024 over a 3.77km
    // tile is 3.68m/px, an 8× improvement on the single terrain's 29.5;
    // the road-paint pass later raises individual tiles, not all.
    public int tileAlphamapResolution = 1024;
    // Tiles of one grid auto-connect their LODs through this ID.
    public int groupingID = 800;

    const string TileFolder = "Assets/Terrain/Tiles";

    [ContextMenu("Split Into Tiles")]
    public void Split()
    {
#if UNITY_EDITOR
        Terrain src = GetComponent<Terrain>();
        TerrainData d = src.terrainData;
        int res = d.heightmapResolution;
        int n = Mathf.Max(1, tilesPerSide);
        if ((res - 1) % n != 0)
        {
            Debug.LogError($"TerrainTiler: heightmap {res} doesn't split into {n}×{n}"
                + $" (res−1 must be divisible by {n}).");
            return;
        }
        ClearTiles();

        int k = (res - 1) / n;          // cells per tile side
        int tres = k + 1;               // samples per tile side
        float[,] all = d.GetHeights(0, 0, res, res);
        Vector3 size = d.size;
        var tileSize = new Vector3(size.x / n, size.y, size.z / n);
        Vector3 srcPos = src.transform.position;

        var gen = GetComponent<TerrainGenerator>();
        float slopeStart = gen != null ? gen.dirtSlopeStart : 24f;
        float slopeFull = gen != null ? gen.dirtSlopeFull : 38f;

        if (!AssetDatabase.IsValidFolder("Assets/Terrain"))
            AssetDatabase.CreateFolder("Assets", "Terrain");
        if (!AssetDatabase.IsValidFolder(TileFolder))
            AssetDatabase.CreateFolder("Assets/Terrain", "Tiles");

        var root = new GameObject(src.name + " Tiles").transform;
        root.SetParent(transform.parent, false);
        root.position = srcPos;

        for (int r = 0; r < n; r++)
        {
            for (int c = 0; c < n; c++)
            {
                var td = new TerrainData();
                td.heightmapResolution = tres;
                td.size = tileSize;
                var h = new float[tres, tres];
                for (int i = 0; i < tres; i++)
                    for (int j = 0; j < tres; j++)
                        h[i, j] = all[r * k + i, c * k + j];
                td.SetHeights(0, 0, h);
                td.terrainLayers = d.terrainLayers;
                ApplyAlphamaps(td, d, r, c, n, tileAlphamapResolution,
                    slopeStart, slopeFull);

                string path = $"{TileFolder}/{src.name} [{r},{c}].asset";
                AssetDatabase.DeleteAsset(path);
                AssetDatabase.CreateAsset(td, path);

                GameObject go = Terrain.CreateTerrainGameObject(td);
                go.name = $"{src.name} [{r},{c}]";
                go.transform.SetParent(root, false);
                go.transform.localPosition = new Vector3(c * tileSize.x, 0f, r * tileSize.z);
                var t = go.GetComponent<Terrain>();
                t.materialTemplate = src.materialTemplate;
                t.heightmapPixelError = src.heightmapPixelError;
                t.basemapDistance = src.basemapDistance;
                t.drawInstanced = src.drawInstanced;
                t.treeDistance = src.treeDistance;
                t.treeBillboardDistance = src.treeBillboardDistance;
                t.treeCrossFadeLength = src.treeCrossFadeLength;
                t.treeMaximumFullLODCount = src.treeMaximumFullLODCount;
                t.groupingID = groupingID;
                t.allowAutoConnect = true;
            }
        }

        src.gameObject.SetActive(false);
        if (d.treeInstanceCount > 0)
            Debug.LogWarning("TerrainTiler: source terrain carried tree instances —"
                + " they do not split. Re-run the dressing to scatter onto the tiles.");
        Debug.Log($"TerrainTiler: split '{src.name}' into {n}×{n} tiles of"
            + $" {tres}² heightmap / {tileAlphamapResolution}² alphamap"
            + $" ({tileSize.x:F0}m each).");
        AssetDatabase.SaveAssets();
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(gameObject.scene);
#else
        Debug.LogError("TerrainTiler: Split is an editor bake.");
#endif
    }

    [ContextMenu("Clear Tiles")]
    public void ClearTiles()
    {
        Transform root = transform.parent != null
            ? transform.parent.Find(GetComponent<Terrain>().name + " Tiles")
            : null;
        if (root == null)
        {
            GameObject found = GameObject.Find(GetComponent<Terrain>().name + " Tiles");
            root = found != null ? found.transform : null;
        }
        if (root != null)
        {
            if (Application.isPlaying) Destroy(root.gameObject);
            else DestroyImmediate(root.gameObject);
        }
        gameObject.SetActive(true);
#if UNITY_EDITOR
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(gameObject.scene);
#endif
    }

    // Two layers means the slope-derived grass/dirt convention
    // (TerrainGenerator's rule; TerrainPads.RefreshSurface repaints by
    // it) — recompute at the tile's own resolution so the new pixels
    // are real, not upscaled. Anything else is resampled bilinearly
    // from the source's paint.
    static void ApplyAlphamaps(TerrainData td, TerrainData src,
        int r, int c, int n, int resolution, float slopeStart, float slopeFull)
    {
        int layers = src.alphamapLayers;
        if (layers == 0)
            return;
        int res = Mathf.Max(16, resolution);
        td.alphamapResolution = res;
        var alphas = new float[res, res, layers];
        float[,,] srcAlphas = layers == 2 ? null
            : src.GetAlphamaps(0, 0, src.alphamapWidth, src.alphamapHeight);
        for (int iz = 0; iz < res; iz++)
        {
            for (int ix = 0; ix < res; ix++)
            {
                if (layers == 2)
                {
                    float steep = td.GetSteepness(
                        (float)ix / (res - 1), (float)iz / (res - 1));
                    float dirt = Mathf.InverseLerp(slopeStart, slopeFull, steep);
                    alphas[iz, ix, 0] = 1f - dirt;
                    alphas[iz, ix, 1] = dirt;
                }
                else
                {
                    // Tile-normalized → source-normalized → source pixels.
                    float u = (c + (float)ix / (res - 1)) / n;
                    float v = (r + (float)iz / (res - 1)) / n;
                    int sx = Mathf.Clamp(Mathf.RoundToInt(u * (src.alphamapWidth - 1)),
                        0, src.alphamapWidth - 1);
                    int sz = Mathf.Clamp(Mathf.RoundToInt(v * (src.alphamapHeight - 1)),
                        0, src.alphamapHeight - 1);
                    for (int l = 0; l < layers; l++)
                        alphas[iz, ix, l] = srcAlphas[sz, sx, l];
                }
            }
        }
        td.SetAlphamaps(0, 0, alphas);
    }
}
