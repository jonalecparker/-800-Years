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
    // What this wall is built OF (palisade, stone — the cost ladder).
    // Decides material and price, never geometry: surgery copies it, no
    // query filters on it, and a save carries it (CastleSave v5).
    public WallKind kind = WallKind.Stone;
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

    // A buttress standing against one face of this wall. STORED ON THE
    // EDGE for exactly the reasons an opening is: it is this wall's own
    // masonry, so it belongs to the data the geometry derives from, and it
    // travels through every split (CarryButtresses) the way a doorway does.
    //
    // `t` is the curve parameter of its CENTRE, which survives subdivision
    // linearly. `side` is +1 or -1 — which face it stands against, in the
    // sign SideOf answers with. `topY` is an ABSOLUTE elevation, captured
    // at the click: a stated fact, so it cannot drift when the ground is
    // re-shaped or the wall is cut in two. The BOTTOM is not stored — it is
    // the ground under the pier's own footprint (WallButtress.BottomAt),
    // the one thing an edge is always allowed to read.
    //
    // No strength number is stored, deliberately (user's call, 2026-08-16):
    // a buttress will make a wall harder to breach when there is a siege to
    // breach it, and inventing the figure before its consumer exists would
    // be guessing at a balance nothing can test.
    [System.Serializable]
    public struct Buttress
    {
        public float t;
        public float side;
        public float topY;
    }
    public List<Buttress> buttresses = new List<Buttress>();

    // A crenellated STRETCH of this wall's top — two curve parameters and
    // nothing else. Edge data for the same reason a doorway is: the notches
    // are cut in this masonry and no other.
    //
    // A SPAN rather than a per-edge flag (the design call, made before any
    // of it was written): a flag can only say "this whole wall", and a
    // chained run is many edges through no fault of the player's, so what a
    // flag meant would depend on where the graph put its nodes. A span means
    // the same stretch whatever the nodes do — and it is what lets a split
    // CLIP a battlement rather than destroy one.
    //
    // `height` and `gap` are the two things the player dials, so they are
    // stored: a battlement raised low and close-set stays low and close-set.
    // Zero means "whatever the default is", which is exactly what an old
    // save's missing field deserializes to — the omitted-field trap turned
    // into a safe answer.
    //
    // The COUNT and the exact spacing are NOT stored. They derive from the
    // span's own arc length by a rule that begins and ends solid
    // (WallCrenel.Layout), so a span clipped by a split re-lays out into a
    // battlement still correct at its new corner. A stored phase would be a
    // fact that could drift; a derived one cannot.
    [System.Serializable]
    public struct Crenel
    {
        public float t0;
        public float t1;
        public float height;
        public float gap;
    }
    public List<Crenel> crenels = new List<Crenel>();

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
        float baseY = float.NegativeInfinity, float skirt = 0f,
        WallKind kind = WallKind.Stone)
    {
        GameObject obj = new GameObject("WallEdge");
        obj.transform.SetParent(parent, false);
        WallEdge edge = obj.AddComponent<WallEdge>();
        edge.nodeA = a;
        edge.nodeB = b;
        edge.control = control;
        edge.height = height;
        edge.thickness = thickness;
        edge.kind = kind;
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
        foreach (WallButtress pier in GetComponentsInChildren<WallButtress>())
            DestroyImmediate(pier.gameObject);
        foreach (WallCrenel crenel in GetComponentsInChildren<WallCrenel>())
            DestroyImmediate(crenel.gameObject);

        List<SectionSpec> specs = BuildSpecs(A, control, B, height, targetSectionLength,
            thickness, baseStep, baseWallHeight, fixedTopY, FloorY, openings);
        foreach (SectionSpec spec in specs)
            CreateSection(spec);
        for (int i = 0; i < buttresses.Count; i++)
            CreateButtress(i);
        // After the sections, always: a merlon reads the wall tops it
        // stands on, and those are the sections that were just made.
        for (int i = 0; i < crenels.Count; i++)
            CreateCrenel(i);
        Physics.SyncTransforms();
    }

    // The battlement along one stretch of this wall's top, from the same
    // builder the ghost draws with. Its own object so the cutaway slices it
    // and the delete tool can pick it off the wall it crowns.
    void CreateCrenel(int index)
    {
        if (!WallCrenel.TryBuild(this, crenels[index], out Mesh mesh))
            return;
        WallCrenel crenel = WallCrenel.Spawn(transform, material, mesh);
        crenel.edge = this;
        crenel.index = index;
    }

    // A pier against this wall's face, from the same builder the ghost
    // draws with. Its own object, so the cutaway slices it like any other
    // prism and the delete tool can pick it out of the masonry behind it.
    void CreateButtress(int index)
    {
        if (!WallButtress.TryBuild(this, buttresses[index], out Mesh mesh,
                out Vector3 position, out float yaw, out float bottomY, out float topY))
            return;
        WallButtress pier = WallButtress.Spawn(transform, material, mesh, position, yaw,
            WallButtress.Width * 0.5f, 0f, thickness * 0.5f + WallButtress.Projection,
            bottomY, topY);
        pier.edge = this;
        pier.index = index;
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
    // EVERY mutator that makes sub-edges must call this — both surgery
    // sites in WallGraph do. It carries BOTH kinds of edge data in one
    // pass so the sub-edge is rebuilt once, however much of it survived.
    public static void CarryDetails(WallEdge from, WallEdge to, float t0, float t1)
    {
        if (t1 - t0 < 0.001f)
            return;
        bool any = CarryOpenings(from, to, t0, t1);
        any |= CarryButtresses(from, to, t0, t1);
        any |= CarryCrenels(from, to, t0, t1);
        if (any)
            to.Rebuild();
    }

    // A battlement is CLIPPED by a split, not dropped — and that is the
    // deliberate difference from an opening and a pier. Those are single
    // objects with a width: half a doorway either side of a new node is not
    // what anyone drew, so a split through one destroys it. A crenellated
    // stretch is not one object; it is a length of wall that happens to be
    // notched, and a wall crossing it halfway leaves both halves just as
    // notched as they were. So the span is intersected with the surviving
    // piece and remapped, and only a remnant too short to hold a battlement
    // at all is let go.
    static bool CarryCrenels(WallEdge from, WallEdge to, float t0, float t1)
    {
        if (from.crenels.Count == 0)
            return false;
        float[] table = BuildArcTable(from.A, from.control, from.B);
        bool any = false;
        foreach (Crenel c in from.crenels)
        {
            float lo = Mathf.Max(t0, Mathf.Min(c.t0, c.t1));
            float hi = Mathf.Min(t1, Mathf.Max(c.t0, c.t1));
            if (hi - lo < 0.0001f)
                continue;
            if (ArcAt(table, hi) - ArcAt(table, lo) < WallCrenel.MinSpan)
                continue;
            to.crenels.Add(new Crenel
            {
                t0 = (lo - t0) / (t1 - t0),
                t1 = (hi - t0) / (t1 - t0),
                height = c.height,
                gap = c.gap,
            });
            any = true;
        }
        return any;
    }

    static bool CarryOpenings(WallEdge from, WallEdge to, float t0, float t1)
    {
        if (from.openings.Count == 0)
            return false;
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
        return any;
    }

    // Same rule as an opening's: a pier the split runs THROUGH doesn't
    // survive — half a buttress on each side of a new node is not what
    // anyone placed. Its topY is absolute, so a split leaves it exactly
    // where it stood; only the parameter is remapped.
    static bool CarryButtresses(WallEdge from, WallEdge to, float t0, float t1)
    {
        if (from.buttresses.Count == 0)
            return false;
        float[] table = BuildArcTable(from.A, from.control, from.B);
        float a0 = ArcAt(table, t0);
        float a1 = ArcAt(table, t1);
        float half = WallButtress.Width * 0.5f;
        bool any = false;
        foreach (Buttress b in from.buttresses)
        {
            float mid = ArcAt(table, Mathf.Clamp01(b.t));
            if (mid - half < a0 - 0.05f || mid + half > a1 + 0.05f)
                continue;
            to.buttresses.Add(new Buttress
            {
                t = (b.t - t0) / (t1 - t0),
                side = b.side,
                topY = b.topY,
            });
            any = true;
        }
        return any;
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
    // stringCourses is off for anything that isn't a plain wall face: a
    // stair's wedge is built from these same specs, and a band running
    // through a flight of steps is a horizontal line across a diagonal —
    // the one place the feature reads as a mistake.
    public static bool BuildSpecFrame(ref SectionSpec spec, Vector3 s, Vector3 c, Vector3 e,
        float thickness, float baseWallHeight, float[] arcTable,
        bool stringCourses = true)
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
        // Openings are NOT excluded, and excluding them was wrong: a
        // lintel and a threshold are masonry — the stone over and under
        // the hole — so a course must cross them like any other wall. The
        // hole itself needs no rule at all, because no section exists
        // there to grow one. Suppressing by openingIndex dropped the
        // course over the WHOLE height above every window, since a lintel
        // is one section running from the head to the wall top.
        spec.mesh = BuildSectionMesh(s, c, e, spec, arcTable, thickness, baseWallHeight,
            spec.position, spec.yaw, stringCourses);
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
        if (!Ground.Any)
            return 0f;

        float min = float.MaxValue;
        for (int i = 0; i <= 4; i++)
        {
            float t = Mathf.Lerp(t0, t1, i / 4f);
            Vector3 p = Evaluate(s, c, e, t);
            Vector3 side = Tangent(s, c, e, t).normalized;
            side = new Vector3(-side.z, 0f, side.x) * (thickness * 0.5f);
            min = Mathf.Min(min, Mathf.Min(Ground.HeightAt(p + side), Ground.HeightAt(p - side)));
        }

        float ground = min;
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

    // The string course: a band of masonry standing proud of the wall,
    // marking a level. Kept deliberately shallow — this is here to put a
    // horizontal shadow line on a tall face so the eye reads storeys
    // instead of counting stones, and anything bolder starts competing
    // with the architecture it is meant to serve.
    // Proud is the number that matters and 60mm was not enough: the band
    // exists to cast a SHADOW, and at any real viewing distance a 60mm
    // ledge under a 32° sun casts one about a pixel wide. 120mm out and
    // 300mm tall is still modest for a real string course and actually
    // reads from across the bailey.
    public const float StringCourseSpacing = 4f;
    public const float StringCourseHeight = 0.3f;
    public const float StringCourseProud = 0.12f;

    // Which slices of [lo, hi] a string course occupies — clipped to the
    // masonry it is given, so a band that only half fits comes out half
    // height instead of vanishing. The margins used to be a full
    // StringCourseHeight rather than half of one, which quietly refused
    // any band whose centre sat within 300mm of a section's own bottom or
    // top: that is why the course disappeared exactly where it crossed a
    // lintel (whose bottom IS the door head) and why it stopped partway
    // up a castle of stacked storeys, each new storey's floor cutting the
    // band above it. Shared with WallNode so a joint carries the course
    // across itself instead of leaving a post-shaped hole in it.
    public static List<(float lo, float hi)> StringCourseBands(float lo, float hi)
    {
        var bands = new List<(float, float)>();
        int first = Mathf.FloorToInt(lo / StringCourseSpacing);
        int last = Mathf.CeilToInt(hi / StringCourseSpacing);
        for (int band = first; band <= last; band++)
        {
            float at = band * StringCourseSpacing;
            float a = Mathf.Max(lo, at - StringCourseHeight * 0.5f);
            float b = Mathf.Min(hi, at + StringCourseHeight * 0.5f);
            if (b - a > 0.02f)
                bands.Add((a, b));
        }
        return bands;
    }

    // A stable pseudo-random slide along U for one edge, in metres, from
    // the edge's own stored endpoints — no state, no seeding, same answer
    // on every rebuild and after every load. Ranges over 4m so it shifts
    // by more than a whole tile of any sane stone texture.
    static float UPhase(Vector3 s, Vector3 e)
    {
        unchecked
        {
            int h = 17;
            h = h * 31 + Mathf.RoundToInt(s.x * 100f);
            h = h * 31 + Mathf.RoundToInt(s.z * 100f);
            h = h * 31 + Mathf.RoundToInt(e.x * 100f);
            h = h * 31 + Mathf.RoundToInt(e.z * 100f);
            return ((h & 0xFFFF) / 65535f) * 4f;
        }
    }

    // Sweeps the wall's rectangular cross-section along one section's
    // slice of the curve. Neighbouring sections evaluate the same curve at
    // the same boundary parameter, so their end faces are identical and
    // the finished wall reads as one continuous mesh. Vertices are baked
    // into the section's local frame (position + yaw, no scale).
    static Mesh BuildSectionMesh(Vector3 s, Vector3 c, Vector3 e,
        SectionSpec spec, float[] arcTable, float thickness, float baseWallHeight,
        Vector3 center, float yaw, bool stringCourses)
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
        var tAt = new float[MeshSegments + 1];
        var centreline = new Vector3[MeshSegments + 1];
        var sideDir = new Vector3[MeshSegments + 1];
        // Every edge starts its own U at zero, so the same stretch of
        // texture begins at every wall in the castle and the eye reads
        // the repeat instantly. Sliding each edge along U by a fixed
        // amount of its own breaks that without costing anything: the
        // phase is derived from the edge's endpoints, so it is identical
        // for every section of one edge (they all receive the whole
        // curve here), survives save and load, and never wanders between
        // rebuilds. Only U moves — V stays world height, because courses
        // lining up with the masonry next door is the property worth
        // keeping, and real walls break their vertical joints anyway.
        float phase = UPhase(s, e);

        for (int j = 0; j <= MeshSegments; j++)
        {
            float t = Mathf.Lerp(spec.tStart, spec.tEnd, (float)j / MeshSegments);
            Vector3 p = Evaluate(s, c, e, t);
            Vector3 side = Tangent(s, c, e, t).normalized;
            side = new Vector3(-side.z, 0f, side.x);
            tAt[j] = t;
            centreline[j] = new Vector3(p.x, 0f, p.z);
            sideDir[j] = side;
            arcU[j] = ArcAt(arcTable, t) + phase;
        }

        // The four corner rows for ONE prism swept along this slice at a
        // given half-width and height range. Filling them per prism is
        // what lets a string course ride the same curve as the wall it
        // sits on, exactly concentric, with no second sweep to keep in
        // step.
        void Rows(float hw, float yLo, float yHi)
        {
            for (int j = 0; j <= MeshSegments; j++)
            {
                Vector3 off = sideDir[j] * hw;
                Vector3 outer = centreline[j] + off;
                Vector3 inner = centreline[j] - off;
                outerBottom[j] = worldToLocal.MultiplyPoint3x4(new Vector3(outer.x, yLo, outer.z));
                outerTop[j] = worldToLocal.MultiplyPoint3x4(new Vector3(outer.x, yHi, outer.z));
                innerBottom[j] = worldToLocal.MultiplyPoint3x4(new Vector3(inner.x, yLo, inner.z));
                innerTop[j] = worldToLocal.MultiplyPoint3x4(new Vector3(inner.x, yHi, inner.z));
            }
        }

        var vertices = new List<Vector3>();
        var uvs = new List<Vector2>();
        var triangles = new List<int>();

        // BOTH UV axes are METRES of real masonry (2026-08-16): U is arc
        // length, V is world height. V used to divide by baseWallHeight,
        // which made vertical texel density depend on a height constant —
        // one stone came out 3.5× taller than it was wide, and the
        // material's y-scale could only paper over it globally. In metres
        // a texture states its own physical size ONCE, in the material's
        // tiling, and every surface agrees. V stays world-anchored either
        // way, which is what keeps courses lined up across sections and
        // across storeys.

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

        // The end cap runs across the wall's THICKNESS, so its U spans
        // that many metres — not 0→1, which stretched a whole tile of
        // stone across however thick the wall happened to be.
        void AddCap(int j, bool startCap, float vLo, float vHi, float across)
        {
            int baseIndex = vertices.Count;
            vertices.Add(outerBottom[j]);
            uvs.Add(new Vector2(0f, vLo));
            vertices.Add(outerTop[j]);
            uvs.Add(new Vector2(0f, vHi));
            vertices.Add(innerBottom[j]);
            uvs.Add(new Vector2(across, vLo));
            vertices.Add(innerTop[j]);
            uvs.Add(new Vector2(across, vHi));
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

        void EmitPrism(float hw, float yLo, float yHi)
        {
            Rows(hw, yLo, yHi);
            AddStrip(outerBottom, outerTop, false, yLo, yHi, arcU, arcU);
            AddStrip(innerBottom, innerTop, true, yLo, yHi, arcU, arcU);
            // Top and underside run across the thickness, so their V is
            // metres for the same reason the cap's U is.
            AddStrip(outerTop, innerTop, false, 0f, hw * 2f, arcU, arcU);
            AddStrip(outerBottom, innerBottom, true, 0f, hw * 2f, arcU, arcU);
            AddCap(0, true, yLo, yHi, hw * 2f);
            AddCap(MeshSegments, false, yLo, yHi, hw * 2f);
        }

        EmitPrism(halfWidth, bottomY, topY);

        // The string course: a shallow band proud of the wall on both
        // faces, at every StringCourseSpacing of ABSOLUTE elevation. World
        // height is the anchor for the same reason the texture's V is —
        // two walls built years apart, at different bases, on different
        // storeys, still band at the same heights, so the castle reads as
        // one building. The alternative (banding at floor level) is the
        // historically correct one and the wrong one here: a wall would
        // have to consult the slabs around it, and then it rebuilds when a
        // neighbour changes, which is the one thing edge geometry never
        // does.
        //
        // It is a separate concentric prism rather than a bulge in the
        // wall's own cross-section: the wall's faces simply pass INSIDE
        // it, hidden, which costs a few buried triangles and saves the
        // sweep from carrying a profile. Openings need no special case —
        // a lintel or threshold section is not masonry and is skipped by
        // the caller.
        if (stringCourses && topY - bottomY > 0.05f)
            foreach ((float lo, float hi) in StringCourseBands(bottomY, topY))
                EmitPrism(halfWidth + StringCourseProud, lo, hi);

        Mesh mesh = new Mesh { name = "WallSection" };
        mesh.SetVertices(vertices);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateNormals();
        // The tangent frame the parallax march needs. Runtime meshes carry
        // NO tangent stream unless one is asked for, and the wall material
        // does parallax occlusion mapping, which ray-marches in TANGENT
        // space — with nothing to march along it eats faces at some
        // viewing angles and not others. It showed up on the stairs first
        // (they borrow this sweep) but every wall had it. Unity's own
        // handedness sign rides in tangent.w, so mirrored faces come out
        // right too. Any new mesh wearing this material owes this line.
        mesh.RecalculateTangents();
        mesh.RecalculateBounds();
        return mesh;
    }
}
