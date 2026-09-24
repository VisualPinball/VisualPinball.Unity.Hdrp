// Visual Pinball Engine
// Copyright (C) 2026 freezy and VPE Team
// SPDX-License-Identifier: GPL-3.0-or-later
#ifndef VPE_INSERT_COVER_INCLUDED
#define VPE_INSERT_COVER_INCLUDED

// Cover UVs and their tangent frame follow the referenced playfield's XZ
// projection. Its texture and source strength are supplied per material slot.
// The haze is the colour the cover blends in at its opacity: a light veil that
// softens the ribs underneath the way a worn, slightly frosted top does.
// Hue, saturation and value, hue as a turn of the wheel in 0..1.
float3 InsertCoverRgbToHsv(float3 c)
{
    float4 K = float4(0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0);
    float4 p = lerp(float4(c.bg, K.wz), float4(c.gb, K.xy), step(c.b, c.g));
    float4 q = lerp(float4(p.xyw, c.r), float4(c.r, p.yzx), step(p.x, c.r));
    float d = q.x - min(q.w, q.y);
    float e = 1.0e-10;
    return float3(abs(q.z + (q.w - q.y) / (6.0 * d + e)), d / (q.x + e), q.x);
}

float3 InsertCoverHsvToRgb(float3 c)
{
    float4 K = float4(1.0, 2.0 / 3.0, 1.0 / 3.0, 3.0);
    float3 p = abs(frac(c.xxx + K.xyz) * 6.0 - K.www);
    return c.z * lerp(K.xxx, saturate(p - K.xxx), c.y);
}

// Turns the colour's hue towards the lamp's, the short way round the wheel,
// and its saturation towards the lamp's, never below its own, both by the
// amount; the value is never touched, so a grey haze comes out the lamp's
// colour, as bright as the grey was, and never darker. The lamp's own
// brightness scales the amount, since an RGB lamp carries its intensity in
// its colour, so a lamp that is off, or whose channels are all down, leaves
// the colour untouched, and a white lamp has no hue to give.
float3 InsertCoverTowardsLampHue(float3 color, float3 lamp, float amount)
{
    float3 hsv = InsertCoverRgbToHsv(max(color, 0.0));
    float3 lampHsv = InsertCoverRgbToHsv(max(lamp, 0.0));
    amount = saturate(amount) * saturate(lampHsv.z);
    float turn = frac(lampHsv.x - hsv.x + 0.5) - 0.5;
    hsv.x = frac(hsv.x + turn * amount);
    hsv.y = lerp(hsv.y, max(hsv.y, lampHsv.y), amount);
    return InsertCoverHsvToRgb(hsv);
}

void InsertCover_float(float2 UV, UnityTexture2D NormalMap, float PlayfieldStrength,
    float Strength, float Smoothness, float Opacity, float3 HazeColor,
    float3 LampColor, float LampLevel, float LampTint,
    out float3 NormalTS, out float SurfaceSmoothness, out float Alpha, out float3 BaseColor)
{
#if defined(SHADERPASS) && defined(SHADERPASS_SHADOWS) && (SHADERPASS == SHADERPASS_SHADOWS)
    clip(-1);
#endif
    float3 normal = UnpackNormal(SAMPLE_TEXTURE2D(NormalMap.tex, NormalMap.samplerstate, UV * NormalMap.scaleTranslate.xy + NormalMap.scaleTranslate.zw));
    NormalTS = normalize(float3(normal.xy * max(PlayfieldStrength * Strength, 0.0), normal.z));
    SurfaceSmoothness = saturate(Smoothness);
    Alpha = saturate(Opacity);
    // The haze's hue and saturation turn to the lamp's as far as the insert
    // light asks and the lamp is lit, its value untouched, so a coloured lamp
    // is not milked by a grey veil, and a lamp that is off leaves the haze
    // its own colour, as bright as ever.
    BaseColor = InsertCoverTowardsLampHue(max(HazeColor, 0.0), LampColor, saturate(LampLevel) * saturate(LampTint));
}
#endif
