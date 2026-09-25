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

// Turns the colour's hue towards the lamp's, and its saturation towards the
// lamp's, never below its own, both by the amount; the value is never touched, so a grey haze comes out the lamp's
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
    // The hue turns the short way round the wheel, by the lamp's share of the
    // saturation the mix ends up with: a grey, which has no hue of its own,
    // takes the lamp's hue at any amount, its saturation growing with it,
    // instead of a hue part of the way round the wheel, so a grey haze half
    // tinted by a blue lamp is a paler blue, never pink; two colours as
    // saturated as each other turn by the amount itself.
    float turn = frac(lampHsv.x - hsv.x + 0.5) - 0.5;
    float share = saturate(amount * lampHsv.y / max(lerp(hsv.y, lampHsv.y, amount), 1e-5));
    hsv.x = frac(hsv.x + turn * share);
    hsv.y = lerp(hsv.y, max(hsv.y, lampHsv.y), amount);
    return InsertCoverHsvToRgb(hsv);
}

// The colour the bulbs send to a point, the same the body's glow takes: every
// bulb's light summed, each reaching by the inverse square of the distance
// over its spread, bulbs that are off adding nothing and taking nothing away,
// so a bulb fading in colours every point it reaches in proportion to its
// level; and in w how far that colour reaches, a bulb counting as on from a
// tenth of full up, so a bulb fading in tints its whole reach at once instead
// of creeping outwards with its light. Each lamp is a position in insert
// space with its radius in w, off at zero; each drive a linear colour with
// the level in w, the first split in two.
float4 InsertCoverLampMix(float2 position, float4 lampA, float4 lampB, float4 lampC, float4 lampD,
    float3 colorA, float levelA, float4 driveB, float4 driveC, float4 driveD, float spread)
{
    float4 lamps[4] = { lampA, lampB, lampC, lampD };
    float3 drives[4] = { max(colorA, 0.0) * saturate(levelA), max(driveB.rgb, 0.0) * saturate(driveB.w),
        max(driveC.rgb, 0.0) * saturate(driveC.w), max(driveD.rgb, 0.0) * saturate(driveD.w) };
    float3 mixed = 0.0;
    float weight = 0.0;
    float tinted = 0.0;
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
        mixed += leak * drives[i];
        weight += leak * level;
        tinted += saturate(leak * 10.0) * saturate(level * 10.0);
    }
    return float4(mixed / max(weight, 1e-6), tinted);
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
    // The haze's hue and saturation turn to the light's, the bulbs' colours
    // summed, as far as the insert light asks and their light reaches, its
    // value untouched, so a coloured lamp is not milked by a grey veil, a
    // banana lit red, green and blue along its length is hazed red, green and
    // blue with the mixes between, and lamps that are off leave the haze its
    // own colour, as bright as ever.
    float4 mixed = InsertCoverLampMix(Position.xz, LampA, LampB, LampC, LampD, LampColor, LampLevel, DriveB, DriveC, DriveD, Spread);
    BaseColor = InsertCoverTowardsLampHue(max(HazeColor, 0.0), mixed.rgb, saturate(LampTint) * saturate(mixed.w));
}
#endif
