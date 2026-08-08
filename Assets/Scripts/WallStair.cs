using System.Collections.Generic;
using UnityEngine;

// A stair: the first STORED VERTICAL relationship in the model.
//
// Everything else in the graph is planar and lives at exactly one baseY —
// that's why WallGraph.SameBase can filter every query with a single
// float. A stair belongs to two storeys at once, which is a different
// kind of fact, so it gets its own object rather than a flag on WallEdge
// that every planar query would have to filter out. It is a RIDER on the
// graph the way SlabTile is: registered here, member of no WallNode,
// invisible to FindCrossings / OverlapsExisting / FaceAt. It never splits
// a wall and no wall splits it.
//
// This doesn't front-run the deferred structural-support call. Support is
// "what rests on what" — spatial, inferrable, correctly deferred. A stair
// is "you can get from here to there" — authored, because the player drew
// it. Walk-mode and attacker pathing will read circulation as stored
// structure instead of re-deriving it from geometry.
//
// Geometry is BAKED AT COMMIT and the host wall is not stored: the wall
// the player pointed at is only what the cursor resolver used to compute
// these numbers, exactly as a flank-snapped wall stores its own node
// point rather than "I am snapped to that". So a stair never rebuilds
// because a neighbour changed, and deleting the wall it leans on leaves
// it standing in the air — consistent with an upper storey over a deleted
// wall. See Docs/Stairs.md.
public class WallStair : MonoBehaviour
{
    public static readonly List<WallStair> All = new List<WallStair>();

    // One piece of the run: a quadratic (same as a wall's), plus where it
    // sits along the whole stair measured in arc length.
    //
    // A stair is a CHAIN of these because the masonry it climbs is one:
    // a circle is eight arcs and a wall with tees in it is several edges,
    // and a stair confined to a single edge pins itself to whichever node
    // it started from. The wall tool's ride already follows masonry
    // through its nodes for exactly this reason; a stair does the same.
    [System.Serializable]
    public struct Span
    {
        public Vector3 s;
        public Vector3 c;
        public Vector3 e;
        public float arcStart;
        public float arcEnd;
    }

    public readonly List<Span> spans = new List<Span>();

    // How much of the chain CLIMBS. Everything past this is the landing:
    // the same run carried on level at topY, so arriving at the next floor
    // doesn't mean arriving at a drop. It is part of the same chain rather
    // than a separate object because it follows the same masonry through
    // the same nodes — a landing that stopped at a corner would be exactly
    // the bug the chain was built to fix.
    public float runArc;

    // The run's ends, for anything that just wants the extremes.
    public Vector3 bottom => spans.Count > 0 ? spans[0].s : transform.position;
    public Vector3 top => spans.Count > 0 ? spans[spans.Count - 1].e : transform.position;
    public float TotalArc => spans.Count > 0 ? spans[spans.Count - 1].arcEnd : 0f;
    // Elevation the first riser starts from, and the landing.
    public float bottomY;
    public float topY;
    // The storey it stands on (-Infinity = terrain). The wedge never digs
    // below this, so a stair on a deck sits ON the deck instead of
    // reaching for the ground far underneath it.
    public float baseFrom = float.NegativeInfinity;
    public float width = 1.2f;

    [HideInInspector] public Material material;

    // Public so the save can round-trip them; set by Create like the rest.
    public float baseStep = 0.5f;
    public float baseWallHeight = 3.5f;

    // About 41° — a real castle stair, and steep enough that a storey's
    // worth of run fits inside a room. Rise is the primary: the count is
    // chosen so every riser comes out exactly equal (the BuildSpecs
    // pattern), and the run follows from the count.
    public const float TargetRise = 0.22f;
    public const float TargetGoing = 0.25f;
    // Somewhere to stand at the top. Best-effort, not required: a stair
    // that fits is never refused because its landing doesn't, so a short
    // wall gives a short landing rather than nothing at all.
    public const float LandingRun = 2.5f;

    public float Rise => topY - bottomY;
    public int StepCount => StepsFor(Rise);

    void OnEnable() { All.Add(this); }
    void OnDisable() { All.Remove(this); }

    public static int StepsFor(float rise)
    {
        return Mathf.Max(1, Mathf.RoundToInt(rise / TargetRise));
    }

    // How much plan length a given rise needs. The player never chooses
    // this, which is why the stair tool has no refusal: a stair can't be
    // too steep or too shallow when its length is derived from its climb.
    public static float RunFor(float rise)
    {
        return StepsFor(rise) * TargetGoing;
    }

