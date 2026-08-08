using System.Collections.Generic;
using UnityEngine;

// One wall of the graph: an edge between exactly two WallNodes, swept as a
// quadratic Bézier and cut into uniform arc-length sections. Edges never
// overlap or cross — placement resolves crossings into shared nodes before
// any edge exists (WallGraph) — so an edge's geometry depends on nothing
// but its own nodes, control point, heights, and the terrain: no probes,
// no cut planes, no rebuild cascades. Both ends always cap flat exactly ON
// their node's point; the node's post (WallNode) owns every joint.
//
// Height above the ground is baseY, not a separate kind of object: a wall
// standing on another wall's top or on a room slab is an ordinary edge
// with its own nodes, and the planar graph is filtered per base
// (WallGraph.SameBase) so storeys never split or refuse one another.
public class WallEdge : MonoBehaviour
{
    // Active edges, in no particular order.
    public static readonly List<WallEdge> All = new List<WallEdge>();

    public WallNode nodeA;
    public WallNode nodeB;
    // Quadratic Bézier control point — the AB midpoint for a straight
    // wall. Splits stay exact via polar-form subdivision (WallGraph).
    public Vector3 control;
    public float height;
    public float thickness;
    // Texture-V anchor: one tile of stone per this much world height, so
    // every wall shares aligned masonry lines.
    public float baseWallHeight = 3.5f;
    public float targetSectionLength = 1f;
    public float baseStep = 0.5f;
    // Locked-top walls: every section's top sits at this absolute height
    // and only the bottom follows the terrain down. -Infinity (default)
    // means tops ride the ground instead (bottom + height per section).
    public float fixedTopY = float.NegativeInfinity;
    // Elevated walls: sections never drop below this — the base a wall
    // stands on when built ON a room slab or another wall's top instead of
    // the terrain. -Infinity (default) rides the terrain. Also the storey
    // filter for every planar-graph query (WallGraph.SameBase).
    public float baseY = float.NegativeInfinity;
    // How far BELOW baseY this wall's masonry actually starts. Purely
    // geometric — baseY stays the storey (node identity and every planar
    // query read it), while the skirt buries the joint. A wall standing on
    // another wall's top needs it: an unlocked host's top STEPS with the
    // terrain, so a flat bottom at one section's top leaves an open slot
    // over every lower step. Buried stone costs nothing; daylight does.
    public float skirt;
    // Where the masonry actually starts — baseY less the buried skirt.
    public float FloorY => float.IsNegativeInfinity(baseY) ? baseY : baseY - skirt;
    // A doorway through this wall. STORED ON THE EDGE, not as a rider:
    // an opening is a hole in this masonry and nothing else's, so it
    // belongs to the same data the sections are derived from and the
    // doctrine holds unchanged — an edge's geometry still depends on
    // nothing but its own fields. A rider could only cut a hole by making
    // the wall ask what is standing near it, which is the inference this
    // graph exists to avoid.
    //
    // `t` is the curve parameter of the opening's CENTRE, which survives
    // subdivision linearly (SubCurve is exact polar-form, so a sub-curve's
    // parameter maps to the parent's by a straight lerp). `width` and
    // `head` are metres, and are unchanged by a split because the geometry
    // is.
    //
    // `sill` is an ABSOLUTE world height (-Infinity = stand on the wall's
    // own footing, the safe default). It exists because a room's floor is
    // one level slab at its highest interior ground, so on a slope the
    // floor can stand well above the ground outside the downhill wall — a
    // doorway cut from that ground opens into the side of the slab. The
    // doorway opens from the FLOOR it serves instead, and the masonry
    // below it becomes a threshold block.
    [System.Serializable]
    public struct Opening
    {
        public float t;
        public float width;
        public float head;
        public float sill;
    }
    public List<Opening> openings = new List<Opening>();

    public Vector3 A => nodeA != null ? nodeA.point : transform.position;
    public Vector3 B => nodeB != null ? nodeB.point : transform.position;

    [HideInInspector] public Material material;

