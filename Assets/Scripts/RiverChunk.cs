using UnityEngine;

// Bake-time facts for one water ribbon chunk — a waterCellSize cell of the
// Marches water network (Docs/Rivers.md). MarchesDressing stamps the
// water-level range at bake time so RiverWaterLOD's steep-reach guard reads
// a STORED number instead of re-deriving one (the one-test doctrine's
// shape). Scene dressing, not a save fact — nothing here joins CastleSave.
public class RiverChunk : MonoBehaviour
{
    public float minWaterY;
    public float maxWaterY;
    // Widest waterway contributing to this chunk — scales the HDRP
    // surface's wave energy so a brook barely stirs while the Monnow
    // keeps visible motion.
    public float maxWidth;
}