    // Welds an offset chain at its joints. Offsetting each span of the
    // masonry independently does NOT leave them touching: at a turn the
    // inner offsets overlap and the outer ones gap, by roughly
    // 2·offset·tan(half the turn) — 0.84m at an octagon's corner, three
    // treads' worth of jump. The fix is the same one the room deck uses
    // for its inset outline: meet at the MITER, where the two offset
    // centerlines actually cross.
    //
    // The chord moves, so each span's bulge is carried over relative to
    // its new chord rather than left pointing at the old one. A turn
    // sharp enough to throw the miter to infinity falls back to the
    // midpoint — a blunted corner beats a spike.
    public static void WeldOffsetJoints(List<(Vector3 s, Vector3 c, Vector3 e)> curves, float offset)
    {
        float maxReach = Mathf.Max(1f, offset * 4f);
        for (int i = 0; i + 1 < curves.Count; i++)
        {
            (Vector3 s, Vector3 c, Vector3 e) a = curves[i];
            (Vector3 s, Vector3 c, Vector3 e) b = curves[i + 1];
            if (Vector3.Distance(a.e, b.s) < 0.001f)
                continue;

            Vector3 da = WallEdge.Tangent(a.s, a.c, a.e, 1f).normalized;
            Vector3 db = WallEdge.Tangent(b.s, b.c, b.e, 0f).normalized;
            float denom = da.x * db.z - da.z * db.x;
            Vector3 m;
            if (Mathf.Abs(denom) < 1e-5f)
            {
                m = (a.e + b.s) * 0.5f;
            }
            else
            {
                float u = ((b.s.x - a.e.x) * db.z - (b.s.z - a.e.z) * db.x) / denom;
                m = Mathf.Abs(u) > maxReach ? (a.e + b.s) * 0.5f : a.e + da * u;
            }
            m.y = 0f;

            Vector3 bulgeA = a.c - (a.s + a.e) * 0.5f;
            Vector3 bulgeB = b.c - (b.s + b.e) * 0.5f;
            curves[i] = (a.s, (a.s + m) * 0.5f + bulgeA, m);
            curves[i + 1] = (m, (m + b.e) * 0.5f + bulgeB, b.e);
        }
    }

    // Builds the arc bookkeeping for a chain of raw curves: each span's
    // arcStart/arcEnd are cumulative along the whole stair, which is what
    // lets the treads be cut over the TOTAL climb and then clipped into
    // whichever spans they land in — so the risers stay equal across a
    // joint instead of each span rounding its own step count.
    public static List<Span> ChainSpans(List<(Vector3 s, Vector3 c, Vector3 e)> curves)
    {
        var spans = new List<Span>();
        float at = 0f;
        foreach ((Vector3 s, Vector3 c, Vector3 e) in curves)
        {
            float[] table = WallEdge.BuildArcTable(s, c, e);
            float len = table[table.Length - 1];
            if (len < 0.01f)
                continue;
            spans.Add(new Span { s = s, c = c, e = e, arcStart = at, arcEnd = at + len });
            at += len;
        }
        return spans;
    }

    // The same chain walked the other way. The walk always starts at the
    // cursor and runs outward, which puts the cursor at the stair's
    // BOTTOM — right for climbing a wall, wrong for arriving at a doorway,
    // where the cursor marks the destination. Reversing costs nothing
    // geometrically (a quadratic's control point is unchanged by swapping
    // its ends) and it does NOT move the run: the side offset was baked in
    // during the walk, so only which end is high changes.
    public static List<Span> ReverseSpans(List<Span> spans)
    {
        var curves = new List<(Vector3 s, Vector3 c, Vector3 e)>();
        for (int i = spans.Count - 1; i >= 0; i--)
            curves.Add((spans[i].e, spans[i].c, spans[i].s));
        return ChainSpans(curves);
    }

    public static WallStair Create(Transform parent, List<Span> spans,
        float bottomY, float topY, float runArc, float baseFrom, float width,
        float baseWallHeight, float baseStep, Material material)
    {
        GameObject obj = new GameObject("WallStair");
        obj.transform.SetParent(parent, false);
        WallStair stair = obj.AddComponent<WallStair>();
        stair.spans.AddRange(spans);
        stair.bottomY = bottomY;
        stair.topY = topY;
        stair.runArc = runArc;
        stair.baseFrom = baseFrom;
        stair.width = width;
        stair.baseWallHeight = baseWallHeight;
        stair.baseStep = baseStep;
        stair.material = material;
        stair.Rebuild();
        return stair;
    }

    // Re-derives the treads from this stair's own stored run. Like an
    // edge, nothing else in the world affects the result.
    public void Rebuild()
    {
        foreach (WallStairStep step in GetComponentsInChildren<WallStairStep>())
            DestroyImmediate(step.gameObject);

        List<WallEdge.SectionSpec> specs = BuildStepSpecs(spans,
            bottomY, topY, runArc, baseFrom, width, baseStep, baseWallHeight);
        foreach (WallEdge.SectionSpec spec in specs)
            CreateStep(spec);
        Physics.SyncTransforms();
    }