    const int MeshSegments = 8;

    public struct SectionSpec
    {
        public int index;
        public float tStart;
        public float tEnd;
        public float arcLength;
        public Vector3 position;
        public float yaw;
        public float bottomY;
        public float topY;
        public Mesh mesh;
        // >= 0 when this section is the LINTEL over that opening — the
        // masonry a click removes to fill the doorway back in.
        public int openingIndex;
    }

    void OnEnable() { All.Add(this); }

    void OnDisable()
    {
        All.Remove(this);
        // Defensive cleanup for teardown paths that skip WallGraph: nodes
        // silently drop this member (a node left empty dissolves).
        if (nodeA != null)
            nodeA.OnMemberGone(this);
        if (nodeB != null)
            nodeB.OnMemberGone(this);
    }

    public static WallEdge Create(Transform parent, WallNode a, WallNode b, Vector3 control,
        float height, float thickness, float baseWallHeight, Material material,
        float targetSectionLength, float baseStep, float fixedTopY = float.NegativeInfinity,
        float baseY = float.NegativeInfinity, float skirt = 0f)
    {
        GameObject obj = new GameObject("WallEdge");
        obj.transform.SetParent(parent, false);
        WallEdge edge = obj.AddComponent<WallEdge>();
        edge.nodeA = a;
        edge.nodeB = b;
        edge.control = control;
        edge.height = height;
        edge.thickness = thickness;
        edge.baseWallHeight = baseWallHeight;
        edge.targetSectionLength = targetSectionLength;
        edge.baseStep = baseStep;
        edge.fixedTopY = fixedTopY;
        edge.baseY = baseY;
        edge.skirt = skirt;
        edge.material = material;
        a.Attach(edge, true);
        b.Attach(edge, false);
        edge.Rebuild();
        return edge;
    }

    // Re-derives this edge's sections from its stored curve and inputs.
    // Nothing else in the world affects the result — that is the graph's
    // whole point.
    public void Rebuild()
    {
        foreach (WallEdgeSection section in GetComponentsInChildren<WallEdgeSection>())
            DestroyImmediate(section.gameObject);

        List<SectionSpec> specs = BuildSpecs(A, control, B, height, targetSectionLength,
            thickness, baseStep, baseWallHeight, fixedTopY, FloorY, openings);
        foreach (SectionSpec spec in specs)
            CreateSection(spec);
        Physics.SyncTransforms();
    }

    void CreateSection(SectionSpec spec)
    {
        GameObject sectionObj = new GameObject("WallSection");
        sectionObj.transform.SetParent(transform, false);
        sectionObj.transform.SetPositionAndRotation(spec.position, Quaternion.Euler(0f, spec.yaw, 0f));

        sectionObj.AddComponent<MeshFilter>().sharedMesh = spec.mesh;
        sectionObj.AddComponent<MeshRenderer>().sharedMaterial = material;

        // Box approximation of the (slightly curved) slice — used only for
        // cursor raycasts (targeting, building on the top face, delete),
        // never for occupancy inference.
        BoxCollider box = sectionObj.AddComponent<BoxCollider>();
        box.size = new Vector3(spec.arcLength, spec.topY - spec.bottomY, thickness);

        WallEdgeSection section = sectionObj.AddComponent<WallEdgeSection>();
        section.edge = this;
        section.index = spec.index;
        section.tStart = spec.tStart;
        section.tEnd = spec.tEnd;
        section.bottomY = spec.bottomY;
        section.topY = spec.topY;
        section.openingIndex = spec.openingIndex;
        section.ownedMesh = spec.mesh;
    }

