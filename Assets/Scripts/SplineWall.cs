using System.Collections.Generic;
using UnityEngine;

// A wall as a first-class object: one quadratic Bézier cut into uniform
// arc-length sections, each a swept-mesh child with its own collider.
// Walls live off the grid — endpoints go anywhere, and snapping is the
// placement tool's job — so section boundaries are pure masonry divisions
// (all equal within a wall) instead of grid-cell accidents. Occupancy is
// answered with physics overlap tests against section colliders.
//
// Junctions are geometric, not interpenetrated: where this wall's sweep
// passes inside an older wall's body at a crossing angle, that interval
// is a "junction band" — sections inside it are dropped (the older wall's
// masonry fills the space) and sections straddling its edges are trimmed
// so their cut caps sit flush on the older wall's face.
public class SplineWall : MonoBehaviour
{
    // Active walls, used for endpoint snapping and course stacking.
    public static readonly List<SplineWall> All = new List<SplineWall>();

    public Vector3 curveStart;
    public Vector3 curveControl;
    public Vector3 curveEnd;
    public float height;
    public float thickness;
    // Texture-V anchor: one tile of stone per this much world height, so
    // every wall and course shares aligned masonry lines.
    public float baseWallHeight = 3.5f;
    // Build inputs recorded at commit, so sections can be re-derived from
    // the curve later (RebuildSections).
    public float targetSectionLength = 1f;
    public float baseStep = 0.5f;
    // Stacked courses copy their cuts and bases from the wall below; they
    // are never re-derived from terrain and probes, so they don't rebuild.
    public bool isStackedCourse;
    // Set once a delete has punched sections out of this wall — a rebuild
    // would resurrect the holes, so a gapped wall keeps its geometry.
    public bool hasGaps;
    // Locked-top walls: every section's top sits at this absolute height
    // and only the bottom follows the terrain down — a level parapet on a
    // slope. -Infinity (the default) means tops ride the ground instead
    // (bottom + height per section).
    public float fixedTopY = float.NegativeInfinity;
    // Commit order. Junction trimming is asymmetric and stable: the newer
    // wall always trims against the older wall's body, never the reverse,
    // so rebuilds can't eat a hole from both sides of a crossing.
    public int buildOrder;

    // The walls this wall physically meets — burials, crossings, flush
    // T-joints, chained endpoints, and stacked courses all land here.
    // Connections are stored fact, not re-inferred from geometry, so
    // edits can re-resolve the neighbourhood (and structural rules can
    // later walk the network).
    public readonly HashSet<SplineWall> Links = new HashSet<SplineWall>();

    Material material;

    static int nextBuildOrder = 1;

    const int MeshSegments = 8;
    // Walls meeting at less than this angle are a duplicate/continuation
    // (blocked or buried), at or above it a junction (trimmed). This is
    // not a placement restriction — it's the discriminator between "these
    // cross" and "this is the same wall drawn again / a lateral overlap",
    // which has no sane joint geometry. Kept as small as the trim solver
    // can handle: below ~10° a junction band grows past 5.7m of dropped
    // wall and the distinction stops meaning anything.
    const float JunctionAngle = 10f;

    public struct SectionSpec
    {
        public int index;
        public float tStart;
        public float tEnd;
        // Actual sweep range — runs past tStart/tEnd where a junction cut
        // needs the far side of the cross-section to continue until it
        // too reaches the host's face plane (shallow crossings).
        public float tSweepStart;
        public float tSweepEnd;
        // Junction cut planes (vertical, inset a hair into their host so
        // clipped faces hide inside it instead of z-fighting its surface).
        // Mesh rows are clipped against every plane — each keeps its
        // positive side — so cut edges follow the hosts' faces exactly,
        // including where several hosts meet at one junction. Null when
        // the piece has no cuts.
        public List<Vector3> cutPoints;
        public List<Vector3> cutNormals;
        public float arcLength;
        public Vector3 position;
        public float yaw;
        public float bottomY;
        public float topY;
        public Mesh mesh;
        // True when this piece would duplicate an existing wall. Blocked
        // specs only exist when a preview asks for them (so the ghost can
        // show red instead of vanishing) — Create skips and disposes
        // them, they never become sections.
        public bool blocked;
    }

    // One junction band: the arc interval of a sweep that lies inside an
    // older wall's body, with the face planes bounding it. Graze bands
    // come from the side lanes — the centerline stayed outside the host
    // but one face dipped in; they only clip, never drop sections.
    class Band
    {
        public SplineWall wall;
        public bool graze;
        public float aIn, aOut;
        public bool hasIn, hasOut;
        public Vector3 pIn, nIn, pOut, nOut;
        // True once the host has appeared under the crossing-angle gate —
        // only such bands may stay open through the body on the geometric
        // hysteresis. A band that opened parallel in the gate-free end
        // zone dies at the zone boundary instead, so a redraw entering
        // through a cap refuses red rather than swallowing the wall.
        public bool gateQualified;
    }

    void OnEnable() { All.Add(this); }

    void OnDisable()
    {
        All.Remove(this);
        foreach (SplineWall other in Links)
            if (other != null)
                other.Links.Remove(this);
        Links.Clear();
    }

    public static SplineWall Create(Transform parent, Vector3 s, Vector3 control, Vector3 e,
        float height, float thickness, float baseWallHeight, Material material, List<SectionSpec> specs,
        float targetSectionLength, float baseStep, bool isStackedCourse, float fixedTopY = float.NegativeInfinity)
    {
        GameObject wallObj = new GameObject("SplineWall");
        wallObj.transform.SetParent(parent, false);
        SplineWall wall = wallObj.AddComponent<SplineWall>();
        wall.curveStart = s;
        wall.curveControl = control;
        wall.curveEnd = e;
        wall.height = height;
        wall.thickness = thickness;
        wall.baseWallHeight = baseWallHeight;
        wall.targetSectionLength = targetSectionLength;
        wall.baseStep = baseStep;
        wall.isStackedCourse = isStackedCourse;
        wall.fixedTopY = fixedTopY;
        wall.material = material;
        wall.buildOrder = nextBuildOrder++;

        foreach (SectionSpec spec in specs)
        {
            if (spec.blocked)
            {
                if (spec.mesh != null)
                    Destroy(spec.mesh);
                continue;
            }
            wall.CreateSection(spec, material);
        }
        Physics.SyncTransforms();
        wall.ResolveLinks();
        return wall;
    }

