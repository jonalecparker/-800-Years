using System.Collections.Generic;
using UnityEngine;

// Procedural town dressing (Docs/VillageHouses.md): houses, a church
// and an inn on each town's Living parcels, derived DETERMINISTICALLY
// from stored facts (parcels + roads + terrain + Estate.Households)
// and never saved — the TimberWoods precedent. The load-bearing idea
// is the STABLE LOT PLAN: PlanLots derives an ordered list of lots
// from the stored facts alone, and population only decides how many
// get buildings. Houses are a PREFIX of the plan, so when households
// grow, new houses appear on lots the plan always reserved for them
// and nothing already built moves — that is the growth contract, and
// swapping graybox boxes for real art later touches only the
// materializers at the bottom of this file.
//
// Scenery doctrine: root "Villages" outside splineParent (the cutaway
// has no business slicing a town), colliders ON (walk mode is real),
// no registry, no save (CastleSave carries the inputs, not the
// output), untouchable by player tools by construction.
public static class VillageHouses
{
    // ---- The plan ----
    // A lot every LotSpacing meters of street, both sides, TWO rows
    // deep, set back from the centerline. Burgage-style: narrow
    // frontage, long axis running back from the road, gable end to
    // the street; the back row is the burgage rears filling in.
    const float LotSpacing = 15f;
    const float Setback = 6.5f;
    const float MinLotGap = 11f;
    // The back row sits a lane behind the front (≥ MinLotGap past the
    // front row's jitter, or it only ever crowds out).
    const float RowPitch = 13.5f;
    // A back-row lot near the castle fills before a front-row lot this
    // much farther out — the user's rule: second-from-the-road beside
    // the castle beats tenth-on-the-road down the lane.
    const float BackRowPenalty = 60f;
    // No house within this of the water's edge (the waterway feature's
    // half width plus this margin) — the carved banks plus a house
    // half-footprint. The TimberWoods keep-out, at house scale.
    const float RiverClear = 10f;
    // A FRONT-row lot on a PRIMARY street within this of the Living-
    // parcel edge counts as frontage (the traced outline is simplified
    // raster steps, so lots near the boundary wobble in and out of
    // strict containment — measured live at 0.2–12.9m before the
    // village became its own flood region). Back rows and lanes stay
    // strict: only the true street front earns the slack.
    const float EdgeTolerance = 14f;
    // Back lanes: each street spawns parallel lanes at this pitch — a
    // burgage plot's depth — so the town fills its blob instead of
    // tracing a line. ≥ both rows + MinLotGap, or adjacent lanes'
    // back rows crowd each other out; 58m also happens to be an
    // honest medieval plot depth. Lanes exist only where their lots
    // land inside the village polygon.
    const float LanePitch = 58f;
    const int LaneCount = 2;
    // No lot inside the castle grounds, nor within this of their
    // boundary — a house footprint reaches ~8m from its lot center.
    const float CastleClear = 8f;
    // A lot is skipped when its footprint's corners differ by more
    // than the plinth can bury — the same "houses terrace, cliffs
    // refuse" call the village parcel bar makes at parcel scale.
    const float MaxLotRelief = 2.2f;
    const float PlinthDepth = 2.6f;

    // ---- The population ----
    // One household, one house. Neighbor towns get a fixed dozen
    // until AI lordships have real numbers; the inn arrives with
    // enough custom to keep it.
    const int NeighborHouseholds = 12;
    const int InnThreshold = 10;

    static Transform root;
    static int seenPlots = -1, seenHouseholds = -1;
    static Material timberMat, stoneMat, roofMat;
    static Material darkTimberMat, whitewashMat, signMat;

    // The one door, called every frame from BuildMenu.Update beside
    // EnsureGenerated: a two-int signature check. Load, Resurvey,
    // snapshot undo and future population growth all arrive here.
    public static void EnsureCurrent()
    {
        if (LandPlot.All.Count == seenPlots
            && Estate.Households == seenHouseholds)
            return;
        seenPlots = LandPlot.All.Count;
        seenHouseholds = Estate.Households;
        Rebuild();
    }

