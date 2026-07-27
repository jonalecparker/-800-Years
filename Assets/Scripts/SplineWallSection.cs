using UnityEngine;

// One uniform section of a SplineWall: the wall's delete/targeting
// granularity. Carries its slice of the curve (tStart..tEnd), its vertical
// span, and its runtime mesh, which dies with it.
public class SplineWallSection : MonoBehaviour
{
    public SplineWall wall;
    public int index;
    public float tStart;
    public float tEnd;
    // Actual sweep range plus junction cut planes — differ from
    // tStart/tEnd where this section was cut against older walls' faces.
    // Courses stacked above copy these, so cuts carry upward. Cut lists
    // are null when the section has no cuts.
    public float tSweepStart;
    public float tSweepEnd;
    public System.Collections.Generic.List<Vector3> cutPoints;
    public System.Collections.Generic.List<Vector3> cutNormals;
    public float bottomY;
    public float topY;
    [HideInInspector] public Mesh ownedMesh;

    void OnDestroy()
    {
        if (ownedMesh != null)
            Destroy(ownedMesh);
    }
}