    void CreateSection(SectionSpec spec, Material material)
    {
        GameObject sectionObj = new GameObject("WallSection");
        sectionObj.transform.SetParent(transform, false);
        sectionObj.transform.SetPositionAndRotation(spec.position, Quaternion.Euler(0f, spec.yaw, 0f));

        sectionObj.AddComponent<MeshFilter>().sharedMesh = spec.mesh;
        sectionObj.AddComponent<MeshRenderer>().sharedMaterial = material;

        // Box approximation of the (slightly curved) slice — plenty for
        // raycast targeting and occupancy overlap tests.
        BoxCollider box = sectionObj.AddComponent<BoxCollider>();
        box.size = new Vector3(spec.arcLength, spec.topY - spec.bottomY, thickness);

        SplineWallSection section = sectionObj.AddComponent<SplineWallSection>();
        section.wall = this;
        section.index = spec.index;
        section.tStart = spec.tStart;
        section.tEnd = spec.tEnd;
        section.tSweepStart = spec.tSweepStart;
        section.tSweepEnd = spec.tSweepEnd;
        section.cutPoints = spec.cutPoints;
        section.cutNormals = spec.cutNormals;
        section.bottomY = spec.bottomY;
        section.topY = spec.topY;
        section.ownedMesh = spec.mesh;
    }

    // Records which walls this wall's sections physically meet, as a
    // bidirectional set on both walls. The overlap boxes are expanded a
    // hair so flush contact counts: trimmed junction stubs touching an
    // older wall's face, courses resting on the wall below, and walls
    // sharing a face across a cell boundary are all real connections.
    public void ResolveLinks()
    {
        foreach (SplineWall other in Links)
            if (other != null)
                other.Links.Remove(this);
        Links.Clear();

        foreach (SplineWallSection section in GetComponentsInChildren<SplineWallSection>())
        {
            BoxCollider box = section.GetComponent<BoxCollider>();
            if (box == null)
                continue;
            foreach (Collider c in Physics.OverlapBox(section.transform.position,
                box.size * 0.5f + Vector3.one * 0.04f, section.transform.rotation))
            {
                SplineWallSection other = c.GetComponent<SplineWallSection>();
                if (other == null || other.wall == this || other.wall == null)
                    continue;
                Links.Add(other.wall);
                other.wall.Links.Add(this);
            }
        }
    }

    // Re-derives this wall's sections from its stored curve and build
    // inputs, re-probing the world — when a wall this one buried its end
    // into is removed, the sweep retracts honestly, and when a wall this
    // one was trimmed against is removed, the junction gap grows back
    // solid. Stacked courses and gapped walls keep their geometry: a
    // course's cuts belong to the wall below it, and a rebuild would
    // resurrect a gapped wall's holes.
    public void RebuildSections()
    {
        if (isStackedCourse || hasGaps)
            return;

        foreach (SplineWallSection section in GetComponentsInChildren<SplineWallSection>())
            DestroyImmediate(section.gameObject);
        Physics.SyncTransforms();

        foreach (SectionSpec spec in BuildSpecs(curveStart, curveControl, curveEnd,
            height, targetSectionLength, thickness, baseStep, baseWallHeight, buildOrder, false, fixedTopY))
            CreateSection(spec, material);
        Physics.SyncTransforms();
    }

    // Sections for a fresh wall on the ground. The cut count is chosen so
    // every section comes out the same arc length, as close to the target
    // as the total divides into — no skinny slivers, ever. The sweep runs
    // PAST both endpoints: half a cell so the picked cells are fully
    // covered (endpoints are cell centers), and deeper when an existing
    // wall stands within a cell of an end — the sweep overshoots into it
    // and the junction band trims it back flush to that wall's face.
    // buildOrder is the committing wall's place in line (int.MaxValue for
    // previews and new walls): only OLDER walls trim this sweep.
    public static List<SectionSpec> BuildSpecs(Vector3 s, Vector3 control, Vector3 e,
        float height, float targetSectionLength, float thickness, float baseStep, float baseWallHeight,
        int buildOrder = int.MaxValue, bool includeBlocked = false, float fixedTopY = float.NegativeInfinity)
    {
        var specs = new List<SectionSpec>();
        float[] arcTable = BuildArcTable(s, control, e);
        float totalArc = arcTable[arcTable.Length - 1];
        if (totalArc < 0.05f)
            return specs;

        float startExt = EndExtension(s, control, e, arcTable, true, targetSectionLength);
        float endExt = EndExtension(s, control, e, arcTable, false, targetSectionLength);

        float fullLength = startExt + totalArc + endExt;
        int count = Mathf.Max(1, Mathf.RoundToInt(fullLength / Mathf.Max(0.1f, targetSectionLength)));
        float sectionArc = fullLength / count;

        List<Band> bands = FindJunctionBands(s, control, e, arcTable, -startExt, totalArc + endExt, thickness, buildOrder);

        // Bands are per-host and can overlap where several hosts meet at
        // one junction; sections are carved against their arc UNION, while
        // every piece keeps each individual band's face planes to clip by.
        // Graze bands only clip — they never remove sections.
        var merged = new List<Vector2>();
        foreach (Band band in bands)
        {
            if (band.graze)
                continue;
            if (merged.Count > 0 && band.aIn <= merged[merged.Count - 1].y + 0.001f)
            {
                Vector2 last = merged[merged.Count - 1];
                last.y = Mathf.Max(last.y, band.aOut);
                merged[merged.Count - 1] = last;
            }
            else
            {
                merged.Add(new Vector2(band.aIn, band.aOut));
            }
        }

        for (int i = 0; i < count; i++)
        {
            float a0 = i * sectionArc - startExt;
            float a1 = (i + 1) * sectionArc - startExt;

            // Pieces wholly inside a band interval never emit — the older
            // walls' masonry fills that space.
            float cursor = a0;
            foreach (Vector2 m in merged)
            {
                if (m.y <= cursor + 0.001f || m.x >= a1 - 0.001f)
                    continue;
                if (m.x > cursor + 0.001f)
                    EmitPiece(specs, s, control, e, i, cursor, m.x, bands, totalArc,
                        height, thickness, baseStep, baseWallHeight, arcTable, includeBlocked, fixedTopY);
                cursor = Mathf.Max(cursor, m.y);
                if (cursor >= a1)
                    break;
            }
            if (cursor < a1 - 0.01f)
                EmitPiece(specs, s, control, e, i, cursor, a1, bands, totalArc,
                    height, thickness, baseStep, baseWallHeight, arcTable, includeBlocked, fixedTopY);
        }
        return specs;
    }

