using UnityEngine;

// The two facts a sliced renderer has to remember: the Y its transform
// was authored at and the Y-SCALE it was authored with, before
// CutawayView ever touched either.
//
// Everything else about a slice — where the masonry's bottom and top are —
// is derived from the live mesh bounds each frame, so an object that
// remeshes under an active cut re-slices correctly. But the transform
// can't tell you where it started once the slice has moved it, and the
// scale matters because not every prism here is a world-space mesh on a
// unit transform: a SlabTile's height IS its Y-scale. Restoring such an
// object to scale 1 (the old single-float assumption) crushed every
// tile taller than a metre one frame after it was placed.
//
// Added by CutawayView the first time it sees a renderer, which is always
// before it modifies one — so the values captured are the authored ones.
// It dies with its GameObject, which is exactly the lifetime it wants.
[AddComponentMenu("")]
[DisallowMultipleComponent]
public class CutawayPivot : MonoBehaviour
{
    public float authoredY;
    public float authoredScaleY = 1f;
}
