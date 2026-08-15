using System.Collections.Generic;
using UnityEngine;

// Earthworks, governed by one rule — THE DEVIATION LAW: the ground may
// never differ from the NATURAL hill (the pristine heightmap, cached at
// first use) by more than MaxDeviation (3m, user-tuned 2026-08-07). Every
// operation clamps against pristine, not against the current state, so
// repeated carving cannot creep past the law. The hill's strategic shape
// is immutable; only its surface is negotiable. (Design call 2026-08-07:
// shaping is a small bounded facility for seating buildings and benching
// paths, never a sculpting feature. Labor pricing arrives with the
// economy.)
//
// Three operations, all stored and replayed by CastleSave IN ORDER onto
// pristine ground on load (bench endpoint heights are captured at
// commit, so replay never re-asks the ground anything):
//   Pad     — polygon cut AND filled to one level (legacy: rooms once
//             padded their footprints; kept only so old saves replay)
//   Bench   — a graded walkable band from a to b (the path tool)
//   Restore — a band returned to the natural ground (the undo of both)
//
// The ground may be ONE terrain (Spiš) or a GRID OF TILES (the Marches —
// Docs/TerrainTiles.md): all state here is PER-TILE, and every operation
// runs its sample loop on each tile its bounds touch. Ops are stored in
// world space, so nothing about the save changes with the tiling; a
// bench crossing a seam writes both tiles' shared edge samples to the
// same world target from the same pristine height, so seams stay
// consistent by construction. Edits go to a runtime COPY of each tile's
// TerrainData (play-mode writes to the authored asset silently persist —
// it is never touched). Ground under EXISTING terrain-storey walls is
// load-bearing (footings never resample), so live ops stop short of them
// (KeepOut). On load nothing exists yet when ops replay, and every wall
// re-foots afterwards.
public static class TerrainPads
{
    public const int KindPad = 0;
    public const int KindBench = 1;
    public const int KindRestore = 2;

    public class Op
    {
        public int kind;
        public List<Vector3> poly;   // pad: ring. bench/restore: [a, b].
        public float level;          // pad only
        public float width;          // bench/restore only
        public float ya, yb;         // bench only: end heights, captured
    }

    public static readonly List<Op> All = new List<Op>();

    // Bumped on every heightmap write (reshape, preview revert, reset)
    // so cached drapes — the survey overlay — can tell whether the
    // ground moved under them without resampling it. Cheap and
    // monotonic; a spurious bump costs one rebuild, a missed one shows
    // a stale map, so every SetHeights path increments it.
    public static int EditVersion { get; private set; }

    // The law. One number; everything above enforces it.
    public const float MaxDeviation = 3f;

    // How far beyond a pad polygon the flattening reaches: the masonry
    // band plus a small bench outside the outer face.
    const float Margin = 0.9f;

    // Radius around existing walls' centerlines an op won't touch.
    const float KeepOut = 0.8f;

    // Everything a tile of ground carries: its pristine snapshot (what
    // the law measures against), its session clone (what ops write), and
    // the path drag's live-preview snapshot.
    class Tile
    {
        public Terrain terrain;
        public TerrainData runtime;
        public float[,] pristine;
        public float[,,] pristineAlpha;
        public float[,] previewBase;
        public int pvC0, pvR0, pvC1, pvR1;
        public bool previewApplied;
    }

    static readonly List<Tile> tiles = new List<Tile>();

    static Tile StateFor(Terrain terrain)
    {
        if (terrain == null)
            return null;
        tiles.RemoveAll(t => t.terrain == null);
        foreach (Tile t in tiles)
            if (t.terrain == terrain && terrain.terrainData == t.runtime)
                return t;
        // The terrain's data was swapped since (scene reload, re-bake) —
        // any state built on the old clone is dead, not shadowed.
        tiles.RemoveAll(t => t.terrain == terrain);
        TerrainData src = terrain.terrainData;
        int res = src.heightmapResolution;
        var tile = new Tile
        {
            terrain = terrain,
            pristine = src.GetHeights(0, 0, res, res),
            pristineAlpha = src.alphamapLayers > 0
                ? src.GetAlphamaps(0, 0, src.alphamapWidth, src.alphamapHeight)
                : null,
            runtime = Object.Instantiate(src),
        };
        tile.runtime.name = src.name + " (session)";
        terrain.terrainData = tile.runtime;
        TerrainCollider col = terrain.GetComponent<TerrainCollider>();
        if (col != null)
            col.terrainData = tile.runtime;
        tiles.Add(tile);
        return tile;
    }

