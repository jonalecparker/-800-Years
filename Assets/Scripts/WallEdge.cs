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
// A stacked course is also a WallEdge: it shares its base edge's nodes and
// curve, flags stackBase, and rides on the base's section tops instead of
// the terrain. Courses never take part in the planar graph (no splits, no
// crossings) — they cover a tMin..tMax span of their base and split
// off-graph when deleted.
public class WallEdge : MonoBehaviour
{
    // Active edges, in no particular order. Courses are included and
    // flagged by IsCourse.
    public static readonly List<WallEdge> All = new List<WallEdge>();

    public WallNode nodeA;
    public WallNode nodeB;
    // Quadratic Bézier control point — the AB midpoint for a straight
    // wall. Splits stay exact via polar-form subdivision (WallGraph).
    public Vector3 control;
    public float height;
    public float thickness;
    // Texture-V anchor: one tile of stone per this much world height, so
    // every wall and course shares aligned masonry lines.
    public float baseWallHeight = 3.5f;
    public float targetSectionLength = 1f;
    public float baseStep = 0.5f;
    // Locked-top walls: every section's top sits at this absolute height
    // and only the bottom follows the terrain down. -Infinity (default)
    // means tops ride the ground instead (bottom + height per section).
    public float fixedTopY = float.NegativeInfinity;
    // Elevated-ground walls: sections never drop below this — the base a
    // wall stands on when built ON a room slab (roof deck or floor)
    // instead of the terrain. -Infinity (default) rides the terrain.
    public float baseY = float.NegativeInfinity;
    // Stacked course: this edge rides on stackBase's section tops.
    public WallEdge stackBase;
    // Coverage along the curve, in curve parameter — courses can be
    // partial after deletes. Ground edges always span 0..1.
    public float tMin = 0f;
    public float tMax = 1f;