    void CreateStep(WallEdge.SectionSpec spec)
    {
        GameObject obj = new GameObject("StairStep");
        obj.transform.SetParent(transform, false);
        obj.transform.SetPositionAndRotation(spec.position, Quaternion.Euler(0f, spec.yaw, 0f));

        obj.AddComponent<MeshFilter>().sharedMesh = spec.mesh;
        obj.AddComponent<MeshRenderer>().sharedMaterial = material;

        // Cursor raycasts only (picking the stair to delete it), never
        // occupancy — same rule as wall section colliders.
        BoxCollider box = obj.AddComponent<BoxCollider>();
        box.size = new Vector3(spec.arcLength, spec.topY - spec.bottomY, width);

        WallStairStep step = obj.AddComponent<WallStairStep>();
        step.stair = this;
        step.index = spec.index;
        step.bottomY = spec.bottomY;
        step.topY = spec.topY;
        step.ownedMesh = spec.mesh;
    }

    // ONE SECTION PER STEP. WallEdge.BuildSpecs already picks a count so
    // every section comes out the same arc length, which is exactly what
    // treads are; a stair is that same sweep with the count taken from the
    // step count and tops that climb instead of following one height.
    //
    // The section mesh needs no changes at all: every section already caps
    // both ends, so at a step boundary the taller section's start cap IS
    // the riser, and the coincident part below it is two caps facing
    // opposite ways (fine, by the same rule the room deck follows). The
    // run stays watertight for the usual reason — neighbours evaluate the
    // shared curve at the same boundary parameter.
    public static List<WallEdge.SectionSpec> BuildStepSpecs(List<Span> spans,
        float bottomY, float topY, float runArc, float baseFrom, float width,
        float baseStep, float baseWallHeight)
    {
        var specs = new List<WallEdge.SectionSpec>();
        float rise = topY - bottomY;
        if (rise < TargetRise * 0.5f || spans.Count == 0)
            return specs;
        float totalArc = spans[spans.Count - 1].arcEnd;
        if (totalArc < 0.05f)
            return specs;

        // The climb takes the first runArc of the chain; whatever the walk
        // found beyond it is landing. Because the treads are cut over that
        // measured arc rather than over the whole chain, each going comes
        // out at exactly TargetGoing and the pitch is exact — the landing
        // absorbs the difference between what was asked for and what the
        // welded corners actually returned.
        float climb = Mathf.Clamp(runArc <= 0.01f ? totalArc : runArc, 0f, totalArc);
        int count = StepsFor(rise);
        float stepRise = rise / count;
        int mesh = 0;
        for (int i = 0; i < count; i++)
        {
            float stepTop = bottomY + stepRise * (i + 1);
            Emit(specs, spans, climb * i / count, climb * (i + 1) / count,
                stepTop, stepTop - stepRise, baseFrom, width, baseStep,
                baseWallHeight, ref mesh);
        }
        // The landing: level, at the height the last tread arrived at.
        if (totalArc - climb > 0.05f)
            Emit(specs, spans, climb, totalArc, topY, topY - stepRise, baseFrom,
                width, baseStep, baseWallHeight, ref mesh);
        return specs;
    }

    // One flat-topped piece of the wedge over an arc range, clipped into
    // whichever spans it lands in.
    //
    // A piece that straddles a joint becomes two meshes at the same height
    // — the same answer the graph gives when an edge crosses a node, and
    // the reason the risers stay equal all the way round a circle.
    static void Emit(List<WallEdge.SectionSpec> specs, List<Span> spans,
        float arcLo, float arcHi, float top, float floor, float baseFrom,
        float width, float baseStep, float baseWallHeight, ref int mesh)
    {
        foreach (Span sp in spans)
        {
            float lo = Mathf.Max(arcLo, sp.arcStart);
            float hi = Mathf.Min(arcHi, sp.arcEnd);
            if (hi - lo < 0.005f)
                continue;

            float[] table = WallEdge.BuildArcTable(sp.s, sp.c, sp.e);
            var spec = new WallEdge.SectionSpec
            {
                index = mesh++,
                tStart = WallEdge.TAtArc(table, lo - sp.arcStart),
                tEnd = WallEdge.TAtArc(table, hi - sp.arcStart),
                topY = top,
            };
            // The wedge is SOLID: its bottom steps with the ground the way
            // a wall's does, never digs below the storey it stands on, and
            // is clamped under `floor` so a rising slope can't invert a
            // step into nothing. Buried stone costs nothing; daylight
            // does — same reasoning as WallEdge.skirt.
            float ground = WallEdge.SampleSliceGround(sp.s, sp.c, sp.e,
                spec.tStart, spec.tEnd, width, baseStep);
            spec.bottomY = Mathf.Min(Mathf.Max(ground, baseFrom), floor);
            if (WallEdge.BuildSpecFrame(ref spec, sp.s, sp.c, sp.e, width,
                baseWallHeight, table))
                specs.Add(spec);
        }
    }
}