    // One kept piece of a section. Face planes from every band behind or
    // ahead of the piece ride along on the spec (a distant plane clips as
    // a no-op — at shallow angles a face cuts into pieces several
    // sections from the band itself, and at multi-wall junctions a piece
    // needs several planes at once). A band ADJACENT to the piece also
    // extends the sweep to where the last side of the cross-section
    // reaches that host's face — well past the centerline crossing at
    // shallow angles — so the clip has geometry to carve the diagonal
    // from.
    static void EmitPiece(List<SectionSpec> specs, Vector3 s, Vector3 control, Vector3 e, int index,
        float k0, float k1, List<Band> bands, float totalArc,
        float height, float thickness, float baseStep, float baseWallHeight, float[] arcTable,
        bool includeBlocked, float fixedTopY)
    {
        if (k1 - k0 < 0.08f)
            return;

        // Cut planes sink a hair into the host so clipped faces hide
        // inside its body instead of z-fighting its surface.
        const float CutInset = 0.015f;
        float half = thickness * 0.5f;

        var spec = new SectionSpec
        {
            index = index,
            tStart = TFromExtendedArc(arcTable, s, control, e, k0),
            tEnd = TFromExtendedArc(arcTable, s, control, e, k1),
        };

        float sweepStartArc = k0;
        float sweepEndArc = k1;
        bool bandAtEnd = false;
        bool bandAtStart = false;
        foreach (Band band in bands)
        {
            if (band.graze)
            {
                // Grazes only clip: the sliver of cross-section that dips
                // into the host gets pushed back out to its face.
                if (band.hasIn && band.aOut >= k0 - 1f && band.aIn <= k1 + 1f)
                    AddCut(ref spec, band.pIn - band.nIn * CutInset, band.nIn);
                continue;
            }
            if (band.hasIn && band.aIn >= k1 - 0.02f)
            {
                Vector3 planeP = band.pIn - band.nIn * CutInset;
                AddCut(ref spec, planeP, band.nIn);
                if (band.aIn <= k1 + 0.02f)
                {
                    bandAtEnd = true;
                    sweepEndArc = Mathf.Max(sweepEndArc, Mathf.Max(
                        SideCrossing(s, control, e, arcTable, +half, k1, planeP, band.nIn),
                        SideCrossing(s, control, e, arcTable, -half, k1, planeP, band.nIn)));
                }
            }
            if (band.hasOut && band.aOut <= k0 + 0.02f)
            {
                Vector3 planeP = band.pOut - band.nOut * CutInset;
                AddCut(ref spec, planeP, band.nOut);
                if (band.aOut >= k0 - 0.02f)
                {
                    bandAtStart = true;
                    sweepStartArc = Mathf.Min(sweepStartArc, Mathf.Min(
                        SideCrossing(s, control, e, arcTable, +half, k0, planeP, band.nOut),
                        SideCrossing(s, control, e, arcTable, -half, k0, planeP, band.nOut)));
                }
            }
        }
        // Extension crumbs: a piece living entirely past an endpoint and
        // severed from the wall's body by a junction band is an orphan
        // the overshoot left on the far side of a corner — drop it.
        if (bandAtEnd && k1 <= 0.001f)
            return;
        if (bandAtStart && k0 >= totalArc - 0.001f)
            return;
        if (sweepEndArc - sweepStartArc < 0.03f)
            return;

        spec.tSweepStart = TFromExtendedArc(arcTable, s, control, e, sweepStartArc);
        spec.tSweepEnd = TFromExtendedArc(arcTable, s, control, e, sweepEndArc);
        spec.bottomY = SampleSliceGround(s, control, e, spec.tSweepStart, spec.tSweepEnd, thickness, baseStep);
        // A piece cut by an adjacent band deliberately penetrates its
        // host at that end (inset + side-crossing overshoot) — pull the
        // occupancy range back from the cut so the burial can't read as
        // a near-parallel duplicate and drop the piece.
        bool cutAdjacent = bandAtStart || bandAtEnd;
        float blockArc0 = bandAtStart ? k0 + 0.3f : sweepStartArc;
        float blockArc1 = bandAtEnd ? k1 - 0.3f : sweepEndArc;
        AddSpecIfClear(specs, s, control, e, spec, height, fixedTopY, thickness, baseWallHeight, arcTable, includeBlocked,
            cutAdjacent ? blockArc0 : float.NaN, cutAdjacent ? blockArc1 : float.NaN);
    }

    static void AddCut(ref SectionSpec spec, Vector3 point, Vector3 normal)
    {
        spec.cutPoints ??= new List<Vector3>();
        spec.cutNormals ??= new List<Vector3>();
        spec.cutPoints.Add(point);
        spec.cutNormals.Add(normal);
    }

    // Where one side face (the offset curve at ±halfWidth) crosses a
    // vertical face plane, in arc length — searched around the coarse
    // centerline crossing, since a skewed crossing shifts each side by up
    // to halfWidth/tan(angle) along the run.
    static float SideCrossing(Vector3 s, Vector3 control, Vector3 e, float[] arcTable,
        float sideOffset, float coarse, Vector3 planePoint, Vector3 planeNormal)
    {
        float SignedDist(float arc)
        {
            float t = TFromExtendedArc(arcTable, s, control, e, arc);
            Vector3 p = Evaluate(s, control, e, t);
            Vector3 side = Tangent(s, control, e, t).normalized;
            Vector3 offset = new Vector3(-side.z, 0f, side.x) * sideOffset;
            return (p.x + offset.x - planePoint.x) * planeNormal.x
                 + (p.z + offset.z - planePoint.z) * planeNormal.z;
        }

        // The shallower the crossing, the further the side crossing shifts
        // from the centerline crossing (halfWidth / tan of the crossing
        // angle) — size the search reach from the actual face-vs-tangent
        // angle so 10° junctions still cut flush.
        Vector3 tan0 = Tangent(s, control, e,
            TFromExtendedArc(arcTable, s, control, e, coarse)).normalized;
        float along = Mathf.Abs(tan0.x * planeNormal.x + tan0.z * planeNormal.z);
        float reach = Mathf.Abs(sideOffset) / Mathf.Max(0.12f, along) + 0.6f;
        float best = coarse;
        float bestDelta = float.MaxValue;
        float prevArc = coarse - reach;
        float prevD = SignedDist(prevArc);
        for (float arc = prevArc + 0.09f; arc <= coarse + reach + 0.045f; arc += 0.09f)
        {
            float d = SignedDist(arc);
            if ((prevD > 0f) != (d > 0f))
            {
                float lo = prevArc, hi = arc, dLo = prevD;
                for (int it = 0; it < 18; it++)
                {
                    float mid = (lo + hi) * 0.5f;
                    float dm = SignedDist(mid);
                    if ((dLo > 0f) == (dm > 0f)) { lo = mid; dLo = dm; }
                    else hi = mid;
                }
                float cross = (lo + hi) * 0.5f;
                float delta = Mathf.Abs(cross - coarse);
                if (delta < bestDelta) { bestDelta = delta; best = cross; }
            }
            prevArc = arc;
            prevD = d;
        }
        return best;
    }