    // The openings of `from` that survive whole inside its [t0, t1] piece,
    // remapped onto `to`. An opening the split runs THROUGH doesn't
    // survive: half a doorway on each side of a new node is not what
    // anyone drew, and the masonry there is being re-formed anyway.
    public static void CarryOpenings(WallEdge from, WallEdge to, float t0, float t1)
    {
        if (from.openings.Count == 0 || t1 - t0 < 0.001f)
            return;
        float[] table = BuildArcTable(from.A, from.control, from.B);
        float a0 = ArcAt(table, t0);
        float a1 = ArcAt(table, t1);
        bool any = false;
        foreach (Opening o in from.openings)
        {
            float mid = ArcAt(table, o.t);
            if (mid - o.width * 0.5f < a0 + 0.05f || mid + o.width * 0.5f > a1 - 0.05f)
                continue;
            to.openings.Add(new Opening
            {
                t = (o.t - t0) / (t1 - t0),
                width = o.width,
                head = o.head,
                sill = o.sill,
            });
            any = true;
        }
        if (any)
            to.Rebuild();
    }

    public List<WallEdgeSection> SectionsInOrder()
    {
        var list = new List<WallEdgeSection>(GetComponentsInChildren<WallEdgeSection>());
        list.Sort((a, b) => a.index.CompareTo(b.index));
        return list;
    }

    // Sections for a fresh wall on the ground. The count is chosen so
    // every section comes out the same arc length, as close to the target
    // as the total divides into — no skinny slivers. The sweep spans
    // exactly node to node: no end extensions, no overshoots — the nodes
    // own everything past the caps.
    public static List<SectionSpec> BuildSpecs(Vector3 s, Vector3 c, Vector3 e,
        float height, float targetSectionLength, float thickness, float baseStep,
        float baseWallHeight, float fixedTopY = float.NegativeInfinity,
        float baseY = float.NegativeInfinity, List<Opening> openings = null)
    {
        var specs = new List<SectionSpec>();
        float[] arcTable = BuildArcTable(s, c, e);
        float totalArc = arcTable[arcTable.Length - 1];
        if (totalArc < 0.02f)
            return specs;

        // The wall's OWN section grid, settled BEFORE any doorway is cut
        // into it: how many cells, and what each one stands on, come from
        // this edge's arc and the terrain under it and nothing else. A
        // doorway then cuts INTO the grid instead of re-dividing the wall.
        //
        // It used to re-divide. An opening split the arc into solid runs
        // and each run was shared out evenly, so EVERY boundary on the
        // wall moved and every section re-sampled a different footprint —
        // and a section's footing is the LOWEST ground under it snapped
        // down to the base step, so a longer section sits lower. Cutting a
        // door into a short partition therefore dropped the masonry beside
        // it half a step: the wall sank where the door went in. Stone at
        // one end of a wall has no business moving because a hole was cut
        // at the other.
        int cells = Mathf.Max(1,
            Mathf.RoundToInt(totalArc / Mathf.Max(0.1f, targetSectionLength)));
        var cellGround = new float[cells];
        for (int i = 0; i < cells; i++)
            cellGround[i] = Mathf.Max(baseY, SampleSliceGround(s, c, e,
                TAtArc(arcTable, totalArc * i / cells),
                TAtArc(arcTable, totalArc * (i + 1) / cells), thickness, baseStep));

        int index = 0;
        foreach (Run run in Runs(arcTable, totalArc, openings))
        {
            if (run.opening < 0)
            {
                // Solid masonry follows the grid, clipped to this run. A
                // cell a doorway clips keeps its own footing, so the jamb
                // beside a door stands exactly where it stood before it.
                List<float> cuts = SolidCuts(run.lo, run.hi, totalArc, cells);
                for (int i = 0; i + 1 < cuts.Count; i++)
                {
                    var solid = new SectionSpec
                    {
                        index = index++,
                        openingIndex = -1,
                        tStart = TAtArc(arcTable, cuts[i]),
                        tEnd = TAtArc(arcTable, cuts[i + 1]),
                        bottomY = cellGround[CellAt((cuts[i] + cuts[i + 1]) * 0.5f, totalArc, cells)],
                    };
                    FinishSpec(specs, s, c, e, solid, height, fixedTopY, thickness,
                        baseWallHeight, arcTable);
                }
                continue;
            }

            // An opening is ONE section, so its ends are exactly the
            // doorway's ends — and it can span a step in the ground. It
            // reaches DOWN to the lowest cell it covers and UP to the
            // highest: the threshold has to meet the ground, the lintel
            // has to meet the wall. One figure for both is what left the
            // masonry over a door half a step below the masonry next to
            // it. Buried stone costs nothing; a notch in the wall top does
            // — the same trade the skirt makes.
            int firstCell = CellAt(run.lo, totalArc, cells);
            int lastCell = CellAt(run.hi - 0.001f, totalArc, cells);
            float lowGround = float.MaxValue, highGround = float.MinValue;
            for (int k = firstCell; k <= lastCell; k++)
            {
                lowGround = Mathf.Min(lowGround, cellGround[k]);
                highGround = Mathf.Max(highGround, cellGround[k]);
            }
            float wallTop = float.IsNegativeInfinity(fixedTopY) ? highGround + height : fixedTopY;
            // Clamped to the wall's own top, because a sill can be handed
            // a floor that stands ABOVE this wall entirely (a room on a
            // steep slope). Then the threshold fills the whole opening and
            // the lintel is dropped for having no room left: the doorway
            // is walled up, which is the honest answer — a hole into the
            // middle of a slab is not.
            float sillY = Mathf.Clamp(Mathf.Max(lowGround, run.sill), lowGround, wallTop);
            var opening = new SectionSpec
            {
                openingIndex = run.opening,
                tStart = TAtArc(arcTable, run.lo),
                tEnd = TAtArc(arcTable, run.hi),
            };
            // A THRESHOLD: the masonry under a doorway whose sill stands
            // above the wall's own footing. Same trick as the lintel,
            // upside down — an ordinary section with its TOP lowered.
            if (sillY - lowGround > 0.05f)
            {
                SectionSpec block = opening;
                block.index = index++;
                block.bottomY = lowGround;
                block.topY = sillY;
                if (BuildSpecFrame(ref block, s, c, e, thickness, baseWallHeight, arcTable))
                    specs.Add(block);
            }
            // A LINTEL is an ordinary section with its bottom raised to
            // the opening's head. Its top still comes from the wall, so
            // the masonry over the door lines up with the masonry beside
            // it; the jambs are the neighbouring sections' end caps, which
            // every section already has — a doorway needs no new mesh code
            // at all. A head taller than the wall leaves nothing to build,
            // and BuildSpecFrame drops it: a full-height gateway, not an
            // error.
            opening.index = index++;
            opening.topY = wallTop;
            opening.bottomY = sillY + run.head;
            if (BuildSpecFrame(ref opening, s, c, e, thickness, baseWallHeight, arcTable))
                specs.Add(opening);
        }
        return specs;
    }

