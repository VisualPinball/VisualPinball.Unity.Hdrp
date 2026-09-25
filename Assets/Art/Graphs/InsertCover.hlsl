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
// colour, as bright as the grey was, and never darker. An RGB lamp carries
// its intensity in its colour, and a dim lamp is still its colour: the amount
// is full from a tenth of full brightness up, and only a lamp that is off,
// its channels all down, leaves the colour untouched. A white lamp has no hue
// to give.
float3 InsertCoverTowardsLampHue(float3 color, float3 lamp, float amount)
{
    float3 hsv = InsertCoverRgbToHsv(max(color, 0.0));
    float3 lampHsv = InsertCoverRgbToHsv(max(lamp, 0.0));
    amount = saturate(amount) * saturate(lampHsv.z * 10.0);
    float turn = frac(lampHsv.x - hsv.x + 0.5) - 0.5;
    hsv.x = frac(hsv.x + turn * amount);
    hsv.y = lerp(hsv.y, max(hsv.y, lampHsv.y), amount);
    return InsertCoverHsvToRgb(hsv);
}

// The light the bulbs send to a point, the same the body's glow takes: every
// lit bulb reaches it by the inverse square of the distance over its spread,
// bulbs that are off adding nothing and taking nothing away, and the colour
// is the nearest lit bulb's, the others blending in by the inverse square of
// their distance. Each lamp is a position in insert space with its radius in
// w, off at zero; each drive a linear colour with the level in w, the first
// split in two.
float3 InsertCoverLampMix(float2 position, float4 lampA, float4 lampB, float4 lampC, float4 lampD,
    float3 colorA, float levelA, float4 driveB, float4 driveC, float4 driveD, float spread)
{
    float4 lamps[4] = { lampA, lampB, lampC, lampD };
    float3 drives[4] = { max(colorA, 0.0) * saturate(levelA), max(driveB.rgb, 0.0) * saturate(driveB.w),
        max(driveC.rgb, 0.0) * saturate(driveC.w), max(driveD.rgb, 0.0) * saturate(driveD.w) };
    float3 mixed = 0.0;
    float weight = 0.0;
    float lit = 0.0;
    [unroll]
    for (int i = 0; i < 4; i++) {
        if (i > 0 && lamps[i].w <= 0.0) continue;
        float radius = max(abs(lamps[i].w), 0.0001);
        float2 apart = position - lamps[i].xz;
        // The body weighs each bulb by its lamp spread as well: the same here,
        // or the two would tint a point differently.
        float leak = 1.0;
        if (spread > 0.0) {
            float2 away = apart / (spread * 0.001);
            leak = rcp(1.0 + dot(away, away));
        }
        float level = max(drives[i].r, max(drives[i].g, drives[i].b));
        float near = leak * rcp(dot(apart, apart) + radius * radius);
        mixed += near * drives[i];
        weight += near * level;
        lit += leak * level;
    }
    return lit * mixed / max(weight, 1e-6);
}

void InsertCover_float(float2 UV, UnityTexture2D NormalMap, float PlayfieldStrength,
    float Strength, float Smoothness, float Opacity, float3 HazeColor,
    float3 LampColor, float LampLevel, float LampTint,
    float3 Position, float4 LampA, float4 LampB, float4 LampC, float4 LampD,
    float4 DriveB, float4 DriveC, float4 DriveD, float Spread,
    out float3 NormalTS, out float SurfaceSmoothness, out float Alpha, out float3 BaseColor)
{
#if defined(SHADERPASS) && defined(SHADERPASS_SHADOWS) && (SHADERPASS == SHADERPASS_SHADOWS)
    clip(-1);
#endif
    float3 normal = UnpackNormal(SAMPLE_TEXTURE2D(NormalMap.tex, NormalMap.samplerstate, UV * NormalMap.scaleTranslate.xy + NormalMap.scaleTranslate.zw));
    NormalTS = normalize(float3(normal.xy * max(PlayfieldStrength * Strength, 0.0), normal.z));
    SurfaceSmoothness = saturate(Smoothness);
    Alpha = saturate(Opacity);
    // The haze's hue and saturation turn to the nearest lit bulb's, as far as
    // the insert light asks and that bulb's light reaches, its value
    // untouched, so a coloured lamp is not milked by a grey veil, a banana lit
    // red, green and blue along its length is hazed red, green and blue, and
    // lamps that are off leave the haze its own colour, as bright as ever.
    float3 mixed = InsertCoverLampMix(Position.xz, LampA, LampB, LampC, LampD, LampColor, LampLevel, DriveB, DriveC, DriveD, Spread);
    BaseColor = InsertCoverTowardsLampHue(max(HazeColor, 0.0), mixed, saturate(LampTint));
}
#endif