    public bool IsCourse => stackBase != null;
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
    }

    void OnEnable() { All.Add(this); }

    void OnDisable()
    {
        All.Remove(this);
        // Defensive cleanup for teardown paths that skip WallGraph: nodes
        // silently drop this member (a node left empty dissolves), and
        // any room still ringing through this edge breaches. Graph ops
        // that split (full coverage) re-point rooms BEFORE destroying, so
        // this only fires for true deaths.
        WallRoom.NotifyBreach(this);
        if (nodeA != null)
            nodeA.OnMemberGone(this);
        if (nodeB != null)
            nodeB.OnMemberGone(this);
    }

    public static WallEdge Create(Transform parent, WallNode a, WallNode b, Vector3 control,
        float height, float thickness, float baseWallHeight, Material material,
        float targetSectionLength, float baseStep, float fixedTopY = float.NegativeInfinity,
        float baseY = float.NegativeInfinity)
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
        edge.material = material;
        a.Attach(edge, true);
        b.Attach(edge, false);
        edge.Rebuild();
        return edge;
    }

    // A course shares its base's nodes and curve; only its vertical span
    // and coverage differ. Courses are not node members — the node climbs
    // stacks itself so its post rises with them.
    public static WallEdge CreateCourse(Transform parent, WallEdge below, float courseHeight,
        float courseBaseWallHeight, Material material, float tMin, float tMax)
    {
        GameObject obj = new GameObject("WallCourse");
        obj.transform.SetParent(parent, false);
        WallEdge edge = obj.AddComponent<WallEdge>();
        edge.nodeA = below.nodeA;
        edge.nodeB = below.nodeB;
        edge.control = below.control;
        edge.height = courseHeight;
        edge.thickness = below.thickness;
        edge.baseWallHeight = courseBaseWallHeight;
        edge.targetSectionLength = below.targetSectionLength;
        edge.baseStep = below.baseStep;
        edge.stackBase = below;
        edge.tMin = tMin;
        edge.tMax = tMax;
        edge.material = material;
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

        List<SectionSpec> specs = stackBase != null
            ? stackBase.BuildCourseSpecs(height, baseWallHeight, tMin, tMax)
            : BuildSpecs(A, control, B, height, targetSectionLength, thickness, baseStep,
                baseWallHeight, fixedTopY, baseY);
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
        // cursor raycasts (targeting, course hover, delete), never for
        // occupancy inference.
        BoxCollider box = sectionObj.AddComponent<BoxCollider>();
        box.size = new Vector3(spec.arcLength, spec.topY - spec.bottomY, thickness);

        WallEdgeSection section = sectionObj.AddComponent<WallEdgeSection>();
        section.edge = this;
        section.index = spec.index;
        section.tStart = spec.tStart;
        section.tEnd = spec.tEnd;
        section.bottomY = spec.bottomY;
        section.topY = spec.topY;
        section.ownedMesh = spec.mesh;
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
        float baseY = float.NegativeInfinity)
    {
        var specs = new List<SectionSpec>();
        float[] arcTable = BuildArcTable(s, c, e);
        float totalArc = arcTable[arcTable.Length - 1];
        if (totalArc < 0.02f)
            return specs;

        int count = Mathf.Max(1, Mathf.RoundToInt(totalArc / Mathf.Max(0.1f, targetSectionLength)));
        for (int i = 0; i < count; i++)
        {
            var spec = new SectionSpec
            {
                index = i,
                tStart = TAtArc(arcTable, totalArc * i / count),
                tEnd = TAtArc(arcTable, totalArc * (i + 1) / count),
            };
            spec.bottomY = Mathf.Max(
                SampleSliceGround(s, c, e, spec.tStart, spec.tEnd, thickness, baseStep), baseY);
            FinishSpec(specs, s, c, e, spec, height, fixedTopY, thickness, baseWallHeight, arcTable);
        }
        return specs;
    }

    // Sections for a course laid on this edge, covering rangeMin..rangeMax
    // of the curve: same slices as the sections below, each base sitting
    // on the section top beneath it — courses align by construction and
    // gaps below stay gaps above.
    public List<SectionSpec> BuildCourseSpecs(float courseHeight, float courseBaseWallHeight,
        float rangeMin = 0f, float rangeMax = 1f)
    {
        var specs = new List<SectionSpec>();
        float[] arcTable = BuildArcTable(A, control, B);
        foreach (WallEdgeSection below in SectionsInOrder())
        {
            float mid = (below.tStart + below.tEnd) * 0.5f;
            if (mid < rangeMin - 0.001f || mid > rangeMax + 0.001f)
                continue;
            var spec = new SectionSpec
            {
                index = below.index,
                tStart = below.tStart,
                tEnd = below.tEnd,
                bottomY = below.topY,
            };
            FinishSpec(specs, A, control, B, spec, courseHeight, float.NegativeInfinity,
                thickness, courseBaseWallHeight, arcTable);
        }
        return specs;
    }

    // The spans of this edge's coverage not yet under a stacked course —
    // where a new course can lie. Fully covered returns an empty list.
    public List<(float lo, float hi)> UncoveredRanges()
    {
        var ranges = new List<(float lo, float hi)> { (tMin, tMax) };
        foreach (WallEdge other in All)
        {
            if (other.stackBase != this)
                continue;
            var next = new List<(float lo, float hi)>();
            foreach ((float lo, float hi) in ranges)
            {
                if (other.tMax <= lo + 0.001f || other.tMin >= hi - 0.001f)
                {
                    next.Add((lo, hi));
                    continue;
                }
                if (other.tMin > lo + 0.01f)
                    next.Add((lo, other.tMin));
                if (other.tMax < hi - 0.01f)
                    next.Add((other.tMax, hi));
            }
            ranges = next;
        }
        return ranges;
    }

    // Finishes a prepared spec (index, t-range, bottomY set): vertical
    // span, placement frame, mesh. A locked-top section fully buried by
    // climbing ground simply doesn't exist.
    static void FinishSpec(List<SectionSpec> specs, Vector3 s, Vector3 c, Vector3 e,
        SectionSpec spec, float height, float fixedTopY, float thickness, float baseWallHeight,
        float[] arcTable)
    {
        spec.topY = float.IsNegativeInfinity(fixedTopY) ? spec.bottomY + height : fixedTopY;
        if (spec.topY - spec.bottomY < 0.1f)
            return;
        spec.arcLength = ArcAt(arcTable, spec.tEnd) - ArcAt(arcTable, spec.tStart);
        if (spec.arcLength < 0.03f)
            return;

        float tMid = (spec.tStart + spec.tEnd) * 0.5f;
        Vector3 mid = Evaluate(s, c, e, tMid);
        Vector3 dir = Tangent(s, c, e, tMid).normalized;
        spec.yaw = Mathf.Atan2(-dir.z, dir.x) * Mathf.Rad2Deg;
        spec.position = new Vector3(mid.x, (spec.bottomY + spec.topY) * 0.5f, mid.z);
        spec.mesh = BuildSectionMesh(s, c, e, spec, arcTable, thickness, baseWallHeight,
            spec.position, spec.yaw);
        specs.Add(spec);
    }

    // Lowest ground under the slice footprint (both faces sampled along
    // it), snapped down to the base step so neighbouring sections share
    // bases on gentle slopes and real drops read as uniform steps.
    static float SampleSliceGround(Vector3 s, Vector3 c, Vector3 e,
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
}