    public static void Rebuild()
    {
        // DestroyImmediate, not Destroy: EnsureCurrent may rebuild in
        // the same frame a load repopulated the plots, and a deferred
        // destroy would leave two villages standing for a frame.
        if (root != null)
            Object.DestroyImmediate(root.gameObject);
        root = null;
        if (LandPlot.All.Count == 0 || !Ground.Any)
            return;
        root = new GameObject("Villages").transform;
        List<LandParcels.Site> sites = LandParcels.Sites();
        int houses = 0, towns = 0;
        for (int t = 0; t < sites.Count; t++)
        {
            var living = new List<LandPlot>();
            var castles = new List<LandPlot>();
            foreach (LandPlot p in LandPlot.All)
            {
                if (p == null || p.territory != t)
                    continue;
                if (p.type == LandType.Living)
                    living.Add(p);
                else if (p.type == LandType.Castle)
                    castles.Add(p);
            }
            if (living.Count == 0)
                continue;
            int built = BuildTown(sites[t], living, castles,
                sites[t].home ? Estate.Households : NeighborHouseholds);
            if (built > 0)
            {
                houses += built;
                towns++;
            }
        }
        if (towns > 0)
            BuildLog.Add($"The villages raised — {houses} buildings"
                + $" across {towns} town{(towns == 1 ? "" : "s")}.");
    }

    struct Lot
    {
        public Vector2 pos;
        public float facing;   // degrees; the gable end looks this way
        public float order;    // castle distance + row penalty — the
                               // plan's fill order
    }

    // Returns how many buildings stood. Lot 0..k are consumed in plan
    // order: church on the most prominent early lot, inn (population
    // permitting), then one house per household while lots last.
    static int BuildTown(LandParcels.Site site, List<LandPlot> living,
        List<LandPlot> castles, int households)
    {
        List<Lot> lots = PlanLots(site, living, castles);
        if (lots.Count == 0)
            return 0;

        // The church takes the highest ground among the first few
        // lots — prominence, not the very first plot off the gate.
        int churchAt = 0;
        float bestY = float.MinValue;
        for (int i = 0; i < Mathf.Min(lots.Count, 8); i++)
        {
            float y = Ground.HeightAt(lots[i].pos.x, lots[i].pos.y);
            if (y > bestY)
            {
                bestY = y;
                churchAt = i;
            }
        }
        var town = new GameObject(site.name + " village").transform;
        town.SetParent(root, false);
        int built = 0, cursor = 0;
        bool inn = households >= InnThreshold;
        Church(town, lots[churchAt]);
        built++;
        for (int n = 0; n < households && cursor < lots.Count; cursor++)
        {
            if (cursor == churchAt)
                continue;
            if (inn)
            {
                Inn(town, lots[cursor]);
                inn = false;
                built++;
                continue;
            }
            House(town, lots[cursor], n);
            built++;
            n++;
        }
        return built;
    }

