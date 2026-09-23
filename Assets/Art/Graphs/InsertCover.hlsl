// Visual Pinball Engine
// Copyright (C) 2026 freezy and VPE Team
// SPDX-License-Identifier: GPL-3.0-or-later
#ifndef VPE_INSERT_COVER_INCLUDED
#define VPE_INSERT_COVER_INCLUDED

// Cover UVs and their tangent frame follow the referenced playfield's XZ
// projection. Its texture and source strength are supplied per material slot.
void InsertCover_float(float2 UV, UnityTexture2D NormalMap, float PlayfieldStrength,
    float Strength, float Smoothness, float Opacity,
    out float3 NormalTS, out float SurfaceSmoothness, out float Alpha)
{
#if defined(SHADERPASS) && defined(SHADERPASS_SHADOWS) && (SHADERPASS == SHADERPASS_SHADOWS)
    clip(-1);
#endif
    float3 normal = UnpackNormal(SAMPLE_TEXTURE2D(NormalMap.tex, NormalMap.samplerstate, UV * NormalMap.scaleTranslate.xy + NormalMap.scaleTranslate.zw));
    NormalTS = normalize(float3(normal.xy * max(PlayfieldStrength * Strength, 0.0), normal.z));
    SurfaceSmoothness = saturate(Smoothness);
    Alpha = saturate(Opacity);
}
#endif
