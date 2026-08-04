using UnityEngine;

// The one fact a sliced renderer has to remember: the Y its transform was
// authored at, before CutawayView ever moved it.
//
// Everything else about a slice — where the masonry's bottom and top are —
// is derived from the live mesh bounds each frame, so an object that
// remeshes under an active cut re-slices correctly. But the mesh can't
// tell you where the transform started once the transform has moved, and
// building inside a cut room remeshes posts constantly. Hence one float.
//
// Added by CutawayView the first time it sees a renderer, which is always
// before it modifies one — so the value captured is the authored one. It
// dies with its GameObject, which is exactly the lifetime it wants.
[AddComponentMenu("")]
[DisallowMultipleComponent]
public class CutawayPivot : MonoBehaviour
{
    public float authoredY;
}