    // The stable plan: stations every LotSpacing along each street —
    // the castle routes, plus back lanes offset either side of them —
    // a lot on each side and in each row where the ground is a Living
    // parcel of this territory (primary front rows tolerate the
    // outline wiggle), level enough, clear of the water and of
    // earlier lots — ordered by distance from the castle plus a back-
    // row penalty, so the town builds outward from the gate but fills
    // inward near the castle before straggling down the lane.
    // Deterministic: same facts, same plan.
    static List<Lot> PlanLots(LandParcels.Site site, List<LandPlot> living,
        List<LandPlot> castles)
    {
        var lots = new List<Lot>();
        var taken = new List<Vector2>();
        var streets = new List<(float[] pts, bool primary)>();
        foreach (float[] road in LandFeatures.RoadLines())
        {
            streets.Add((road, true));
            for (int k = 1; k <= LaneCount; k++)
            {
                streets.Add((OffsetPolyline(road, k * LanePitch), false));
                streets.Add((OffsetPolyline(road, -k * LanePitch), false));
            }
        }
        List<(float[] p, float w)> waters = LandFeatures.WaterLines();
        for (int ri = 0; ri < streets.Count; ri++)
        {
            (float[] r, bool primary) = streets[ri];
            float untilNext = 0f;
            for (int i = 0; i + 3 < r.Length; i += 2)
            {
                var a = new Vector2(r[i], r[i + 1]);
                var b = new Vector2(r[i + 2], r[i + 3]);
                float len = (b - a).magnitude;
                if (len < 0.01f)
                    continue;
                Vector2 dir = (b - a) / len;
                var nrm = new Vector2(-dir.y, dir.x);
                float at = untilNext;
                while (at <= len)
                {
                    Vector2 p = a + dir * at;
                    at += LotSpacing;
                    for (int side = -1; side <= 1; side += 2)
                        for (int row = 0; row < 2; row++)
                        {
                            float jitter =
                                Hash01(p.x, p.y, side * (row + 1)) * 2.5f;
                            Vector2 c = p + nrm * side
                                * (Setback + row * RowPitch + jitter);
                            if (!InsideAny(living, c)
                                && (row > 0 || !primary
                                    || EdgeDistance(living, c) > EdgeTolerance))
                                continue;
                            // The castle grounds are the lord's, not
                            // the town's — no lot inside them or close
                            // enough that a footprint reaches in. Lanes
                            // (sideways offsets of routes trimmed only
                            // on the centerline) and the frontage
                            // tolerance both leaked houses across the
                            // gate without this.
                            if (castles.Count > 0
                                && (InsideAny(castles, c)
                                    || EdgeDistance(castles, c) < CastleClear))
                                continue;
                            if (LotRelief(c) > MaxLotRelief)
                                continue;
                            if (WaterDistance(waters, c) < RiverClear)
                                continue;
                            bool crowded = false;
                            foreach (Vector2 q in taken)
                                if ((q - c).sqrMagnitude
                                    < MinLotGap * MinLotGap)
                                {
                                    crowded = true;
                                    break;
                                }
                            if (crowded)
                                continue;
                            taken.Add(c);
                            lots.Add(new Lot
                            {
                                pos = c,
                                // The gable end looks back at the street.
                                facing = Mathf.Atan2(-nrm.x * side,
                                    -nrm.y * side) * Mathf.Rad2Deg,
                                order = (c - site.pos).magnitude
                                    + row * BackRowPenalty,
                            });
                        }
                }
                untilNext = at - len;
            }
        }
        lots.Sort((x, y) => x.order != y.order ? x.order.CompareTo(y.order)
            : x.pos.x != y.pos.x ? x.pos.x.CompareTo(y.pos.x)
            : x.pos.y.CompareTo(y.pos.y));
        return lots;
    }

    // A back lane: each vertex pushed sideways along its local normal
    // (the averaged direction of its neighbors). No mitering — sharp
    // bends compress or fan the lane, and the crowding and containment
    // checks absorb what that produces; lanes are dressing, not roads.
    static float[] OffsetPolyline(float[] p, float off)
    {
        int m = p.Length / 2;
        var q = new float[m * 2];
        for (int i = 0; i < m; i++)
        {
            var prev = new Vector2(p[Mathf.Max(0, i - 1) * 2],
                p[Mathf.Max(0, i - 1) * 2 + 1]);
            var next = new Vector2(p[Mathf.Min(m - 1, i + 1) * 2],
                p[Mathf.Min(m - 1, i + 1) * 2 + 1]);
            Vector2 d = next - prev;
            if (d.sqrMagnitude < 1e-6f)
                d = Vector2.up;
            d.Normalize();
            q[i * 2] = p[i * 2] - d.y * off;
            q[i * 2 + 1] = p[i * 2 + 1] + d.x * off;
        }
        return q;
    }

    static bool InsideAny(List<LandPlot> plots, Vector2 p)
    {
        var v = new Vector3(p.x, 0f, p.y);
        foreach (LandPlot plot in plots)
            if (SlabTile.Contains(plot.verts, v))
                return true;
        return false;
    }

    // Distance from a point to the nearest Living-parcel edge — the
    // front-row tolerance test. Only called for points OUTSIDE every
    // polygon, so the answer is how far past the boundary they sit.
    static float EdgeDistance(List<LandPlot> plots, Vector2 p)
    {
        float best = float.MaxValue;
        foreach (LandPlot plot in plots)
        {
            List<Vector3> vs = plot.verts;
            for (int i = 0; i < vs.Count; i++)
            {
                var a = new Vector2(vs[i].x, vs[i].z);
                Vector3 w = vs[(i + 1) % vs.Count];
                var ab = new Vector2(w.x, w.z) - a;
                float t = Mathf.Clamp01(Vector2.Dot(p - a, ab)
                    / Mathf.Max(0.0001f, ab.sqrMagnitude));
                best = Mathf.Min(best, (a + ab * t - p).magnitude);
            }
        }
        return best;
    }

