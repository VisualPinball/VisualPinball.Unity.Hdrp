// Visual Pinball Engine
// Copyright (C) 2026 freezy and VPE Team
// SPDX-License-Identifier: GPL-3.0-or-later
#ifndef VPE_INSERT_OPTICS_INCLUDED
#define VPE_INSERT_OPTICS_INCLUDED

float3 InsertFacetRandom(float sector, float seed)
{
    return frac(sin((sector + seed * 17.13) * float3(127.1, 311.7, 74.7)) * 43758.5453);
}

float3 InsertNoiseHash(float2 p)
{
    float3 h = frac(float3(p.x, p.y, p.x) * 0.1031);
    h += dot(h, h.yxz + 33.33);
    return frac((h.xxy + h.yzz) * h.zyx);
}

// Soft nearest-cell values: small cloudy variations without hard Voronoi
// borders. Coordinates are local metres, so atlas seams cannot split them.
float InsertCellular(float2 p)
{
    float2 cell = floor(p);
    float2 local = frac(p);
    float value = 0.0;
    float weight = 0.0;
    [unroll] for (int y = -1; y <= 1; y++) {
        [unroll] for (int x = -1; x <= 1; x++) {
            float2 offset = float2(x, y);
            float3 random = InsertNoiseHash(cell + offset);
            float2 delta = offset + 0.15 + 0.7 * random.xy - local;
            float w = exp2(-8.0 * dot(delta, delta));
            value += random.z * w;
            weight += w;
        }
    }
    return value / max(weight, 1e-5);
}

float InsertGrain(float2 p)
{
    float2 cell = floor(p);
    float2 t = frac(p);
    t = t * t * (3.0 - 2.0 * t);
    return lerp(lerp(InsertNoiseHash(cell).z, InsertNoiseHash(cell + float2(1, 0)).z, t.x),
        lerp(InsertNoiseHash(cell + float2(0, 1)).z, InsertNoiseHash(cell + 1.0).z, t.x), t.y);
}

float InsertDetailVisibility(float2 p)
{
    float footprint = max(length(ddx(p)), length(ddy(p)));
    return 1.0 - smoothstep(0.35, 1.0, footprint);
}

// All positions and directions are in insert local space, in metres. This
// works per eye and at arbitrary camera angles without a scene-colour buffer.
// Hue, saturation and value, hue as a turn of the wheel in 0..1.
float3 InsertOpticsRgbToHsv(float3 c)
{
    float4 K = float4(0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0);
    float4 p = lerp(float4(c.bg, K.wz), float4(c.gb, K.xy), step(c.b, c.g));
    float4 q = lerp(float4(p.xyw, c.r), float4(c.r, p.yzx), step(p.x, c.r));
    float d = q.x - min(q.w, q.y);
    float e = 1.0e-10;
    return float3(abs(q.z + (q.w - q.y) / (6.0 * d + e)), d / (q.x + e), q.x);
}

float3 InsertOpticsHsvToRgb(float3 c)
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
float3 InsertOpticsTowardsLampHue(float3 color, float3 lamp, float amount)
{
    float3 hsv = InsertOpticsRgbToHsv(max(color, 0.0));
    float3 lampHsv = InsertOpticsRgbToHsv(max(lamp, 0.0));
    amount = saturate(amount) * saturate(lampHsv.z * 10.0);
    float turn = frac(lampHsv.x - hsv.x + 0.5) - 0.5;
    hsv.x = frac(hsv.x + turn * amount);
    hsv.y = lerp(hsv.y, max(hsv.y, lampHsv.y), amount);
    return InsertOpticsHsvToRgb(hsv);
}

