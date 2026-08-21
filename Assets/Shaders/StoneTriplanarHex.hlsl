#ifndef STONE_TRIPLANAR_HEX_INCLUDED
#define STONE_TRIPLANAR_HEX_INCLUDED

// Stone surface: world-space triplanar projection, each plane sampled with
// hex-grid stochastic tiling (per-hex random UV offset) so the texture never
// visibly repeats, plus parallax occlusion mapping marched on the dominant
// projection plane so the relief survives the move off UVs.
//
// The hex blend is HEIGHT-AWARE: raw barycentric cross-fading of a structured
// brick pattern interleaves brick edges from different copies (squiggly
// zigzag lines) and averages brick against mortar until the joints wash out.
// Biasing the weights by the height map and sharpening hard makes each region
// show ONE clean copy, with the transition snapped into the mortar recesses
// where a wandering boundary reads as natural masonry.
//
// Reads the blackboard properties directly by name (they are declared by
// Shader Graph codegen before this file is included):
//   _BaseColorMap, _NormalMap, _MaskMap, _HeightMap (textures + samplers)
//   _BaseColor, _TileSize, _BlendSharpness, _PomAmplitude, _PomSteps,
//   _AORemapMin, _NormalStrength

#ifndef SHADERGRAPH_PREVIEW

// How hard the hex selection snaps. Higher = cleaner bricks, tighter
// transition band hugging the mortar.
#define STONE_HEX_CONTRAST 12.0

float2 StoneHexHash(float2 p)
{
    return frac(sin(float2(dot(p, float2(127.1, 311.7)),
                           dot(p, float2(269.5, 183.3)))) * 43758.5453);
}

// Which three hex-lattice cells cover this point, and with what weights.
void StoneTriangleGrid(float2 uv,
                       out float w1, out float w2, out float w3,
                       out float2 v1, out float2 v2, out float2 v3)
{
    uv *= 3.4641016; // 2 * sqrt(3): hex cells ~1/3.5 of a texture repeat
    const float2x2 gridToSkewedGrid = float2x2(1.0, -0.57735027, 0.0, 1.15470054);
    float2 skewedCoord = mul(gridToSkewedGrid, uv);
    float2 baseId = floor(skewedCoord);
    float3 temp = float3(frac(skewedCoord), 0);
    temp.z = 1.0 - temp.x - temp.y;
    if (temp.z > 0.0)
    {
        w1 = temp.z; w2 = temp.y; w3 = temp.x;
        v1 = baseId; v2 = baseId + float2(0, 1); v3 = baseId + float2(1, 0);
    }
    else
    {
        w1 = -temp.z; w2 = 1.0 - temp.y; w3 = 1.0 - temp.x;
        v1 = baseId + float2(1, 1); v2 = baseId + float2(1, 0); v3 = baseId + float2(0, 1);
    }
}

// The three offset taps for one plane point, with height-biased weights, and
// the blended height (which, with near-hard weights, is effectively the
// selected copy's height). Per-hex UV offsets only (no rotation): gradients
// stay continuous, so one ddx/ddy pair serves all three taps, and normal maps
// need no counter-rotation.
struct StoneHex
{
    float2 uv1, uv2, uv3;
    float3 w;
    float height;
};

StoneHex StoneHexAt(float2 uv, float2 dx, float2 dy)
{
    float w1, w2, w3; float2 v1, v2, v3;
    StoneTriangleGrid(uv, w1, w2, w3, v1, v2, v3);
    StoneHex h;
    h.uv1 = uv + StoneHexHash(v1);
    h.uv2 = uv + StoneHexHash(v2);
    h.uv3 = uv + StoneHexHash(v3);
    float3 hgt = float3(
        SAMPLE_TEXTURE2D_GRAD(_HeightMap, sampler_HeightMap, h.uv1, dx, dy).r,
        SAMPLE_TEXTURE2D_GRAD(_HeightMap, sampler_HeightMap, h.uv2, dx, dy).r,
        SAMPLE_TEXTURE2D_GRAD(_HeightMap, sampler_HeightMap, h.uv3, dx, dy).r);
    // Height-aware, near-hard selection: the copy standing proudest where its
    // barycentric weight is still alive wins, so the seam falls in the mortar.
    float3 w = float3(w1, w2, w3);
    w = pow(max(w * (hgt + 0.05), 1e-5), STONE_HEX_CONTRAST);
    h.w = w / (w.x + w.y + w.z);
    h.height = dot(h.w, hgt);
    return h;
}