    // Which grid cell an arc position falls in.
    static int CellAt(float arc, float totalArc, int cells)
        => Mathf.Clamp(Mathf.FloorToInt(arc / totalArc * cells), 0, cells - 1);

    // The grid boundaries falling inside a solid run, with the run's own
    // ends. A boundary landing within MinCell of one of those ends is
    // DROPPED rather than left to make a sliver beside a doorway: the
    // neighbouring section keeps the off-cut and takes the footing of
    // whichever cell its middle lands in. A sliver would be dropped by
    // BuildSpecFrame for being degenerate, and a dropped section is a gap.
    const float MinCell = 0.2f;

    static List<float> SolidCuts(float lo, float hi, float totalArc, int cells)
    {
        // Never more than half a cell, so a fine grid keeps its boundaries
        // instead of merging every cell a doorway touches.
        float minCell = Mathf.Min(MinCell, totalArc / cells * 0.5f);
        var cuts = new List<float> { lo };
        int last = CellAt(hi - 0.001f, totalArc, cells);
        for (int k = CellAt(lo, totalArc, cells) + 1; k <= last; k++)
        {
            float b = totalArc * k / cells;
            if (b - cuts[cuts.Count - 1] >= minCell && hi - b >= minCell)
                cuts.Add(b);
        }
        cuts.Add(hi);
        return cuts;
    }