    // Distance from a point to the nearest water's EDGE (segment
    // distance minus the feature's half width — the carved channel is
    // about the feature width).
    static float WaterDistance(List<(float[] p, float w)> waters, Vector2 c)
    {
        float best = float.MaxValue;
        foreach ((float[] p, float w) in waters)
            for (int i = 0; i + 3 < p.Length; i += 2)
            {
                var a = new Vector2(p[i], p[i + 1]);
                var ab = new Vector2(p[i + 2], p[i + 3]) - a;
                float t = Mathf.Clamp01(Vector2.Dot(c - a, ab)
                    / Mathf.Max(0.0001f, ab.sqrMagnitude));
                best = Mathf.Min(best,
                    (a + ab * t - c).magnitude - w * 0.5f);
            }
        return best;
    }

    static float LotRelief(Vector2 c)
    {
        float lo = float.MaxValue, hi = float.MinValue;
        for (int dx = -1; dx <= 1; dx += 2)
            for (int dz = -1; dz <= 1; dz += 2)
            {
                float y = Ground.HeightAt(c.x + dx * 4f, c.y + dz * 4f);
                lo = Mathf.Min(lo, y);
                hi = Mathf.Max(hi, y);
            }
        return hi - lo;
    }

    // ---- The materializers ----
    // Graybox on purpose; real art replaces these bodies and nothing
    // above them. Every building: floor at the highest corner of its
    // footprint (Seat), plinth skirting PlinthDepth down — a house
    // seats itself the way a foundation slab does, without being one.

    static void House(Transform town, Lot lot, int idx)
    {
        float w = 5.0f + Hash01(lot.pos.x, lot.pos.y, 11) * 1.6f;
        float d = 6.5f + Hash01(lot.pos.x, lot.pos.y, 13) * 2.5f;
        float h = 2.6f + Hash01(lot.pos.x, lot.pos.y, 17) * 0.6f;
        Transform b = Seat(town, "House " + idx, lot, w, d);
        Plinth(b, w, d, PlinthDepth);
        Box(b, "Walls", Timber(), new Vector3(w, h, d),
            new Vector3(0f, h * 0.5f, 0f));
        Roof(b, w, d + 0.8f, h + 0.15f);
    }

    // The inn must read as the inn from across the green (the user's
    // ask — a big cottage wasn't tellable): a jettied, whitewashed
    // upper storey overhanging a dark timber ground floor, a wider
    // roof, and a painted sign hanging at the street corner.
    static void Inn(Transform town, Lot lot)
    {
        const float w = 7f, d = 10f, ground = 2.8f, upper = 2.5f;
        Transform b = Seat(town, "Inn", lot, w + 1f, d + 0.7f);
        Plinth(b, w, d, PlinthDepth);
        Box(b, "Ground floor",
            Mat(ref darkTimberMat, "InnTimber",
                new Color(0.34f, 0.27f, 0.2f)),
            new Vector3(w, ground, d),
            new Vector3(0f, ground * 0.5f, 0f));
        Box(b, "Jetty",
            Mat(ref whitewashMat, "InnWhitewash",
                new Color(0.88f, 0.85f, 0.76f)),
            new Vector3(w + 1f, upper, d + 0.7f),
            new Vector3(0f, ground + upper * 0.5f, 0f));
        Roof(b, w + 1f, d + 1.8f, ground + upper + 0.15f);
        Box(b, "Sign post", darkTimberMat,
            new Vector3(0.18f, 3.4f, 0.18f),
            new Vector3(w * 0.5f + 1.3f, 1.7f, d * 0.5f + 0.9f));
        Box(b, "Sign arm", darkTimberMat,
            new Vector3(1.3f, 0.14f, 0.14f),
            new Vector3(w * 0.5f + 0.75f, 3.25f, d * 0.5f + 0.9f));
        Box(b, "Sign", Mat(ref signMat, "InnSign",
                new Color(0.5f, 0.11f, 0.09f)),
            new Vector3(0.9f, 0.7f, 0.08f),
            new Vector3(w * 0.5f + 0.55f, 2.75f, d * 0.5f + 0.9f));
    }