    // Scans the sweep for intervals where the centerline sits inside an
    // older wall's body at a junction angle. Face planes are read off the
    // older wall's own colliders with short raycasts across each
    // transition, so curved hosts and box approximations both just work.
    static List<Band> FindJunctionBands(Vector3 s, Vector3 control, Vector3 e, float[] arcTable,
        float aMin, float aMax, float thickness, int buildOrder)
    {
        Terrain terrain = Terrain.activeTerrain;
        const float step = 0.06f;
        float totalArc = arcTable[arcTable.Length - 1];

        Vector3 ProbeAt(float arc, float sideOffset)
        {
            float t = TFromExtendedArc(arcTable, s, control, e, arc);
            Vector3 p = Evaluate(s, control, e, t);
            if (sideOffset != 0f)
            {
                Vector3 dir = Tangent(s, control, e, t).normalized;
                p += new Vector3(-dir.z, 0f, dir.x) * sideOffset;
            }
            float ground = terrain != null ? terrain.transform.position.y + terrain.SampleHeight(p) : 0f;
            return new Vector3(p.x, ground + 0.75f, p.z);
        }

        // Hosts whose body contains the centerline at this arc — tracked
        // per wall, so overlapping crossings (several hosts meeting at
        // one junction) each get their own band and their own faces. The
        // near-point radius matters: a sample only counts as inside when
        // the centerline truly is, or the face raycast starts from a
        // sample that never entered the host and misses the face plane.
        HashSet<SplineWall> HostsAt(float arc, float sideOffset, bool requireAngle)
        {
            float t = TFromExtendedArc(arcTable, s, control, e, arc);
            Vector3 tanDir = Tangent(s, control, e, t).normalized;
            var hosts = new HashSet<SplineWall>();
            foreach (Collider c in Physics.OverlapSphere(ProbeAt(arc, sideOffset), 0.01f))
                if (IsCrossingHost(c, tanDir, buildOrder, requireAngle))
                    hosts.Add(c.GetComponent<SplineWallSection>().wall);
            return hosts;
        }

        // A raycast face is only the host's tangent plane at one hit point.
        // On a convex curve the real face bows away from that plane, so a
        // cap clipped to it sits proud of the masonry by the sagitta —
        // visible daylight at the joint. Measure the host's TRUE face
        // analytically (its curve offset by half its thickness — the
        // colliders are box approximations with cracks and interior caps,
        // useless for this) across the lateral span the cut will occupy,
        // and return the deepest recession behind the plane; the caller
        // sinks the plane by it, burying the cap inside the curve —
        // CutInset's trick, made curvature-aware. Flat faces and host end
        // caps measure zero and stay put.
        float FaceRecession(Vector3 point, Vector3 normal, float reach, SplineWall host)
        {
            const float MaxSink = 0.25f;
            if (reach < 0.01f || host == null)
                return 0f;
            Vector3 hs = host.curveStart, hc = host.curveControl, he = host.curveEnd;

            // Closest centerline parameter to the plane anchor, a little
            // past the ends to cover end-extension geometry.
            float bestT = 0f, bestSq = float.MaxValue;
            for (float t = -0.2f; t <= 1.2001f; t += 0.01f)
            {
                Vector3 q = Evaluate(hs, hc, he, t);
                float dx = point.x - q.x, dz = point.z - q.z;
                float sq = dx * dx + dz * dz;
                if (sq < bestSq) { bestSq = sq; bestT = t; }
            }
            Vector3 c0 = Evaluate(hs, hc, he, bestT);
            Vector3 tan0 = Tangent(hs, hc, he, bestT).normalized;
            float half = host.thickness * 0.5f;
            // Only flank faces curve: anchor off the flank (an end cap) or
            // a plane whose normal runs along the host are flat — no sink.
            if (Mathf.Abs(Mathf.Sqrt(bestSq) - half) > 0.12f)
                return 0f;
            if (Mathf.Abs(normal.x * tan0.x + normal.z * tan0.z) > 0.5f)
                return 0f;
            float side = (point.x - c0.x) * -tan0.z + (point.z - c0.z) * tan0.x >= 0f ? 1f : -1f;

            // Curve length estimate: (chord + control polygon) / 2. The
            // reach is padded 1.5× — sampling past the cap's corners only
            // sinks a few harmless millimeters deeper, while stopping
            // short leaves the corners proud, which is the whole bug.
            float len = ((he - hs).magnitude + (hc - hs).magnitude + (he - hc).magnitude) * 0.5f;
            float dtReach = reach * 1.5f / Mathf.Max(1f, len);
            float sink = 0f;
            for (int i = -4; i <= 4; i++)
            {
                float t = bestT + dtReach * (i * 0.25f);
                Vector3 q = Evaluate(hs, hc, he, t);
                Vector3 tn = Tangent(hs, hc, he, t).normalized;
                float fx = q.x + -tn.z * half * side;
                float fz = q.z + tn.x * half * side;
                float r = (point.x - fx) * normal.x + (point.z - fz) * normal.z;
                if (r > sink)
                    sink = r;
            }
            return Mathf.Min(sink, MaxSink);
        }

        bool TryFaceAt(float outsideArc, float insideArc, float sideOffset, bool requireAngle, SplineWall host, out Vector3 point, out Vector3 normal)
        {
            point = default;
            normal = default;
            float t = TFromExtendedArc(arcTable, s, control, e, (outsideArc + insideArc) * 0.5f);
            Vector3 tanDir = Tangent(s, control, e, t).normalized;
            Vector3 from = ProbeAt(outsideArc, sideOffset);
            Vector3 dir = ProbeAt(insideArc, sideOffset) - from;
            float dist = dir.magnitude;
            if (dist < 0.001f)
                return false;
            dir /= dist;

            // Overshoot generously: the first hit on THIS host along the
            // ray is its near face regardless, and a tight max distance
            // loses borderline hits when the face plane sits exactly at
            // the inside sample.
            RaycastHit[] hits = Physics.RaycastAll(from - dir * 0.05f, dir, dist + 0.35f);
            System.Array.Sort(hits, (x, y) => x.distance.CompareTo(y.distance));
            foreach (RaycastHit hit in hits)
            {
                if (!IsCrossingHost(hit.collider, tanDir, buildOrder, requireAngle))
                    continue;
                SplineWallSection hitSection = hit.collider.GetComponent<SplineWallSection>();
                if (hitSection.wall != host)
                    continue;
                Vector3 n = new Vector3(hit.normal.x, 0f, hit.normal.z);
                // A grazing top-face hit has no sideways normal — face the
                // way we came so the cut still lands between the samples.
                if (n.sqrMagnitude < 0.01f)
                    n = new Vector3(-dir.x, 0f, -dir.z);
                point = new Vector3(hit.point.x, 0f, hit.point.z);
                normal = n.normalized;
                // Lateral span of the cut face: half thickness, stretched
                // by the crossing's obliquity (a skewed cut is wider than
                // the wall), then sunk to the curve's deepest recession.
                float obliquity = Mathf.Max(0.15f, Mathf.Abs(normal.x * tanDir.x + normal.z * tanDir.z));
                float reach = Mathf.Min(2f, thickness * 0.5f / obliquity);
                point -= normal * FaceRecession(point, normal, reach, host);
                return true;
            }
            return false;
        }

        List<Band> ScanLane(float sideOffset, bool graze)
        {
            // The crossing-angle gate applies only where the wall's own
            // BODY overlaps a host — a collinear host there is a duplicate
            // to refuse, not a junction. Inside the end-extension
            // overshoot the gate is off: end-on burials (collinear
            // continuations, closing an in-line gap) band at any angle
            // and clip flush at the host's cap, instead of dropping whole
            // blocked sections and leaving a stepped gap.
            bool RequireAngleAt(float arcPos) => !graze && arcPos > 0.02f && arcPos < totalArc - 0.02f;

            var laneBands = new List<Band>();
            var open = new Dictionary<SplineWall, Band>();
            HashSet<SplineWall> prevHosts = HostsAt(aMin, sideOffset, RequireAngleAt(aMin));
            foreach (SplineWall host in prevHosts)
                open[host] = new Band { wall = host, graze = graze, aIn = aMin, hasIn = false, gateQualified = RequireAngleAt(aMin) };
            float prevArc = aMin;

            var left = new List<SplineWall>();
            for (float arc = aMin + step; ; arc += step)
            {
                float a = Mathf.Min(arc, aMax);
                HashSet<SplineWall> hosts = HostsAt(a, sideOffset, RequireAngleAt(a));
                // Appearing under the gate qualifies an open band for the
                // hysteresis below.
                if (RequireAngleAt(a))
                    foreach (SplineWall host in hosts)
                        if (open.TryGetValue(host, out Band qb))
                            qb.gateQualified = true;
                // A gate-qualified open band closes at its geometric exit,
                // never at the gate boundary. A band that never passed the
                // gate (opened parallel in the gate-free end zone) gets no
                // such grace — it must close here, or a redraw entering
                // through a cap would read as one endless burial and drop
                // the whole wall instead of refusing red.
                if (open.Count > 0 && RequireAngleAt(a))
                    foreach (SplineWall host in HostsAt(a, sideOffset, false))
                        if (open.TryGetValue(host, out Band hb) && hb.gateQualified)
                            hosts.Add(host);
                foreach (SplineWall host in hosts)
                {
                    if (open.ContainsKey(host))
                        continue;
                    Band band = new Band { wall = host, graze = graze, aIn = prevArc, gateQualified = RequireAngleAt(a) };
                    band.hasIn = TryFaceAt(prevArc, a, sideOffset, false, host, out band.pIn, out band.nIn);
                    open[host] = band;
                }
                left.Clear();
                foreach (KeyValuePair<SplineWall, Band> pair in open)
                    if (!hosts.Contains(pair.Key))
                        left.Add(pair.Key);
                foreach (SplineWall host in left)
                {
                    Band band = open[host];
                    band.aOut = a;
                    band.hasOut = TryFaceAt(a, prevArc, sideOffset, false, host, out band.pOut, out band.nOut);
                    if (band.aOut - band.aIn > 0.02f)
                        laneBands.Add(band);
                    open.Remove(host);
                }
                prevHosts = hosts;
                prevArc = a;
                if (a >= aMax)
                    break;
            }
            // Sweep ends inside a host (a buried overshoot): everything
            // from the entry face onward is dropped — the flush T-joint.
            // Except a parallel leftover with no entry face: that's a
            // redraw lying inside the host, not a burial — discarded, so
            // its pieces fall through to the duplicate refusal (red)
            // instead of silently vanishing.
            foreach (KeyValuePair<SplineWall, Band> pair in open)
            {
                Band band = pair.Value;
                band.aOut = aMax + 1f;
                band.hasOut = false;
                if (!graze && !band.hasIn && !HostsAt(aMax, sideOffset, true).Contains(pair.Key))
                    continue;
                laneBands.Add(band);
            }
            return laneBands;
        }

        List<Band> bands = ScanLane(0f, false);

        // The face a graze leans into, found by casting sideways from the
        // centerline (outside the host) toward the lane (inside it) — this
        // works even when the lane never transitions, e.g. a wall leaning
        // alongside a host for its entire length.
        bool TrySideFace(float arc, float sideOffset, SplineWall host, float reach, out Vector3 point, out Vector3 normal)
        {
            point = default;
            normal = default;
            Vector3 from = ProbeAt(arc, 0f);
            Vector3 dir = ProbeAt(arc, sideOffset) - from;
            float dist = dir.magnitude;
            if (dist < 0.001f)
                return false;
            dir /= dist;
            float t = TFromExtendedArc(arcTable, s, control, e, arc);
            Vector3 tanDir = Tangent(s, control, e, t).normalized;
            RaycastHit[] hits = Physics.RaycastAll(from - dir * 0.02f, dir, dist + 0.3f);
            System.Array.Sort(hits, (x, y) => x.distance.CompareTo(y.distance));
            foreach (RaycastHit hit in hits)
            {
                if (!IsCrossingHost(hit.collider, tanDir, buildOrder, false))
                    continue;
                if (hit.collider.GetComponent<SplineWallSection>().wall != host)
                    continue;
                Vector3 n = new Vector3(hit.normal.x, 0f, hit.normal.z);
                if (n.sqrMagnitude < 0.01f)
                    return false;
                point = new Vector3(hit.point.x, 0f, hit.point.z);
                normal = n.normalized;
                // For a graze the cut spans the band along the run —
                // sink the plane to the curved flank's deepest point so
                // a straight wall leaning on a curve fuses without
                // hairline gaps at the band's ends.
                point -= normal * FaceRecession(point, normal, reach, host);
                return true;
            }
            return false;
        }

        // Side lanes catch grazes: the centerline stays outside the host
        // but one face dips (or leans) in. Grazes whose centerline is
        // itself inside that host are burials — collinear chains, T-join
        // overshoots — owned by the centerline machinery, and grazes
        // already covered by a centerline band are redundant. The face
        // plane is sampled at two points along the band: disagreement
        // means a corner clip, which no single plane represents — skip.
        float halfW = thickness * 0.5f;
        foreach (float side in new[] { halfW, -halfW })
        {
            foreach (Band g in ScanLane(side, true))
            {
                float aEnd = Mathf.Min(g.aOut, aMax);
                float midArc = Mathf.Clamp((g.aIn + aEnd) * 0.5f, aMin, aMax);
                if (HostsAt(midArc, 0f, false).Contains(g.wall))
                    continue;
                bool covered = false;
                foreach (Band b in bands)
                    if (!b.graze && b.wall == g.wall
                        && g.aIn <= b.aOut + 0.3f && g.aOut >= b.aIn - 0.3f)
                        covered = true;
                if (covered)
                    continue;
                float aA = Mathf.Clamp(Mathf.Lerp(g.aIn, aEnd, 0.3f), aMin, aMax);
                float aB = Mathf.Clamp(Mathf.Lerp(g.aIn, aEnd, 0.7f), aMin, aMax);
                float grazeReach = Mathf.Min(2f, (aEnd - g.aIn) * 0.5f + 0.3f);
                if (!TrySideFace(aA, side, g.wall, grazeReach, out Vector3 pA, out Vector3 nA))
                    continue;
                if (!TrySideFace(aB, side, g.wall, 0f, out Vector3 pB, out Vector3 nB))
                    continue;
                if (Vector3.Dot(nA, nB) < 0.7f)
                    continue;
                g.hasIn = true;
                g.pIn = pA;
                g.nIn = nA;
                g.hasOut = false;
                bands.Add(g);
            }
        }
        bands.Sort((x, y) => x.aIn.CompareTo(y.aIn));
        return bands;
    }