    struct Run
    {
        public float lo;
        public float hi;
        public float head;
        public float sill;
        public int opening;
    }

    // The wall's arc split into solid runs and openings, in order. Cutting
    // the section boundaries at the doorway's edges is what makes a
    // doorway possible without touching the sweep: everything downstream
    // just builds sections between the boundaries it's handed.
    static IEnumerable<Run> Runs(float[] arcTable, float totalArc, List<Opening> openings)
    {
        var doors = new List<(float lo, float hi, float head, float sill, int index)>();
        if (openings != null)
            for (int i = 0; i < openings.Count; i++)
            {
                Opening o = openings[i];
                float mid = ArcAt(arcTable, Mathf.Clamp01(o.t));
                float lo = Mathf.Max(0f, mid - o.width * 0.5f);
                float hi = Mathf.Min(totalArc, mid + o.width * 0.5f);
                if (hi - lo > 0.05f)
                    doors.Add((lo, hi, o.head, o.sill, i));
            }
        doors.Sort((x, y) => x.lo.CompareTo(y.lo));

        float at = 0f;
        foreach ((float lo, float hi, float head, float sill, int index) in doors)
        {
            if (lo < at)
                continue;
            if (lo - at > 0.05f)
                yield return new Run { lo = at, hi = lo, head = 0f,
                                       sill = float.NegativeInfinity, opening = -1 };
            yield return new Run { lo = lo, hi = hi, head = head, sill = sill, opening = index };
            at = hi;
        }
        if (totalArc - at > 0.05f || at == 0f)
            yield return new Run { lo = at, hi = totalArc, head = 0f,
                                   sill = float.NegativeInfinity, opening = -1 };
    }

    // Finishes a prepared spec (index, t-range, bottomY set): vertical
    // span, placement frame, mesh. A locked-top section fully buried by
    // climbing ground simply doesn't exist.
    static void FinishSpec(List<SectionSpec> specs, Vector3 s, Vector3 c, Vector3 e,
        SectionSpec spec, float height, float fixedTopY, float thickness, float baseWallHeight,
        float[] arcTable)
    {
        spec.topY = float.IsNegativeInfinity(fixedTopY) ? spec.bottomY + height : fixedTopY;
        if (BuildSpecFrame(ref spec, s, c, e, thickness, baseWallHeight, arcTable))
            specs.Add(spec);
    }

    // The frame-and-mesh half of finishing a spec, once its t-range and
    // FULL vertical span are already decided. Split out so WallStair can
    // borrow the sweep: a stair is this same section machinery with tops
    // that step instead of following one height, and it has no business
    // knowing how a wall picks its topY. False = degenerate, skip it.
    public static bool BuildSpecFrame(ref SectionSpec spec, Vector3 s, Vector3 c, Vector3 e,
        float thickness, float baseWallHeight, float[] arcTable)
    {
        if (spec.topY - spec.bottomY < 0.1f)
            return false;
        spec.arcLength = ArcAt(arcTable, spec.tEnd) - ArcAt(arcTable, spec.tStart);
        if (spec.arcLength < 0.03f)
            return false;

        float tMid = (spec.tStart + spec.tEnd) * 0.5f;
        Vector3 mid = Evaluate(s, c, e, tMid);
        Vector3 dir = Tangent(s, c, e, tMid).normalized;
        spec.yaw = Mathf.Atan2(-dir.z, dir.x) * Mathf.Rad2Deg;
        spec.position = new Vector3(mid.x, (spec.bottomY + spec.topY) * 0.5f, mid.z);
        spec.mesh = BuildSectionMesh(s, c, e, spec, arcTable, thickness, baseWallHeight,
            spec.position, spec.yaw);
        return true;
    }