#define STONE_SAMPLE_HEX(tex, hex, dx, dy, outVar)                                     \
    {                                                                                  \
        outVar = SAMPLE_TEXTURE2D_GRAD(tex, sampler##tex, (hex).uv1, dx, dy) * (hex).w.x \
               + SAMPLE_TEXTURE2D_GRAD(tex, sampler##tex, (hex).uv2, dx, dy) * (hex).w.y \
               + SAMPLE_TEXTURE2D_GRAD(tex, sampler##tex, (hex).uv3, dx, dy) * (hex).w.z; \
    }

// ---- parallax occlusion on one projection plane ---------------------------
// Heights use the HDRP convention the material carried: center 1, so height 1
// is flush stone and everything below is recessed by up to _PomAmplitude.
float2 StonePom(float2 uv, float3 viewTS, float2 dx, float2 dy)
{
    float steps = clamp(_PomSteps, 4.0, 32.0);
    // UV travel per unit of normalized descent, over the full amplitude.
    float2 rayUV = -viewTS.xy / max(viewTS.z, 0.15) * (_PomAmplitude / max(_TileSize, 1e-3));
    float stepSize = 1.0 / steps;
    float rayHeight = 1.0;
    float2 curUV = uv;
    float2 prevUV = uv;
    float prevHeight = 1.0;
    float surfHeight = 1.0;
    [loop]
    for (int i = 0; i < (int)steps; i++)
    {
        surfHeight = StoneHexAt(curUV, dx, dy).height;
        if (surfHeight >= rayHeight)
            break;
        prevUV = curUV;
        prevHeight = rayHeight;
        rayHeight -= stepSize;
        curUV += rayUV * stepSize;
    }
    // One secant refinement between the straddling samples.
    float prevSurf = StoneHexAt(prevUV, dx, dy).height;
    float denom = (prevHeight - prevSurf) - (rayHeight - surfHeight);
    float t = abs(denom) > 1e-5 ? saturate((prevHeight - prevSurf) / denom) : 0.0;
    return lerp(prevUV, curUV, t);
}

#endif // SHADERGRAPH_PREVIEW

// ---- the surface ----------------------------------------------------------

void StoneSurface_float(
    float3 PositionWS,
    float3 NormalWS,
    float3 ViewDirWS,
    out float3 BaseColor,
    out float3 NormalOut,
    out float Metallic,
    out float Smoothness,
    out float Occlusion)
{
#ifdef SHADERGRAPH_PREVIEW
    BaseColor = float3(0.5, 0.5, 0.5);
    NormalOut = float3(0, 0, 1);
    Metallic = 0;
    Smoothness = 0.4;
    Occlusion = 1;
#else
    float tile = max(_TileSize, 1e-3);
    // HDRP world space is camera-relative; the stone must anchor to the
    // absolute world or it swims when the camera moves.
    float3 posAbs = GetAbsolutePositionWS(PositionWS);
    float3 n = normalize(NormalWS);
    float3 v = normalize(ViewDirWS);

    // Triplanar weights, sharpened so one plane dominates on axis-built walls.
    float3 w = pow(abs(n), max(_BlendSharpness, 1.0));
    w /= (w.x + w.y + w.z);

    // The three plane UVs (world meters over tile size).
    float2 uvX = posAbs.zy / tile;
    float2 uvY = posAbs.xz / tile;
    float2 uvZ = posAbs.xy / tile;
    float2 dxX = ddx(uvX), dyX = ddy(uvX);
    float2 dxY = ddx(uvY), dyY = ddy(uvY);
    float2 dxZ = ddx(uvZ), dyZ = ddy(uvZ);

    // Parallax on the dominant plane only: project the view direction into
    // that plane's implied tangent frame and march the hex-tiled height.
    if (w.x >= w.y && w.x >= w.z)
    {
        float s = n.x >= 0 ? 1.0 : -1.0;
        uvX = StonePom(uvX, float3(v.z, v.y, v.x * s), dxX, dyX);
    }
    else if (w.y >= w.z)
    {
        float s = n.y >= 0 ? 1.0 : -1.0;
        uvY = StonePom(uvY, float3(v.x, v.z, v.y * s), dxY, dyY);
    }
    else
    {
        float s = n.z >= 0 ? 1.0 : -1.0;
        uvZ = StonePom(uvZ, float3(v.x, v.y, v.z * s), dxZ, dyZ);
    }

    float4 colX = 0, colY = 0, colZ = 0;
    float4 maskX = 0, maskY = 0, maskZ = 0;
    float4 nrmX = 0, nrmY = 0, nrmZ = 0;
    if (w.x > 0.015)
    {
        StoneHex hex = StoneHexAt(uvX, dxX, dyX);
        STONE_SAMPLE_HEX(_BaseColorMap, hex, dxX, dyX, colX);
        STONE_SAMPLE_HEX(_MaskMap, hex, dxX, dyX, maskX);
        STONE_SAMPLE_HEX(_NormalMap, hex, dxX, dyX, nrmX);
    }
    if (w.y > 0.015)
    {
        StoneHex hex = StoneHexAt(uvY, dxY, dyY);
        STONE_SAMPLE_HEX(_BaseColorMap, hex, dxY, dyY, colY);
        STONE_SAMPLE_HEX(_MaskMap, hex, dxY, dyY, maskY);
        STONE_SAMPLE_HEX(_NormalMap, hex, dxY, dyY, nrmY);
    }
    if (w.z > 0.015)
    {
        StoneHex hex = StoneHexAt(uvZ, dxZ, dyZ);
        STONE_SAMPLE_HEX(_BaseColorMap, hex, dxZ, dyZ, colZ);
        STONE_SAMPLE_HEX(_MaskMap, hex, dxZ, dyZ, maskZ);
        STONE_SAMPLE_HEX(_NormalMap, hex, dxZ, dyZ, nrmZ);
    }
    // Renormalize over the planes actually sampled.
    float3 ws = float3(w.x > 0.015 ? w.x : 0, w.y > 0.015 ? w.y : 0, w.z > 0.015 ? w.z : 0);
    ws /= (ws.x + ws.y + ws.z);

    float4 col = colX * ws.x + colY * ws.y + colZ * ws.z;
    float4 mask = maskX * ws.x + maskY * ws.y + maskZ * ws.z;

    BaseColor = col.rgb * _BaseColor.rgb;
    Metallic = mask.r;
    Occlusion = lerp(_AORemapMin, 1.0, mask.g);
    Smoothness = mask.a;

    // Whiteout-blend triplanar normal (bgolus): abs(z)*axis handles the sign.
    float3 tnX = UnpackNormalScale(nrmX, _NormalStrength);
    float3 tnY = UnpackNormalScale(nrmY, _NormalStrength);
    float3 tnZ = UnpackNormalScale(nrmZ, _NormalStrength);
    tnX = float3(tnX.xy + n.zy, abs(tnX.z) * n.x);
    tnY = float3(tnY.xy + n.xz, abs(tnY.z) * n.y);
    tnZ = float3(tnZ.xy + n.xy, abs(tnZ.z) * n.z);
    NormalOut = normalize(tnX.zyx * ws.x + tnY.xzy * ws.y + tnZ.xyz * ws.z);
#endif
}

#endif // STONE_TRIPLANAR_HEX_INCLUDED
