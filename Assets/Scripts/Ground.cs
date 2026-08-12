using UnityEngine;

// The ONLY way scripts read the ground (Docs/TerrainTiles.md): the world
// may be one Unity terrain (Spiš) or a grid of tiles (Marches), and
// Terrain.activeTerrain picks arbitrarily among tiles while SampleHeight
// silently CLAMPS outside its own tile — a 15km world quietly reporting
// the wrong hill. Height questions route through here by world XZ.
// Cursor raycasts stay type-based (`hit.collider is TerrainCollider`,
// every tile's collider passes) and never needed the facade.
public static class Ground
{
    static Terrain[] tiles = System.Array.Empty<Terrain>();
    static Terrain last;          // gestures sample one tile repeatedly
    static int refreshFrame = -1;

    // Terrain.activeTerrains allocates per call, so the set is cached and
    // re-read only when a cached tile has died (scene load, bake) — at
    // most once a frame, so a point off the map can't re-scan per sample.
    public static Terrain[] Tiles()
    {
        bool stale = tiles.Length == 0;
        if (!stale)
            foreach (Terrain t in tiles)
                if (t == null || !t.isActiveAndEnabled) { stale = true; break; }
        if (stale && refreshFrame != Time.frameCount)
        {
            tiles = Terrain.activeTerrains;
            last = null;
            refreshFrame = Time.frameCount;
        }
        return tiles;
    }

    public static bool Any => Tiles().Length > 0;

    // The Terrain whose XZ bounds contain the point; outside every tile,
    // the nearest tile (whose SampleHeight then clamps — the old
    // single-terrain edge behavior, preserved deliberately).
    public static Terrain TileAt(float x, float z)
    {
        Terrain[] ts = Tiles();
        if (ts.Length == 0)
            return null;
        if (last != null && Contains(last, x, z))
            return last;
        foreach (Terrain t in ts)
            if (Contains(t, x, z))
            {
                last = t;
                return t;
            }
        Terrain best = null;
        float bestD = float.MaxValue;
        foreach (Terrain t in ts)
        {
            float d = ClampedDistSq(t, x, z);
            if (d < bestD)
            {
                bestD = d;
                best = t;
            }
        }
        return best;
    }

    public static float HeightAt(float x, float z)
    {
        Terrain t = TileAt(x, z);
        if (t == null)
            return 0f;
        return t.transform.position.y + t.SampleHeight(new Vector3(x, 0f, z));
    }

    public static float HeightAt(Vector3 p) => HeightAt(p.x, p.z);

    // World-XZ union of all tiles — what "the map" means to the survey.
    public static Rect Bounds()
    {
        Terrain[] ts = Tiles();
        if (ts.Length == 0)
            return new Rect(0f, 0f, 0f, 0f);
        float minX = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxZ = float.MinValue;
        foreach (Terrain t in ts)
        {
            Vector3 o = t.transform.position;
            Vector3 s = t.terrainData.size;
            minX = Mathf.Min(minX, o.x);
            minZ = Mathf.Min(minZ, o.z);
            maxX = Mathf.Max(maxX, o.x + s.x);
            maxZ = Mathf.Max(maxZ, o.z + s.z);
        }
        return new Rect(minX, minZ, maxX - minX, maxZ - minZ);
    }

    // The tile name with its " [r,c]" suffix stripped — saves made on the
    // pre-split terrain keep matching CastleSave's terrain-name check.
    public static string BaseName
    {
        get
        {
            Terrain[] ts = Tiles();
            if (ts.Length == 0)
                return "";
            string name = ts[0].name;
            int i = name.IndexOf(" [");
            return i < 0 ? name : name.Substring(0, i);
        }
    }

    static bool Contains(Terrain t, float x, float z)
    {
        Vector3 o = t.transform.position;
        Vector3 s = t.terrainData.size;
        return x >= o.x && x <= o.x + s.x && z >= o.z && z <= o.z + s.z;
    }

    static float ClampedDistSq(Terrain t, float x, float z)
    {
        Vector3 o = t.transform.position;
        Vector3 s = t.terrainData.size;
        float dx = x - Mathf.Clamp(x, o.x, o.x + s.x);
        float dz = z - Mathf.Clamp(z, o.z, o.z + s.z);
        return dx * dx + dz * dz;
    }
}