void InsertOptics_float(float3 Position, float3 View, float2 UV,
    UnityTexture2D OpticalNormal, UnityTexture2D Response, UnityTexture2D SurfaceNormal,
    float4 Lamp, float Ior, float Warp, float Scatter, float EdgeGlow, float LampGain,
    float NormalStrength, float3 Tint, float3 LampColor, float3 DriveColor, float Level,
    float4 Starburst, float Contrast, float Variation, float Seed, float Brightness,
    float MottleSize, float Grain, float BulbDefinition, float MinBrightness,
    float3 WorldNormal, float3 WorldTangent, float3 WorldBitangent, float Flatten,
    float Clarity, float3 CavityColor, float CavitySpread, float CavityContrast,
    float LampTint,
    out float3 BaseColor, out float3 NormalTS, out float3 Emission)
{
    float3 rawNormal = SAMPLE_TEXTURE2D(OpticalNormal.tex, OpticalNormal.samplerstate, UV).rgb * 2.0 - 1.0;
    float3 n = normalize(rawNormal + float3(0, 1e-5, 0));
    float3 response = SAMPLE_TEXTURE2D(Response.tex, Response.samplerstate, UV).rgb;
    float3 v = normalize(View + float3(0, 1e-5, 0));
    float2 radial = Position.xz - Starburst.xy;
    float angle = atan2(radial.y, radial.x);
    float coordinate = frac(angle * (1.0 / 6.28318530718) + 1.0) * max(Starburst.z, 1.0);
    float sector = floor(coordinate);
    float3 random = InsertFacetRandom(sector, Seed);
    // Fade the random coefficients across less than one screen pixel at a
    // crease. Their identity comes from the fan, never quantized bake noise.
    float width = min(0.45, max(fwidth(coordinate), 0.01));
    float part = frac(coordinate);
    float previous = fmod(sector + max(Starburst.z, 1.0) - 1.0, max(Starburst.z, 1.0));
    float next = fmod(sector + 1.0, max(Starburst.z, 1.0));
    random = lerp(InsertFacetRandom(previous, Seed), random, smoothstep(-width, width, part));
    random = lerp(random, InsertFacetRandom(next, Seed), smoothstep(1.0 - width, 1.0 + width, part));
    float floorMask = step(4.0, Starburst.z) * smoothstep(0.35, 0.7, response.b)
        * smoothstep(Starburst.w - 0.00015, Starburst.w + 0.00015, Position.y);
    float2 noisePosition = Position.xz + Position.y * float2(0.37, 0.71);
    float2 cells = noisePosition / max(MottleSize * 0.001, 0.0001) + Seed * float2(7.13, 3.71);
    float mottle = (InsertCellular(cells) - 0.5) * InsertDetailVisibility(cells);
    float2 grainPosition = noisePosition * 4000.0 + Seed * float2(13.1, 17.7);
    float grain = (InsertGrain(grainPosition) - 0.5) * InsertDetailVisibility(grainPosition);
    float finish = 1.0 + grain * Grain;
    // Optical normal strength is independent of surface reflection strength.
    // Flattening steep walls keeps this deliberately artistic ray pointed down.
    n = normalize(float3(n.x * Warp, max(abs(n.y), 0.25), n.z * Warp));
    float3 ray = refract(-v, n, rcp(max(Ior, 1.001)));
    float distance = max(Position.y - Lamp.y, 0.0) / max(-ray.y, 0.12);
    float2 hit = Position.xz + ray.xz * distance;
    float radius = max(abs(Lamp.w), 0.0001);
    // Keep a common source behind every face. Moving the source independently
    // per sector turns the bulb into unrelated gradients and destroys depth.
    float2 delta = (hit - Lamp.xz) / radius;
    float direct = exp2(-2.0 * dot(delta, delta));
    float bulb = exp2(-12.0 * dot(delta, delta)) * BulbDefinition;
    // A wider reflected image stands in for a second internal bounce.
    float2 reflectedHit = Position.xz - ray.xz * distance * 0.65;
    float2 reflectedDelta = (reflectedHit - Lamp.xz) / (radius * 1.5);
    float bounced = exp2(-2.0 * dot(reflectedDelta, reflectedDelta));
    float facing = saturate(dot(v, n));
    float fresnel = 0.04 + 0.96 * pow(1.0 - facing, 5.0);
    // Opposite slopes couple very differently to the same internal light.
    // This contrast fills the face instead of outlining its rounded edge.
    float2 bias = v.xz + float2(0.38, -0.21);
    bias *= rsqrt(max(dot(bias, bias), 1e-5));
    float coupling = saturate(0.5 + 1.8 * dot(n.xz, bias));
    float faceGain = lerp(1.0, 0.12 + 1.8 * pow(coupling, 1.3), Contrast * floorMask);
    faceGain *= 1.0 + Variation * floorMask * ((random.z - 0.5) * 0.18 + mottle * 0.7);
    // The tight source image survives even on the less efficient slopes;
    // the broad transmitted component still carries face-to-face contrast.
    float transmitted = response.b * (1.0 - 0.65 * fresnel)
        * ((direct + 0.17 * bounced) * faceGain + bulb * lerp(1.0, faceGain, 0.25));
    float glow = Scatter * response.r * lerp(1.0, faceGain, floorMask * 0.65)
        + EdgeGlow * response.g * (0.25 + 0.75 * response.r);
    // Every term is achromatic until here: RGB lamp events can tint all of it.
    // The material sets full-intensity brightness; ROM events only change the
    // normalized intensity and RGB through the renderer's property block.
    Emission = max(LampColor, 0.0) * max(DriveColor, 0.0) * saturate(Level) * max(Brightness, 0.0) * (glow + LampGain * transmitted) * finish;
    // Darken the unlit plastic without keeping the bulb or spill light on.
    // Fade back to the authored albedo as the ROM raises lamp intensity.
    float bodyBrightness = lerp(saturate(MinBrightness), 1.0, saturate(Level));
    float cavity = 0.35 + 0.65 * response.r;
    float3 tintedBody = Tint * cavity * bodyBrightness;
    // Clear plastic absorbs nothing: an off clear insert shows the bright lamp
    // board or reflector under it, lit by room light that came in through the
    // top, lamp or no lamp. Each face shows the part of that cavity its
    // refracted view ray lands on, spread wide and kept soft, so the ribs shift
    // with the view instead of alternating with the lights. The walls and the
    // channel get their share by internal reflection: the baked irradiance.
    float spread = radius * max(CavitySpread, 0.1);
    float2 cavityDelta = (hit - Lamp.xz) / spread;
    float2 cavityBounce = (reflectedHit - Lamp.xz) / (spread * 1.5);
    float seen = saturate(exp2(-dot(cavityDelta, cavityDelta)) + 0.3 * exp2(-dot(cavityBounce, cavityBounce)));
    float cavityView = lerp(1.0, seen, saturate(CavityContrast));
    // Room light reaches the whole cavity, so its irradiance shapes the clear
    // body less than it darkens the tinted one.
    float3 clearBody = CavityColor * (0.6 + 0.4 * response.r) * lerp(0.6, cavityView, floorMask);
    BaseColor = lerp(tintedBody, Tint * clearBody, saturate(Clarity)) * finish;
    // A grey body under a coloured lamp milks its colour. Tinted with the lamp,
    // the body's hue and saturation turn to the lamp's as far as the lamp is
    // lit, its value untouched, so nothing of the lamp stays once it is off.
    BaseColor = InsertOpticsTowardsLampHue(BaseColor, DriveColor, saturate(Level) * saturate(LampTint));
    float4 packedNormal = SAMPLE_TEXTURE2D(SurfaceNormal.tex, SurfaceNormal.samplerstate, UV);
    float3 surface = UnpackNormal(packedNormal);
    float3 shading = normalize(float3(surface.xy * NormalStrength, lerp(1.0, surface.z, saturate(NormalStrength))));
    // Room light enters a clear insert through its flat top, and the facets
    // underneath only redistribute what is already inside. To the lights and
    // reflection probes the surface is that flat top, so the shading normal is
    // flattened towards it, written in the frame the tangent-space normal is
    // read back in. The optical normal that refracts the lamp stays as baked.
    float3 upWS = TransformObjectToWorldNormal(float3(0.0, 1.0, 0.0));
    // The interpolated frame need not be orthonormal, so this is the frame's
    // proper inverse, not three dot products.
    float3 upTS = TransformWorldToTangent(upWS, float3x3(WorldTangent, WorldBitangent, WorldNormal));
    NormalTS = normalize(lerp(shading, upTS, saturate(Flatten)) + float3(0.0, 0.0, 1e-5));
}
#endif