    static Tile StateAt(float x, float z) => StateFor(Ground.TileAt(x, z));

    // The timber planter's door: a tile's session clone, created on
    // first use exactly like an earthwork's. Anything that WRITES
    // terrain data at runtime (heights, alphas, trees) must come
    // through here — play-mode writes to the authored asset silently
    // persist, and that asset is never touched.
    public static TerrainData SessionData(Terrain terrain)
        => StateFor(terrain).runtime;

    // Reshaped ground changes its slopes, and the surface paint is
    // slope-derived (grass flat, dirt steep — TerrainGenerator's rule).
    // Recompute it over the touched region so a cut face shows worn
    // earth and a flattened pad grows back to grass. Only the grass/dirt
    // SPLIT is slope-derived: painted layers beyond the first two (the
    // road splat, later land-use paint) keep their weight, and grass and
    // dirt share what remains — benching a path under a painted lane
    // must not scrub the lane off it.
    static void RefreshSurface(Tile tile, int hc0, int hr0, int hc1, int hr1)
    {
        int layers = tile.runtime.alphamapLayers;
        if (layers < 2)
            return;
        var gen = Object.FindAnyObjectByType<TerrainGenerator>();
        float slopeStart = gen != null ? gen.dirtSlopeStart : 24f;
        float slopeFull = gen != null ? gen.dirtSlopeFull : 38f;
        int hres = tile.runtime.heightmapResolution;
        int ares = tile.runtime.alphamapWidth;
        int a0 = Mathf.Clamp((hc0 - 1) * (ares - 1) / (hres - 1), 0, ares - 1);
        int a1 = Mathf.Clamp((hc1 + 1) * (ares - 1) / (hres - 1) + 1, 0, ares - 1);
        int b0 = Mathf.Clamp((hr0 - 1) * (ares - 1) / (hres - 1), 0, ares - 1);
        int b1 = Mathf.Clamp((hr1 + 1) * (ares - 1) / (hres - 1) + 1, 0, ares - 1);
        if (a1 <= a0 || b1 <= b0)
            return;
        int w = a1 - a0 + 1, h = b1 - b0 + 1;
        float[,,] existing = layers > 2
            ? tile.runtime.GetAlphamaps(a0, b0, w, h)
            : null;
        var alphas = new float[h, w, layers];
        for (int r = b0; r <= b1; r++)
        {
            for (int c = a0; c <= a1; c++)
            {
                float extra = 0f;
                for (int l = 2; l < layers; l++)
                {
                    float v = existing[r - b0, c - a0, l];
                    alphas[r - b0, c - a0, l] = v;
                    extra += v;
                }
                float steep = tile.runtime.GetSteepness(
                    (float)c / (ares - 1), (float)r / (ares - 1));
                float dirt = Mathf.InverseLerp(slopeStart, slopeFull, steep);
                float rem = Mathf.Clamp01(1f - extra);
                alphas[r - b0, c - a0, 0] = rem * (1f - dirt);
                alphas[r - b0, c - a0, 1] = rem * dirt;
            }
        }
        tile.runtime.SetAlphamaps(a0, b0, alphas);
    }

    // The natural ground's world Y at a point — what the law measures
    // against, whatever has been dug since.
    public static float PristineGroundAt(float x, float z)
    {
        Tile tile = StateAt(x, z);
        if (tile == null)
            return 0f;
        return SampleField(tile.pristine, x, z, tile);
    }

    // Bilinear world-Y sample of a stored heightmap snapshot.
    static float SampleField(float[,] field, float x, float z, Tile tile)
    {
        int res = tile.runtime.heightmapResolution;
        Vector3 size = tile.runtime.size;
        Vector3 origin = tile.terrain.transform.position;
        float fx = Mathf.Clamp((x - origin.x) / size.x * (res - 1), 0, res - 1.001f);
        float fz = Mathf.Clamp((z - origin.z) / size.z * (res - 1), 0, res - 1.001f);
        int c = (int)fx, r = (int)fz;
        float tx = fx - c, tz = fz - r;
        float h = Mathf.Lerp(
            Mathf.Lerp(field[r, c], field[r, c + 1], tx),
            Mathf.Lerp(field[r + 1, c], field[r + 1, c + 1], tx), tz);
        return origin.y + h * size.y;
    }