    // requireAngle distinguishes the two junction mechanisms: centerline
    // bands demand a real crossing angle where the wall's BODY overlaps a
    // host (near-parallel same-strip overlap is a refusal, not a
    // junction) — but not in the end-extension overshoot, where end-on
    // collinear burials are deliberate fuses (see ScanLane's
    // RequireAngleAt). Graze lanes take any angle everywhere — a
    // same-face graze necessarily passes through a parallel-to-host
    // moment at its deepest point, and grazes only clip, never drop.
    static bool IsCrossingHost(Collider c, Vector3 tanDir, int buildOrder, bool requireAngle)
    {
        if (c.isTrigger)
            return false;
        SplineWallSection section = c.GetComponent<SplineWallSection>();
        if (section == null || section.wall == null)
            return false;
        if (section.wall.buildOrder >= buildOrder)
            return false;
        if (!requireAngle)
            return true;
        float myYaw = Mathf.Atan2(-tanDir.z, tanDir.x) * Mathf.Rad2Deg;
        float delta = Mathf.Abs(Mathf.DeltaAngle(myYaw, c.transform.eulerAngles.y));
        delta = Mathf.Min(delta, 180f - delta);
        // The box yaw is only the section's AVERAGE heading — a tightly
        // curved section turns 20°+ within itself, enough for a wall to
        // read as crossing its own redraw. Near the gate, re-judge
        // against the host's local tangents across the section; the
        // minimum decides, so following the same curve stays parallel.
        if (delta >= JunctionAngle && delta < JunctionAngle + 25f)
        {
            SplineWall host = section.wall;
            for (int i = 0; i <= 2; i++)
            {
                float t = Mathf.Lerp(section.tStart, section.tEnd, i * 0.5f);
                Vector3 ht = Tangent(host.curveStart, host.curveControl, host.curveEnd, t).normalized;
                float hy = Mathf.Atan2(-ht.z, ht.x) * Mathf.Rad2Deg;
                float d = Mathf.Abs(Mathf.DeltaAngle(myYaw, hy));
                d = Mathf.Min(d, 180f - d);
                delta = Mathf.Min(delta, d);
            }
        }
        return delta >= JunctionAngle;
    }