    // Lowest ground under the slice footprint (both faces sampled along
    // it), snapped down to the base step so neighbouring sections share
    // bases on gentle slopes and real drops read as uniform steps.
    // Public because a stair's wedge steps its ground exactly like a wall
    // does (WallStair).
    public static float SampleSliceGround(Vector3 s, Vector3 c, Vector3 e,
        float t0, float t1, float thickness, float baseStep)
    {
        Terrain terrain = Terrain.activeTerrain;
        if (terrain == null)
            return 0f;

        float min = float.MaxValue;
        for (int i = 0; i <= 4; i++)
        {
            float t = Mathf.Lerp(t0, t1, i / 4f);
            Vector3 p = Evaluate(s, c, e, t);
            Vector3 side = Tangent(s, c, e, t).normalized;
            side = new Vector3(-side.z, 0f, side.x) * (thickness * 0.5f);
            min = Mathf.Min(min, Mathf.Min(terrain.SampleHeight(p + side), terrain.SampleHeight(p - side)));
        }

        float ground = terrain.transform.position.y + min;
        if (baseStep > 0f)
            ground = Mathf.Floor(ground / baseStep) * baseStep;
        return ground;
    }

    public Vector3 EndPoint(bool atStart) => atStart ? A : B;

    // The direction pointing out past an end, flattened — the node reads
    // its member bearings from this, and snapping uses it as the outward
    // continuation direction.
    public Vector3 EndOutwardDir(bool atStart)
    {
        Vector3 d = Tangent(A, control, B, atStart ? 0f : 1f);
        if (atStart)
            d = -d;
        d.y = 0f;
        return d.normalized;
    }

    // Nearest curve parameter to a world point (XZ only): coarse scan plus
    // a short bisection refine, so split points land on the curve to
    // centimeters.
    public float NearestT(Vector3 point, out float distSq)
    {
        Vector3 s = A, e = B;
        float bestT = 0f;
        distSq = float.MaxValue;
        for (int i = 0; i <= 64; i++)
        {
            float t = i / 64f;
            Vector3 p = Evaluate(s, control, e, t);
            float dx = p.x - point.x, dz = p.z - point.z;
            float sq = dx * dx + dz * dz;
            if (sq < distSq) { distSq = sq; bestT = t; }
        }
        float lo = Mathf.Max(0f, bestT - 1f / 64f);
        float hi = Mathf.Min(1f, bestT + 1f / 64f);
        for (int it = 0; it < 16; it++)
        {
            float m1 = Mathf.Lerp(lo, hi, 1f / 3f);
            float m2 = Mathf.Lerp(lo, hi, 2f / 3f);
            if (DistSqAt(m1, point) < DistSqAt(m2, point))
                hi = m2;
            else
                lo = m1;
        }
        bestT = (lo + hi) * 0.5f;
        distSq = DistSqAt(bestT, point);
        return bestT;
    }

    float DistSqAt(float t, Vector3 point)
    {
        Vector3 p = Evaluate(A, control, B, t);
        float dx = p.x - point.x, dz = p.z - point.z;
        return dx * dx + dz * dz;
    }

    // Which side of this edge's centerline a point lies on (+1 toward the
    // curve's left-hand normal) — flank snapping and the offset tool read
    // the cursor's side from this.
    public float SideOf(Vector3 point)
    {
        float t = NearestT(point, out _);
        Vector3 near = Evaluate(A, control, B, t);
        Vector3 dir = Tangent(A, control, B, t).normalized;
        float side = (point.x - near.x) * -dir.z + (point.z - near.z) * dir.x;
        return side >= 0f ? 1f : -1f;
    }

    // The same curve shifted sideways by d: endpoints move along their
    // normals and the control point is refit so the curve's midpoint
    // lands d away too — exact for straight walls, a close fit for
    // bulged ones.
    public static void OffsetCurve(Vector3 s, Vector3 c, Vector3 e, float d,
        out Vector3 s2, out Vector3 c2, out Vector3 e2)
    {
        Vector3 NormalAt(float t)
        {
            Vector3 dir = Tangent(s, c, e, t).normalized;
            return new Vector3(-dir.z, 0f, dir.x);
        }
        s2 = s + NormalAt(0f) * d;
        e2 = e + NormalAt(1f) * d;
        Vector3 mid2 = Evaluate(s, c, e, 0.5f) + NormalAt(0.5f) * d;
        c2 = 2f * mid2 - 0.5f * (s2 + e2);
    }

