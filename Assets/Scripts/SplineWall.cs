using System.Collections.Generic;
using UnityEngine;

// A curved wall as a first-class object: one quadratic Bézier cut into
// uniform arc-length sections, each a swept-mesh child with its own
// collider. Walls live off the grid — endpoints go anywhere, and snapping
// is the placement tool's job — so section boundaries are pure masonry
// divisions (all equal within a wall) instead of grid-cell accidents.
// Occupancy, which grid cells used to provide, is answered with physics
// overlap tests against section colliders.
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
    void OnDisable() { All.Remove(this); }

    public static SplineWall Create(Transform parent, Vector3 s, Vector3 control, Vector3 e,
        float height, float thickness, float baseWallHeight, Material material, List<SectionSpec> specs)
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

        foreach (SectionSpec spec in specs)
            wall.CreateSection(spec, material);
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
        section.bottomY = spec.bottomY;
        section.topY = spec.topY;
        section.ownedMesh = spec.mesh;
    }

    // Sections for a fresh wall on the ground. The cut count is chosen so
    // every section comes out the same arc length, as close to the target
    // as the total divides into — no skinny slivers, ever. The sweep runs
    // PAST both endpoints: half a cell so the picked cells are fully
    // covered (endpoints are cell centers), and deeper when an existing
    // wall stands just beyond an end — the sweep extends backwards into
    // it, closing the joint regardless of approach angle.
    public static List<SectionSpec> BuildSpecs(Vector3 s, Vector3 control, Vector3 e,
        float height, float targetSectionLength, float thickness, float baseStep, float baseWallHeight)
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

        for (int i = 0; i < count; i++)
        {
            float tStart = TFromExtendedArc(arcTable, s, control, e, i * sectionArc - startExt);
            float tEnd = TFromExtendedArc(arcTable, s, control, e, (i + 1) * sectionArc - startExt);
            float ground = SampleSliceGround(s, control, e, tStart, tEnd, thickness, baseStep);
            AddSpecIfClear(specs, s, control, e, i, tStart, tEnd, sectionArc,
                ground, height, thickness, baseWallHeight, arcTable);
        }
        return specs;
    }

    // How far past an endpoint the sweep continues: at least half a
    // section, so the endpoint's cell is fully covered — and further when
    // an existing wall stands within reach along the curve's own line,
    // in which case the sweep buries itself into that wall.
    static float EndExtension(Vector3 s, Vector3 control, Vector3 e, float[] arcTable,
        bool atStart, float sectionLength)
    {
        float extension = sectionLength * 0.5f;
        float totalArc = arcTable[arcTable.Length - 1];
        Terrain terrain = Terrain.activeTerrain;

        for (float arc = 0.1f; arc <= sectionLength * 1.3f; arc += 0.1f)
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
                if (c.GetComponent<SplineWallSection>() != null || c.GetComponentInParent<PlacedPiece>() != null)
                    return Mathf.Max(extension, arc + 0.3f);
            }
        }
        return extension;
    }

    // Sections for a new course laid directly on this wall: same curve,
    // same cutting, each base sitting on the section top beneath it —
    // courses belong to the wall, so they align by construction, and gaps
    // in the wall below stay gaps above.
    public List<SectionSpec> BuildCourseSpecs(float courseHeight, float courseBaseWallHeight)
    {
        var specs = new List<SectionSpec>();
        float[] arcTable = BuildArcTable(curveStart, curveControl, curveEnd);
        foreach (SplineWallSection below in GetComponentsInChildren<SplineWallSection>())
        {
            float arcLength = ExtendedArcAt(arcTable, curveStart, curveControl, curveEnd, below.tEnd)
                - ExtendedArcAt(arcTable, curveStart, curveControl, curveEnd, below.tStart);
            AddSpecIfClear(specs, curveStart, curveControl, curveEnd, below.index,
                below.tStart, below.tEnd, arcLength, below.topY, courseHeight,
                thickness, courseBaseWallHeight, arcTable);
        }
        return specs;
    }

    static void AddSpecIfClear(List<SectionSpec> specs, Vector3 s, Vector3 control, Vector3 e,
        int index, float tStart, float tEnd, float arcLength, float bottom,
        float height, float thickness, float baseWallHeight, float[] arcTable)
    {
        float tMid = (tStart + tEnd) * 0.5f;
        Vector3 mid = Evaluate(s, control, e, tMid);
        Vector3 direction = Tangent(s, control, e, tMid).normalized;
        float yaw = Mathf.Atan2(-direction.z, direction.x) * Mathf.Rad2Deg;
        float top = bottom + height;
        Vector3 center = new Vector3(mid.x, (bottom + top) * 0.5f, mid.z);

        if (IsBlocked(center, arcLength, height, thickness, yaw))
            return;

        Mesh mesh = BuildSectionMesh(s, control, e, tStart, tEnd, bottom, top,
            arcTable, thickness, baseWallHeight, center, yaw);

        specs.Add(new SectionSpec
        {
            index = index,
            tStart = tStart,
            tEnd = tEnd,
            arcLength = arcLength,
            position = center,
            yaw = yaw,
            bottomY = bottom,
            topY = top,
            mesh = mesh,
        });
    }

    // A section that would duplicate an existing wall — spline or grid
    // piece — is dropped: the physics test stands in for the occupancy the
    // grid used to provide. Walls crossing at a real angle are a junction,
    // not a conflict: the masonry interpenetrates and reads as one build,
    // so only near-parallel overlap (a second wall drawn through the same
    // space) blocks. The test box is shrunk so touching neighbours and
    // snapped endpoints don't count either.
    static bool IsBlocked(Vector3 center, float length, float height, float thickness, float yaw)
    {
        Vector3 halfExtents = new Vector3(length * 0.35f, height * 0.4f, thickness * 0.3f);
        foreach (Collider c in Physics.OverlapBox(center, halfExtents, Quaternion.Euler(0f, yaw, 0f)))
        {
            if (c.isTrigger)
                continue;

            // The other wall's run direction: spline sections carry it in
            // their transform, but grid pieces have square footprints, so
            // they record the run direction at placement time instead.
            float otherYaw;
            PlacedPiece gridPiece = c.GetComponentInParent<PlacedPiece>();
            if (c.GetComponent<SplineWallSection>() != null)
                otherYaw = c.transform.eulerAngles.y;
            else if (gridPiece != null)
                otherYaw = gridPiece.runYaw;
            else
                continue;

            // Crossing walls (no facing direction) form a junction; only
            // near-parallel overlap blocks as a duplicate.
            float delta = Mathf.Abs(Mathf.DeltaAngle(yaw, otherYaw));
            delta = Mathf.Min(delta, 180f - delta);
            if (delta < 25f)
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
    // the finished wall reads as one continuous mesh. Vertices are baked
    // into the section's local frame (position + yaw, no scale).
    static Mesh BuildSectionMesh(Vector3 s, Vector3 control, Vector3 e,
        float t0, float t1, float bottomY, float topY, float[] arcTable,
        float thickness, float baseWallHeight, Vector3 center, float yaw)
    {
        Matrix4x4 worldToLocal = Matrix4x4.TRS(center, Quaternion.Euler(0f, yaw, 0f), Vector3.one).inverse;
        float halfWidth = thickness * 0.5f;

        var outerBottom = new Vector3[MeshSegments + 1];
        var outerTop = new Vector3[MeshSegments + 1];
        var innerBottom = new Vector3[MeshSegments + 1];
        var innerTop = new Vector3[MeshSegments + 1];
        var arcU = new float[MeshSegments + 1];

        for (int j = 0; j <= MeshSegments; j++)
        {
            float t = Mathf.Lerp(t0, t1, (float)j / MeshSegments);
            Vector3 p = Evaluate(s, control, e, t);
            Vector3 side = Tangent(s, control, e, t).normalized;
            side = new Vector3(-side.z, 0f, side.x) * halfWidth;

            outerBottom[j] = worldToLocal.MultiplyPoint3x4(new Vector3(p.x + side.x, bottomY, p.z + side.z));
            outerTop[j] = worldToLocal.MultiplyPoint3x4(new Vector3(p.x + side.x, topY, p.z + side.z));
            innerBottom[j] = worldToLocal.MultiplyPoint3x4(new Vector3(p.x - side.x, bottomY, p.z - side.z));
            innerTop[j] = worldToLocal.MultiplyPoint3x4(new Vector3(p.x - side.x, topY, p.z - side.z));
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
        void AddStrip(Vector3[] rowA, Vector3[] rowB, bool flip, float vA, float vB)
        {
            int baseIndex = vertices.Count;
            for (int j = 0; j <= MeshSegments; j++)
            {
                vertices.Add(rowA[j]);
                uvs.Add(new Vector2(arcU[j], vA));
                vertices.Add(rowB[j]);
                uvs.Add(new Vector2(arcU[j], vB));
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

        AddStrip(outerBottom, outerTop, false, vBottom, vTop);
        AddStrip(innerBottom, innerTop, true, vBottom, vTop);
        AddStrip(outerTop, innerTop, false, 0f, 1f);
        AddStrip(outerBottom, innerBottom, true, 0f, 1f);
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