    // No live caller pads anymore (rooms stopped moving earth) — this
    // stays as the REPLAY door for kind-0 ops in older saves.
    public static void Flatten(List<Vector3> polygon, float level)
    {
        if (polygon == null || polygon.Count < 3)
            return;
        bool touched = Reshape(
            PolyBounds(polygon, Margin),
            w => InPad(w, polygon),
            w => level);
        if (touched)
            All.Add(new Op { kind = KindPad, poly = new List<Vector3>(polygon), level = level });
    }

    public static void Bench(Vector3 a, Vector3 b, float width, float ya, float yb)
    {
        float len2 = FlatLen(a, b);
        if (len2 < 0.5f)
            return;
        bool touched = Reshape(
            SegBounds(a, b, width * 0.5f),
            w => SegmentDistXZ(w, a, b) <= width * 0.5f,
            w => Mathf.Lerp(ya, yb, SegmentT(w, a, b)));
        if (touched)
            All.Add(new Op { kind = KindBench, poly = new List<Vector3> { a, b },
                width = width, ya = ya, yb = yb });
    }

    public static void RestoreLine(Vector3 a, Vector3 b, float width)
    {
        if (FlatLen(a, b) < 0.5f)
            return;
        bool touched = Reshape(
            SegBounds(a, b, width * 0.5f),
            w => SegmentDistXZ(w, a, b) <= width * 0.5f,
            w => PristineGroundAt(w.x, w.z));
        if (touched)
            All.Add(new Op { kind = KindRestore, poly = new List<Vector3> { a, b },
                width = width });
    }

    // ------------------------------------------------------------------
    // Live preview: the path drag deforms the REAL terrain as it moves —
    // the terrain is the ghost — and nothing becomes an Op until the
    // mouse releases. The first time the drag touches a TILE, that
    // tile's current (post-ops) heightmap is snapshotted; each call
    // reverts the last previewed region from those snapshots, then
    // applies the candidate bench WITHOUT recording it. A commit is
    // CancelPreview (put the snapshots back) followed by the ordinary
    // Bench() — the same door a direct commit uses, so what previews is
    // exactly what commits.
    // ------------------------------------------------------------------

    public static void PreviewBench(Vector3 a, Vector3 b, float width, float ya, float yb)
    {
        if (!Ground.Any)
            return;
        RevertPreviewAll();
        if (FlatLen(a, b) < 0.5f)
            return;
        var bounds = SegBounds(a, b, width * 0.5f);
        List<Vector3[]> guarded = GuardedLines();
        foreach (Terrain terrain in Ground.Tiles())
        {
            if (!TileTouches(terrain, bounds))
                continue;
            Tile tile = StateFor(terrain);
            if (tile.previewBase == null)
            {
                int res = tile.runtime.heightmapResolution;
                tile.previewBase = tile.runtime.GetHeights(0, 0, res, res);
            }
            tile.previewApplied = ReshapeTile(tile, bounds,
                w => SegmentDistXZ(w, a, b) <= width * 0.5f,
                w => Mathf.Lerp(ya, yb, SegmentT(w, a, b)),
                guarded,
                out tile.pvC0, out tile.pvR0, out tile.pvC1, out tile.pvR1);
        }
    }

    // Ground as it stood before the preview deformed it — what a drag's
    // end heights sample, so the bench never feeds back on itself.
    public static float PrePreviewGroundAt(float x, float z)
    {
        Tile tile = StateAt(x, z);
        if (tile == null)
            return 0f;
        if (tile.previewBase == null)
            return tile.terrain.transform.position.y
                + tile.terrain.SampleHeight(new Vector3(x, 0f, z));
        return SampleField(tile.previewBase, x, z, tile);
    }

    public static void CancelPreview()
    {
        RevertPreviewAll();
        foreach (Tile tile in tiles)
            tile.previewBase = null;
    }

    static void RevertPreviewAll()
    {
        foreach (Tile tile in tiles)
        {
            if (!tile.previewApplied || tile.previewBase == null)
                continue;
            int w = tile.pvC1 - tile.pvC0 + 1, h = tile.pvR1 - tile.pvR0 + 1;
            var patch = new float[h, w];
            for (int r = 0; r < h; r++)
                for (int c = 0; c < w; c++)
                    patch[r, c] = tile.previewBase[tile.pvR0 + r, tile.pvC0 + c];
            tile.runtime.SetHeights(tile.pvC0, tile.pvR0, patch);
            RefreshSurface(tile, tile.pvC0, tile.pvR0, tile.pvC1, tile.pvR1);
            tile.previewApplied = false;
            EditVersion++;
        }
    }