    // Nearest point on this wall's face to a world point (XZ only), plus
    // the point-to-face distance — feeds the placement tool's distance
    // guides.
    public Vector3 ClosestFacePoint(Vector3 point, out float faceDistance)
    {
        float t = NearestT(point, out float bestSq);
        Vector3 best = Evaluate(A, control, B, t);
        float centerDist = Mathf.Sqrt(bestSq);
        faceDistance = Mathf.Max(0f, centerDist - thickness * 0.5f);
        if (centerDist > 0.001f)
        {
            Vector3 toPoint = new Vector3(point.x - best.x, 0f, point.z - best.z) / centerDist;
            best += toPoint * (thickness * 0.5f);
        }
        return new Vector3(best.x, 0f, best.z);
    }

    public static Vector3 Evaluate(Vector3 s, Vector3 c, Vector3 e, float t)
    {
        float u = 1f - t;
        return u * u * s + 2f * u * t * c + t * t * e;
    }

    public static Vector3 Tangent(Vector3 s, Vector3 c, Vector3 e, float t)
    {
        Vector3 d = 2f * (1f - t) * (c - s) + 2f * t * (e - c);
        return d.sqrMagnitude < 0.0001f ? e - s : d;
    }

    // Cumulative arc length lookup for the whole curve. Sections read
    // their texture U from this one shared table, so seams match exactly
    // and cuts are equal by measurement.
    public static float[] BuildArcTable(Vector3 s, Vector3 c, Vector3 e)
    {
        const int Samples = 65;
        float[] table = new float[Samples];
        Vector3 prev = s;
        for (int i = 1; i < Samples; i++)
        {
            Vector3 p = Evaluate(s, c, e, (float)i / (Samples - 1));
            table[i] = table[i - 1] + Vector3.Distance(prev, p);
            prev = p;
        }
        return table;
    }

    public static float ArcAt(float[] table, float t)
    {
        float f = Mathf.Clamp01(t) * (table.Length - 1);
        int i = Mathf.Min(table.Length - 2, Mathf.FloorToInt(f));
        return Mathf.Lerp(table[i], table[i + 1], f - i);
    }

    public static float TAtArc(float[] table, float arc)
    {
        arc = Mathf.Clamp(arc, 0f, table[table.Length - 1]);
        for (int i = 1; i < table.Length; i++)
        {
            if (table[i] >= arc)
            {
                float span = table[i] - table[i - 1];
                float f = span > 0.0001f ? (arc - table[i - 1]) / span : 0f;
                return (i - 1 + f) / (table.Length - 1);
            }
        }
        return 1f;
    }