    // How far past an endpoint the sweep continues: at least half a
    // section, so the endpoint's cell is fully covered — and further when
    // an existing wall stands AT (or within a whisker of) that natural
    // cap. The sweep then overshoots INTO that wall on purpose: the
    // junction band trims it back flush to the wall's face, which is what
    // makes a snapped or touching end read as a built joint. Contact
    // only — probes reach 0.4 + 0.2 radius = 0.6 from the endpoint, just
    // past the 0.5 default cap, so anything the cap would touch fuses and
    // a visible gap is never bridged. Connecting across a distance is the
    // end snap's job, not a reach-back's.
    static float EndExtension(Vector3 s, Vector3 control, Vector3 e, float[] arcTable,
        bool atStart, float sectionLength)
    {
        float extension = sectionLength * 0.5f;
        float totalArc = arcTable[arcTable.Length - 1];
        Terrain terrain = Terrain.activeTerrain;

        for (float arc = 0.1f; arc <= sectionLength * 0.4f + 0.001f; arc += 0.1f)
        {
            float trueArc = atStart ? -arc : totalArc + arc;
            float t = TFromExtendedArc(arcTable, s, control, e, trueArc);
            Vector3 p = Evaluate(s, control, e, t);
            float ground = terrain != null
                ? terrain.transform.position.y + terrain.SampleHeight(p)
                : 0f;
            Vector3 probe = new Vector3(p.x, ground + 0.75f, p.z);
            foreach (Collider c in Physics.OverlapSphere(probe, 0.2f))
            {
                if (c.isTrigger)
                    continue;
                if (c.GetComponent<SplineWallSection>() != null)
                    return Mathf.Max(extension, arc + 0.6f);
            }
        }
        return extension;
    }

    // Sections for a new course laid directly on this wall: same curve,
    // same cutting, each base sitting on the section top beneath it —
    // courses belong to the wall, so they align by construction, gaps in
    // the wall below stay gaps above, and junction cuts carry upward
    // (cut planes are vertical, so they hold at any height).
    public List<SectionSpec> BuildCourseSpecs(float courseHeight, float courseBaseWallHeight, bool includeBlocked = false)
    {
        var specs = new List<SectionSpec>();
        float[] arcTable = BuildArcTable(curveStart, curveControl, curveEnd);
        foreach (SplineWallSection below in GetComponentsInChildren<SplineWallSection>())
        {
            var spec = new SectionSpec
            {
                index = below.index,
                tStart = below.tStart,
                tEnd = below.tEnd,
                tSweepStart = below.tSweepStart,
                tSweepEnd = below.tSweepEnd,
                cutPoints = below.cutPoints != null ? new List<Vector3>(below.cutPoints) : null,
                cutNormals = below.cutNormals != null ? new List<Vector3>(below.cutNormals) : null,
                bottomY = below.topY,
            };
            AddSpecIfClear(specs, curveStart, curveControl, curveEnd, spec,
                courseHeight, float.NegativeInfinity, thickness, courseBaseWallHeight, arcTable, includeBlocked);
        }
        return specs;
    }