    static void Church(Transform town, Lot lot)
    {
        const float w = 7.5f, d = 14f, h = 5.5f;
        Transform b = Seat(town, "Church", lot, w, d);
        Plinth(b, w, d, PlinthDepth + 1.5f);
        Box(b, "Nave", Stone(), new Vector3(w, h, d),
            new Vector3(0f, h * 0.5f, 0f));
        Roof(b, w, d + 0.8f, h + 0.15f);
        // The tower stands at the street end of the nave.
        Box(b, "Tower", Stone(), new Vector3(4.2f, 10.5f, 4.2f),
            new Vector3(0f, 5.25f, d * 0.5f - 1.2f));
    }

    // The building root: positioned on the lot, rotated so local +Z
    // faces the street, floor (local y=0) at the highest ground under
    // the w×d footprint.
    static Transform Seat(Transform town, string name, Lot lot,
        float w, float d)
    {
        float floor = float.MinValue;
        float yaw = lot.facing * Mathf.Deg2Rad;
        var fwd = new Vector2(Mathf.Sin(yaw), Mathf.Cos(yaw));
        var right = new Vector2(fwd.y, -fwd.x);
        for (int sx = -1; sx <= 1; sx += 2)
            for (int sz = -1; sz <= 1; sz += 2)
            {
                Vector2 corner = lot.pos + right * sx * w * 0.5f
                    + fwd * sz * d * 0.5f;
                floor = Mathf.Max(floor,
                    Ground.HeightAt(corner.x, corner.y));
            }
        var go = new GameObject(name);
        go.transform.SetParent(town, false);
        go.transform.position
            = new Vector3(lot.pos.x, floor + 0.05f, lot.pos.y);
        go.transform.rotation = Quaternion.Euler(0f, lot.facing, 0f);
        return go.transform;
    }

    static void Plinth(Transform root, float w, float d, float depth)
        => Box(root, "Plinth", Stone(),
            new Vector3(w + 0.4f, depth, d + 0.4f),
            new Vector3(0f, -depth * 0.5f, 0f));

    // A cube rotated 45° about the ridge axis reads as a gable from
    // any distance the town is judged at. Sized so its lower corners
    // meet the wall tops, stretched for eaves.
    static void Roof(Transform root, float span, float length, float atY)
    {
        float s = span * 0.78f;
        Transform roof = Box(root, "Roof",
            Mat(ref roofMat, "VillageThatch",
                new Color(0.45f, 0.36f, 0.22f)),
            new Vector3(s, s, length), new Vector3(0f, atY, 0f));
        roof.localRotation = Quaternion.Euler(0f, 0f, 45f);
    }

    static Transform Box(Transform parent, string name, Material m,
        Vector3 scale, Vector3 at)
    {
        var t = GameObject.CreatePrimitive(PrimitiveType.Cube).transform;
        t.name = name;
        t.SetParent(parent, false);
        t.GetComponent<MeshRenderer>().sharedMaterial = m;
        t.localScale = scale;
        t.localPosition = at;
        return t;
    }

    static Material Timber() => Mat(ref timberMat, "VillageTimber",
        new Color(0.78f, 0.68f, 0.52f));
    static Material Stone() => Mat(ref stoneMat, "VillageStone",
        new Color(0.62f, 0.6f, 0.56f));

    static Material Mat(ref Material slot, string name, Color c)
    {
        if (slot == null)
            slot = new Material(Shader.Find("HDRP/Lit"))
            { name = name, color = c };
        return slot;
    }

    static float Hash01(float a, float b, int salt)
    {
        uint h = (uint)(Mathf.RoundToInt(a * 8f) * 73856093
            ^ Mathf.RoundToInt(b * 8f) * 19349663 ^ salt * 83492791);
        h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
        return (h & 0xffffff) / (float)0x1000000;
    }
}