    // Sweeps the wall's rectangular cross-section along one section's
    // slice of the curve. Neighbouring sections evaluate the same curve at
    // the same boundary parameter, so their end faces are identical and
    // the finished wall reads as one continuous mesh. Vertices are baked
    // into the section's local frame (position + yaw, no scale).
    static Mesh BuildSectionMesh(Vector3 s, Vector3 c, Vector3 e,
        SectionSpec spec, float[] arcTable, float thickness, float baseWallHeight,
        Vector3 center, float yaw)
    {
        Matrix4x4 worldToLocal = Matrix4x4.TRS(center, Quaternion.Euler(0f, yaw, 0f), Vector3.one).inverse;
        float halfWidth = thickness * 0.5f;
        float bottomY = spec.bottomY;
        float topY = spec.topY;

        var outerBottom = new Vector3[MeshSegments + 1];
        var outerTop = new Vector3[MeshSegments + 1];
        var innerBottom = new Vector3[MeshSegments + 1];
        var innerTop = new Vector3[MeshSegments + 1];
        var arcU = new float[MeshSegments + 1];

        for (int j = 0; j <= MeshSegments; j++)
        {
            float t = Mathf.Lerp(spec.tStart, spec.tEnd, (float)j / MeshSegments);
            Vector3 p = Evaluate(s, c, e, t);
            Vector3 side = Tangent(s, c, e, t).normalized;
            side = new Vector3(-side.z, 0f, side.x) * halfWidth;
            Vector3 outer = new Vector3(p.x + side.x, 0f, p.z + side.z);
            Vector3 inner = new Vector3(p.x - side.x, 0f, p.z - side.z);

            outerBottom[j] = worldToLocal.MultiplyPoint3x4(new Vector3(outer.x, bottomY, outer.z));
            outerTop[j] = worldToLocal.MultiplyPoint3x4(new Vector3(outer.x, topY, outer.z));
            innerBottom[j] = worldToLocal.MultiplyPoint3x4(new Vector3(inner.x, bottomY, inner.z));
            innerTop[j] = worldToLocal.MultiplyPoint3x4(new Vector3(inner.x, topY, inner.z));
            arcU[j] = ArcAt(arcTable, t);
        }

        var vertices = new List<Vector3>();
        var uvs = new List<Vector2>();
        var triangles = new List<int>();

        // Texture V is anchored to world height so stone courses line up
        // across sections and across storeys; taller walls show more
        // courses rather than stretched ones.
        float vBottom = bottomY / baseWallHeight;
        float vTop = topY / baseWallHeight;

        // Each face gets its own vertices so RecalculateNormals keeps hard
        // edges between faces while smoothing along the sweep.
        void AddStrip(Vector3[] rowA, Vector3[] rowB, bool flip, float vA, float vB, float[] uA, float[] uB)
        {
            int baseIndex = vertices.Count;
            for (int j = 0; j <= MeshSegments; j++)
            {
                vertices.Add(rowA[j]);
                uvs.Add(new Vector2(uA[j], vA));
                vertices.Add(rowB[j]);
                uvs.Add(new Vector2(uB[j], vB));
            }
            for (int j = 0; j < MeshSegments; j++)
            {
                int a0 = baseIndex + j * 2;
                int b0 = a0 + 1;
                int a1 = a0 + 2;
                int b1 = a0 + 3;
                if (!flip)
                {
                    triangles.Add(a0); triangles.Add(a1); triangles.Add(b0);
                    triangles.Add(b0); triangles.Add(a1); triangles.Add(b1);
                }
                else
                {
                    triangles.Add(a0); triangles.Add(b0); triangles.Add(a1);
                    triangles.Add(b0); triangles.Add(b1); triangles.Add(a1);
                }
            }
        }

        void AddCap(int j, bool startCap)
        {
            int baseIndex = vertices.Count;
            vertices.Add(outerBottom[j]);
            uvs.Add(new Vector2(0f, vBottom));
            vertices.Add(outerTop[j]);
            uvs.Add(new Vector2(0f, vTop));
            vertices.Add(innerBottom[j]);
            uvs.Add(new Vector2(1f, vBottom));
            vertices.Add(innerTop[j]);
            uvs.Add(new Vector2(1f, vTop));
            if (startCap)
            {
                triangles.Add(baseIndex); triangles.Add(baseIndex + 1); triangles.Add(baseIndex + 2);
                triangles.Add(baseIndex + 2); triangles.Add(baseIndex + 1); triangles.Add(baseIndex + 3);
            }
            else
            {
                triangles.Add(baseIndex); triangles.Add(baseIndex + 2); triangles.Add(baseIndex + 1);
                triangles.Add(baseIndex + 1); triangles.Add(baseIndex + 2); triangles.Add(baseIndex + 3);
            }
        }

        AddStrip(outerBottom, outerTop, false, vBottom, vTop, arcU, arcU);
        AddStrip(innerBottom, innerTop, true, vBottom, vTop, arcU, arcU);
        AddStrip(outerTop, innerTop, false, 0f, 1f, arcU, arcU);
        AddStrip(outerBottom, innerBottom, true, 0f, 1f, arcU, arcU);
        AddCap(0, true);
        AddCap(MeshSegments, false);

        Mesh mesh = new Mesh { name = "WallSection" };
        mesh.SetVertices(vertices);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }
}