    // Finishes a prepared spec (index, ranges, cuts, bottomY set) and adds
    // it. A spec that would duplicate an existing wall is skipped — or,
    // when includeBlocked is set (previews), added with blocked = true so
    // the ghost can show the refusal in red instead of vanishing.
    static void AddSpecIfClear(List<SectionSpec> specs, Vector3 s, Vector3 control, Vector3 e,
        SectionSpec spec, float height, float fixedTopY, float thickness, float baseWallHeight, float[] arcTable,
        bool includeBlocked, float clearArc0 = float.NaN, float clearArc1 = float.NaN)
    {
        float tMid = (spec.tSweepStart + spec.tSweepEnd) * 0.5f;
        Vector3 mid = Evaluate(s, control, e, tMid);
        Vector3 direction = Tangent(s, control, e, tMid).normalized;
        float yaw = Mathf.Atan2(-direction.z, direction.x) * Mathf.Rad2Deg;
        // A locked top is one absolute height for the whole wall; where
        // the ground climbs to within a sliver of it the wall is fully
        // buried and the piece simply doesn't exist.
        spec.topY = float.IsNegativeInfinity(fixedTopY) ? spec.bottomY + height : fixedTopY;
        float span = spec.topY - spec.bottomY;
        if (span < 0.1f)
            return;
        Vector3 center = new Vector3(mid.x, (spec.bottomY + spec.topY) * 0.5f, mid.z);

        spec.arcLength = ExtendedArcAt(arcTable, s, control, e, spec.tSweepEnd)
            - ExtendedArcAt(arcTable, s, control, e, spec.tSweepStart);
        if (spec.arcLength < 0.03f)
            return;

        // Occupancy box: the sweep, unless a clearArc range says a
        // junction cut owns part of it — then only the range clear of the
        // burial is tested. A piece that is nearly all burial is a
        // junction sliver, not a redraw, and is never blocked.
        float blockLen = spec.arcLength;
        Vector3 blockCenter = center;
        float blockYaw = yaw;
        bool checkable = true;
        if (!float.IsNaN(clearArc0))
        {
            blockLen = clearArc1 - clearArc0;
            checkable = blockLen >= 0.1f;
            if (checkable)
            {
                float bt = TFromExtendedArc(arcTable, s, control, e, (clearArc0 + clearArc1) * 0.5f);
                Vector3 bp = Evaluate(s, control, e, bt);
                Vector3 bd = Tangent(s, control, e, bt).normalized;
                blockCenter = new Vector3(bp.x, center.y, bp.z);
                blockYaw = Mathf.Atan2(-bd.z, bd.x) * Mathf.Rad2Deg;
            }
        }
        spec.blocked = checkable && IsBlocked(blockCenter, blockLen, span, thickness, blockYaw);
        if (spec.blocked && !includeBlocked)
            return;

        spec.position = center;
        spec.yaw = yaw;
        spec.mesh = BuildSectionMesh(s, control, e, spec, arcTable, thickness, baseWallHeight, center, yaw);
        specs.Add(spec);
    }

    // A section that would duplicate an existing wall is dropped: the
    // physics test is the build's occupancy answer. Crossings at a real
    // angle are junctions (handled by band trimming, not blocking), so
    // only near-parallel overlap (a second wall drawn through the same
    // space) blocks. The test box is shrunk so touching neighbours and
    // snapped endpoints don't count either.
    static bool IsBlocked(Vector3 center, float length, float height, float thickness, float yaw)
    {
        // The thickness extent is deliberately slim: only a wall whose
        // CENTERLINE reaches another wall's body is a duplicate. A wall
        // merely leaning its face into a neighbour is legal — the graze
        // planes clip it flush against that neighbour instead.
        Vector3 halfExtents = new Vector3(length * 0.35f, height * 0.4f, thickness * 0.15f);
        foreach (Collider c in Physics.OverlapBox(center, halfExtents, Quaternion.Euler(0f, yaw, 0f)))
        {
            if (c.isTrigger || c.GetComponent<SplineWallSection>() == null)
                continue;

            float otherYaw = c.transform.eulerAngles.y;
            float delta = Mathf.Abs(Mathf.DeltaAngle(yaw, otherYaw));
            delta = Mathf.Min(delta, 180f - delta);
            if (delta < JunctionAngle)
                return true;
        }
        return false;
    }

    // Lowest ground under the slice footprint (both faces sampled along
    // it), snapped down to the base step so neighbouring sections share
    // bases on gentle slopes and real drops read as uniform steps.
    static float SampleSliceGround(Vector3 s, Vector3 control, Vector3 e,
        float t0, float t1, float thickness, float baseStep)
    {
        Terrain terrain = Terrain.activeTerrain;
        if (terrain == null)
            return 0f;

        float min = float.MaxValue;
        for (int i = 0; i <= 4; i++)
        {
            float t = Mathf.Lerp(t0, t1, i / 4f);
            Vector3 p = Evaluate(s, control, e, t);
            Vector3 side = Tangent(s, control, e, t).normalized;
            side = new Vector3(-side.z, 0f, side.x) * (thickness * 0.5f);
            min = Mathf.Min(min, Mathf.Min(terrain.SampleHeight(p + side), terrain.SampleHeight(p - side)));
        }

        float ground = terrain.transform.position.y + min;
        if (baseStep > 0f)
            ground = Mathf.Floor(ground / baseStep) * baseStep;
        return ground;
    }

    // A wall end for the placement tool's end snapping: the endpoint
    // itself, and the direction pointing out past it, flattened. The end
    // piece's physical faces sit around these — the end cap half a
    // section out along the outward direction, the flanks half a
    // thickness to each side.
    public Vector3 EndPosition(bool atStart) => atStart ? curveStart : curveEnd;

    public Vector3 EndOutwardDir(bool atStart)
    {
        Vector3 d = Tangent(curveStart, curveControl, curveEnd, atStart ? 0f : 1f);
        if (atStart)
            d = -d;
        d.y = 0f;
        return d.normalized;
    }