    public static void Replay(Op op)
    {
        if (op == null || op.poly == null)
            return;
        if (op.kind == KindPad)
            Flatten(op.poly, op.level);
        else if (op.kind == KindBench && op.poly.Count >= 2)
            Bench(op.poly[0], op.poly[1], op.width, op.ya, op.yb);
        else if (op.kind == KindRestore && op.poly.Count >= 2)
            RestoreLine(op.poly[0], op.poly[1], op.width);
    }

    // Load support: pristine ground back first, then the saved ops
    // reapply through Replay — the same doors a commit uses.
    public static void ResetToPristine()
    {
        All.Clear();
        EditVersion++;
        foreach (Tile tile in tiles)
        {
            // A load mid-drag would otherwise revert onto stale heights.
            tile.previewBase = null;
            tile.previewApplied = false;
            if (tile.runtime != null && tile.pristine != null)
                tile.runtime.SetHeights(0, 0, tile.pristine);
            if (tile.runtime != null && tile.pristineAlpha != null)
                tile.runtime.SetAlphamaps(0, 0, tile.pristineAlpha);
        }
    }

    // ------------------------------------------------------------------
    // The one sample loop every operation runs through, once per tile
    // the op's bounds touch. The law is enforced HERE — per sample,
    // against that tile's pristine — so no caller can dig past it,
    // whatever level it asks for.
    // ------------------------------------------------------------------
    static bool Reshape((float minX, float maxX, float minZ, float maxZ) bounds,
        System.Func<Vector3, bool> covers, System.Func<Vector3, float> levelAt)
    {
        if (!Ground.Any)
            return false;
        List<Vector3[]> guarded = GuardedLines();
        bool touched = false;
        foreach (Terrain terrain in Ground.Tiles())
        {
            if (!TileTouches(terrain, bounds))
                continue;
            Tile tile = StateFor(terrain);
            touched |= ReshapeTile(tile, bounds, covers, levelAt, guarded,
                out _, out _, out _, out _);
        }
        if (touched)
            EditVersion++;
        return touched;
    }

    static bool ReshapeTile(Tile tile,
        (float minX, float maxX, float minZ, float maxZ) bounds,
        System.Func<Vector3, bool> covers, System.Func<Vector3, float> levelAt,
        List<Vector3[]> guarded,
        out int oc0, out int or0, out int oc1, out int or1)
    {
        oc0 = or0 = oc1 = or1 = 0;
        int res = tile.runtime.heightmapResolution;
        Vector3 size = tile.runtime.size;
        Vector3 origin = tile.terrain.transform.position;
        int c0 = Mathf.Clamp(Mathf.FloorToInt((bounds.minX - origin.x) / size.x * (res - 1)), 0, res - 1);
        int c1 = Mathf.Clamp(Mathf.CeilToInt((bounds.maxX - origin.x) / size.x * (res - 1)), 0, res - 1);
        int r0 = Mathf.Clamp(Mathf.FloorToInt((bounds.minZ - origin.z) / size.z * (res - 1)), 0, res - 1);
        int r1 = Mathf.Clamp(Mathf.CeilToInt((bounds.maxZ - origin.z) / size.z * (res - 1)), 0, res - 1);
        if (c1 <= c0 || r1 <= r0)
            return false;
        oc0 = c0; or0 = r0; oc1 = c1; or1 = r1;

        float[,] heights = tile.runtime.GetHeights(c0, r0, c1 - c0 + 1, r1 - r0 + 1);
        for (int r = r0; r <= r1; r++)
        {
            for (int c = c0; c <= c1; c++)
            {
                var w = new Vector3(
                    origin.x + (float)c / (res - 1) * size.x, 0f,
                    origin.z + (float)r / (res - 1) * size.z);
                if (!covers(w))
                    continue;
                bool held = false;
                foreach (Vector3[] line in guarded)
                    if (NearPolyline(w, line, KeepOut)) { held = true; break; }
                if (held)
                    continue;
                float natural = origin.y + tile.pristine[r, c] * size.y;
                float target = Mathf.Clamp(levelAt(w),
                    natural - MaxDeviation, natural + MaxDeviation);
                heights[r - r0, c - c0] = Mathf.Clamp01((target - origin.y) / size.y);
            }
        }
        tile.runtime.SetHeights(c0, r0, heights);
        RefreshSurface(tile, c0, r0, c1, r1);
        return true;
    }

