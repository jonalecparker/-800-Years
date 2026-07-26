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
    public float bottomY;
    public float topY;
    [HideInInspector] public Mesh ownedMesh;

    void OnDestroy()
    {
        if (ownedMesh != null)
            Destroy(ownedMesh);
    }
}