    // Which side of this wall's centerline a point lies on (+1 toward the
    // curve's left-hand normal) — the offset tool reads the cursor's side
    // from this.
    public float SideOf(Vector3 point)
    {
        float bestSq = float.MaxValue;
        float bestT = 0f;
        for (int i = 0; i <= 64; i++)
        {
            float t = i / 64f;
            Vector3 p = Evaluate(curveStart, curveControl, curveEnd, t);
            float dx = p.x - point.x;
            float dz = p.z - point.z;
            float sq = dx * dx + dz * dz;
            if (sq < bestSq) { bestSq = sq; bestT = t; }
        }
        Vector3 near = Evaluate(curveStart, curveControl, curveEnd, bestT);
        Vector3 dir = Tangent(curveStart, curveControl, curveEnd, bestT).normalized;
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
        float bestSq = float.MaxValue;
        Vector3 best = curveStart;
        for (int i = 0; i <= 64; i++)
        {
            Vector3 p = Evaluate(curveStart, curveControl, curveEnd, i / 64f);
            float dx = p.x - point.x;
            float dz = p.z - point.z;
            float sq = dx * dx + dz * dz;
            if (sq < bestSq) { bestSq = sq; best = p; }
        }
        float centerDist = Mathf.Sqrt(bestSq);
        faceDistance = Mathf.Max(0f, centerDist - thickness * 0.5f);
        if (centerDist > 0.001f)
        {
            Vector3 toPoint = new Vector3(point.x - best.x, 0f, point.z - best.z) / centerDist;
            best += toPoint * (thickness * 0.5f);
        }
        return new Vector3(best.x, 0f, best.z);
    }

    public static Vector3 Evaluate(Vector3 s, Vector3 control, Vector3 e, float t)
    {
        float u = 1f - t;
        return u * u * s + 2f * u * t * control + t * t * e;
    }

    static Vector3 Tangent(Vector3 s, Vector3 control, Vector3 e, float t)
    {
        Vector3 d = 2f * (1f - t) * (control - s) + 2f * t * (e - control);
        return d.sqrMagnitude < 0.0001f ? e - s : d;
    }

    // Cumulative arc length lookup for the whole curve. Sections read
    // their texture U and their cut points from this one shared table, so
    // seams match exactly and cuts are equal by measurement.
    static float[] BuildArcTable(Vector3 s, Vector3 control, Vector3 e)
    {
        const int Samples = 65;
        float[] table = new float[Samples];
        Vector3 prev = s;
        for (int i = 1; i < Samples; i++)
        {
            Vector3 p = Evaluate(s, control, e, (float)i / (Samples - 1));
            table[i] = table[i - 1] + Vector3.Distance(prev, p);
            prev = p;
        }
        return table;
    }

    static float ArcAt(float[] table, float t)
    {
        float f = Mathf.Clamp01(t) * (table.Length - 1);
        int i = Mathf.Min(table.Length - 2, Mathf.FloorToInt(f));
        return Mathf.Lerp(table[i], table[i + 1], f - i);
    }

    static float TAtArc(float[] table, float arc)
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

    // Arc-length mapping extended past both curve ends: outside [0, 1] the
    // quadratic extrapolates along its tangent, so a linear approximation
    // with the endpoint parametric speed holds for the short overshoots
    // the end extensions use.
    static float TFromExtendedArc(float[] table, Vector3 s, Vector3 control, Vector3 e, float arc)
    {
        float total = table[table.Length - 1];
        if (arc < 0f)
            return arc / Mathf.Max(0.05f, (2f * (control - s)).magnitude);
        if (arc > total)
            return 1f + (arc - total) / Mathf.Max(0.05f, (2f * (e - control)).magnitude);
        return TAtArc(table, arc);
    }

    static float ExtendedArcAt(float[] table, Vector3 s, Vector3 control, Vector3 e, float t)
    {
        float total = table[table.Length - 1];
        if (t < 0f)
            return t * Mathf.Max(0.05f, (2f * (control - s)).magnitude);
        if (t > 1f)
            return total + (t - 1f) * Mathf.Max(0.05f, (2f * (e - control)).magnitude);
        return ArcAt(table, t);
    }

    // Sweeps the wall's rectangular cross-section along one section's
    // slice of the curve. Neighbouring sections evaluate the same curve at
    // the same boundary parameter, so their end faces are identical and
    // the finished wall reads as one continuous mesh. Junction cuts clip
    // each row's cross-section against the stored planes: clipped points
    // land exactly on the plane, so the cut edge follows the older wall's
    // face — a flush cap at right angles, a clean diagonal at shallow
    // ones. Vertices are baked into the section's local frame (position +
    // yaw, no scale).
    static Mesh BuildSectionMesh(Vector3 s, Vector3 control, Vector3 e,
        SectionSpec spec, float[] arcTable,
        float thickness, float baseWallHeight, Vector3 center, float yaw)
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
            float t = Mathf.Lerp(spec.tSweepStart, spec.tSweepEnd, (float)j / MeshSegments);
            Vector3 p = Evaluate(s, control, e, t);
            Vector3 side = Tangent(s, control, e, t).normalized;
            side = new Vector3(-side.z, 0f, side.x) * halfWidth;
            Vector3 outer = new Vector3(p.x + side.x, 0f, p.z + side.z);
            Vector3 inner = new Vector3(p.x - side.x, 0f, p.z - side.z);

            if (spec.cutPoints != null)
                for (int k = 0; k < spec.cutPoints.Count; k++)
                    ClipRow(ref outer, ref inner, spec.cutPoints[k], spec.cutNormals[k]);

            outerBottom[j] = worldToLocal.MultiplyPoint3x4(new Vector3(outer.x, bottomY, outer.z));
            outerTop[j] = worldToLocal.MultiplyPoint3x4(new Vector3(outer.x, topY, outer.z));
            innerBottom[j] = worldToLocal.MultiplyPoint3x4(new Vector3(inner.x, bottomY, inner.z));
            innerTop[j] = worldToLocal.MultiplyPoint3x4(new Vector3(inner.x, topY, inner.z));
            arcU[j] = ExtendedArcAt(arcTable, s, control, e, t);
        }

        var vertices = new List<Vector3>();
        var uvs = new List<Vector2>();
        var triangles = new List<int>();

        // Texture V is anchored to world height so stone courses line up
        // across sections and courses; taller walls show more courses
        // rather than stretched ones.
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

    // Clips one row's cross-section segment against a vertical plane,
    // keeping the positive (outside-the-host) side. A point past the
    // plane moves to the segment's plane crossing; a row entirely past it
    // collapses onto the plane (degenerate, invisible).
    static void ClipRow(ref Vector3 outer, ref Vector3 inner, Vector3 planePoint, Vector3 planeNormal)
    {
        float dOut = (outer.x - planePoint.x) * planeNormal.x + (outer.z - planePoint.z) * planeNormal.z;
        float dIn = (inner.x - planePoint.x) * planeNormal.x + (inner.z - planePoint.z) * planeNormal.z;
        if (dOut >= 0f && dIn >= 0f)
            return;
        if (dOut < 0f && dIn < 0f)
        {
            outer -= planeNormal * dOut;
            inner -= planeNormal * dIn;
            return;
        }
        if (dOut < 0f)
            outer = Vector3.Lerp(outer, inner, dOut / (dOut - dIn));
        else
            inner = Vector3.Lerp(inner, outer, dIn / (dIn - dOut));
    }
}