    // Ground under existing terrain-storey walls is load-bearing; every
    // op skips samples near their centerlines, whichever tile they're on.
    static List<Vector3[]> GuardedLines()
    {
        var guarded = new List<Vector3[]>();
        foreach (WallEdge e in WallEdge.All)
        {
            if (e == null || !float.IsNegativeInfinity(e.baseY))
                continue;
            var pts = new Vector3[9];
            for (int i = 0; i < 9; i++)
            {
                float t = i / 8f, u = 1f - t;
                pts[i] = u * u * e.A + 2f * u * t * e.control + t * t * e.B;
            }
            guarded.Add(pts);
        }
        return guarded;
    }

    static bool TileTouches(Terrain terrain,
        (float minX, float maxX, float minZ, float maxZ) bounds)
    {
        Vector3 o = terrain.transform.position;
        Vector3 s = terrain.terrainData.size;
        return bounds.maxX >= o.x && bounds.minX <= o.x + s.x
            && bounds.maxZ >= o.z && bounds.minZ <= o.z + s.z;
    }

    static (float, float, float, float) PolyBounds(List<Vector3> poly, float pad)
    {
        float minX = float.MaxValue, maxX = float.MinValue;
        float minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (Vector3 p in poly)
        {
            minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
            minZ = Mathf.Min(minZ, p.z); maxZ = Mathf.Max(maxZ, p.z);
        }
        return (minX - pad, maxX + pad, minZ - pad, maxZ + pad);
    }

    static (float, float, float, float) SegBounds(Vector3 a, Vector3 b, float pad)
    {
        return (Mathf.Min(a.x, b.x) - pad, Mathf.Max(a.x, b.x) + pad,
                Mathf.Min(a.z, b.z) - pad, Mathf.Max(a.z, b.z) + pad);
    }

    // ~1m grid of points covering a polygon's interior, for law checks.
    static IEnumerable<Vector3> SampleGrid(List<Vector3> poly)
    {
        (float minX, float maxX, float minZ, float maxZ) = PolyBounds(poly, 0f);
        for (float x = minX; x <= maxX; x += 1f)
            for (float z = minZ; z <= maxZ; z += 1f)
            {
                var p = new Vector3(x, 0f, z);
                if (InPad(p, poly))
                    yield return p;
            }
    }

    // Inside the polygon, or within Margin of its boundary.
    static bool InPad(Vector3 p, List<Vector3> poly)
    {
        bool inside = false;
        int n = poly.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            Vector3 a = poly[i], b = poly[j];
            if ((a.z > p.z) != (b.z > p.z)
                && p.x < (b.x - a.x) * (p.z - a.z) / (b.z - a.z) + a.x)
                inside = !inside;
        }
        if (inside)
            return true;
        for (int i = 0, j = n - 1; i < n; j = i++)
            if (SegmentDistXZ(p, poly[i], poly[j]) <= Margin)
                return true;
        return false;
    }

    static bool NearPolyline(Vector3 p, Vector3[] line, float radius)
    {
        for (int i = 0; i + 1 < line.Length; i++)
            if (SegmentDistXZ(p, line[i], line[i + 1]) <= radius)
                return true;
        return false;
    }

    static float FlatLen(Vector3 a, Vector3 b)
    {
        float dx = b.x - a.x, dz = b.z - a.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    static float SegmentT(Vector3 p, Vector3 a, Vector3 b)
    {
        float abx = b.x - a.x, abz = b.z - a.z;
        float len2 = abx * abx + abz * abz;
        return len2 < 0.000001f ? 0f
            : Mathf.Clamp01(((p.x - a.x) * abx + (p.z - a.z) * abz) / len2);
    }

    static float SegmentDistXZ(Vector3 p, Vector3 a, Vector3 b)
    {
        float t = SegmentT(p, a, b);
        float dx = p.x - (a.x + (b.x - a.x) * t), dz = p.z - (a.z + (b.z - a.z) * t);
        return Mathf.Sqrt(dx * dx + dz * dz);
    }
}
