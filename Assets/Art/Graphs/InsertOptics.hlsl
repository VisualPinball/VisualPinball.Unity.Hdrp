// Visual Pinball Engine
// Copyright (C) 2026 freezy and VPE Team
// SPDX-License-Identifier: GPL-3.0-or-later
#ifndef VPE_INSERT_OPTICS_INCLUDED
#define VPE_INSERT_OPTICS_INCLUDED

float3 InsertFacetRandom(float sector, float seed)
{
    return frac(sin((sector + seed * 17.13) * float3(127.1, 311.7, 74.7)) * 43758.5453);
}

float3 InsertNoiseHash(float3 p)
{
    float3 h = frac(p * float3(0.1031, 0.1030, 0.0973));
    h += dot(h, h.yxz + 33.33);
    return frac((h.xxy + h.yxx) * h.zyx);
}

// Soft nearest-cell values: small cloudy variations without hard Voronoi
// borders. Coordinates are local metres, in all three axes, so atlas seams
// cannot split them and no slope stretches them: a starburst's facets stand
// steep, and a noise laid out over the floor with a share of the height
// mixed in was sheared along one slope of every ridge and squeezed on the
// other, into streaks along the facets seen on every other pleat. Noise in
// three dimensions is the same on any face, walls included.
float InsertCellular(float3 p)
{
    float3 cell = floor(p);
    float3 local = frac(p);
    float value = 0.0;
    float weight = 0.0;
    [unroll] for (int z = -1; z <= 1; z++) {
        [unroll] for (int y = -1; y <= 1; y++) {
            [unroll] for (int x = -1; x <= 1; x++) {
                float3 offset = float3(x, y, z);
                float3 random = InsertNoiseHash(cell + offset);
                float3 delta = offset + 0.15 + 0.7 * random - local;
                float w = exp2(-8.0 * dot(delta, delta));
                value += InsertNoiseHash(cell + offset + 17.0).x * w;
                weight += w;
            }
        }
    }
    return value / max(weight, 1e-5);
}

float InsertGrain(float3 p)
{
    float3 cell = floor(p);
    float3 t = frac(p);
    t = t * t * (3.0 - 2.0 * t);
    float4 below = float4(InsertNoiseHash(cell).z, InsertNoiseHash(cell + float3(1, 0, 0)).z,
        InsertNoiseHash(cell + float3(0, 1, 0)).z, InsertNoiseHash(cell + float3(1, 1, 0)).z);
    float4 above = float4(InsertNoiseHash(cell + float3(0, 0, 1)).z, InsertNoiseHash(cell + float3(1, 0, 1)).z,
        InsertNoiseHash(cell + float3(0, 1, 1)).z, InsertNoiseHash(cell + 1.0).z);
    float4 layer = lerp(below, above, t.z);
    return lerp(lerp(layer.x, layer.y, t.x), lerp(layer.z, layer.w, t.x), t.y);
}

float InsertDetailVisibility(float3 p)
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

// Turns the colour's hue towards the lamp's, and its saturation towards the
// lamp's, never below its own, both by the amount; the value is never touched, so a grey haze comes out the lamp's
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
    return InsertOpticsHsvToRgb(hsv);
}

// One virtual bulb's share of a point: the halo and the bulb image its facet
// shows (x), how much of the cavity under it a clear body's face looks onto
// (y), and how far the point sits from it for the spread (z, one without a
// spread). Position and the refracted view ray are in insert space, the lamp
// its position in metres with its radius in w.
float3 InsertOpticsBulb(float4 lamp, float3 position, float3 ray, float faceGain, float fresnel, float exit,
    float responseB, float bulbDefinition, float cavitySpread, float spread)
{
    float distance = max(position.y - lamp.y, 0.0) / max(-ray.y, 0.12);
    float2 hit = position.xz + ray.xz * distance;
    float radius = max(abs(lamp.w), 0.0001);
    // Keep a common source behind every face. Moving the source independently
    // per sector turns the bulb into unrelated gradients and destroys depth.
    float2 delta = (hit - lamp.xz) / radius;
    float direct = exp2(-2.0 * dot(delta, delta));
    float bulb = exp2(-12.0 * dot(delta, delta)) * bulbDefinition;
    // A wider reflected image stands in for a second internal bounce.
    float2 reflectedHit = position.xz - ray.xz * distance * 0.65;
    float2 reflectedDelta = (reflectedHit - lamp.xz) / (radius * 1.5);
    float bounced = exp2(-2.0 * dot(reflectedDelta, reflectedDelta));
    // The tight source image survives even on the less efficient slopes;
    // the broad transmitted component still carries face-to-face contrast.
    // Both are what the facet lets through, none at all once it traps the
    // ray; the bounce is what it throws back and stays.
    float transmitted = responseB * (1.0 - 0.65 * fresnel)
        * ((direct * exit + 0.17 * bounced) * faceGain + bulb * exit * lerp(1.0, faceGain, 0.25));
    // Each face shows the part of the cavity its refracted view ray lands on,
    // spread wide and kept soft, so the ribs shift with the view instead of
    // alternating with the lights.
    float wide = radius * max(cavitySpread, 0.1);
    float2 cavityDelta = (hit - lamp.xz) / wide;
    float2 cavityBounce = (reflectedHit - lamp.xz) / (wide * 1.5);
    float seen = exit * exp2(-dot(cavityDelta, cavityDelta)) + 0.3 * exp2(-dot(cavityBounce, cavityBounce));
    // Every facet tilted towards the lamp shows its halo, however far from the
    // lamp it sits, and the baked glow reaches the whole body alike, so raising
    // the brightness floods the body. A real insert is brightest around its
    // bulb and dims away from it, the light spreading out from the bulb as
    // from a point, by the inverse square of the distance: the spread, in
    // millimetres, is the distance from the lamp at which the lit look has
    // fallen to half, a fifth at twice that and a tenth at three times, a long
    // reach so that one bulb of a banana still shows at its far end and two
    // bulbs meet in a soft blend, never a seam. Zero leaves it everywhere alike.
    float leak = 1.0;
    if (spread > 0.0) {
        float2 away = (position.xz - lamp.xz) / (spread * 0.001);
        leak = rcp(1.0 + dot(away, away));
    }
    return float3(transmitted, seen, leak);
}

void InsertOptics_float(float3 Position, float3 View, float2 UV,
    UnityTexture2D OpticalNormal, UnityTexture2D Response, UnityTexture2D SurfaceNormal,
    float4 Lamp, float Ior, float Warp, float Scatter, float EdgeGlow, float LampGain,
    float NormalStrength, float3 Tint, float3 LampColor, float3 DriveColor, float Level,
    float4 Starburst, float Contrast, float Variation, float Seed, float Brightness,
    float MottleSize, float Grain, float BulbDefinition, float MinBrightness,
    float3 WorldNormal, float3 WorldTangent, float3 WorldBitangent, float Flatten,
    float Clarity, float3 CavityColor, float CavitySpread, float CavityContrast,
    float LampTint, float Spread, float4 LampB, float4 LampC, float4 LampD,
    float4 DriveB, float4 DriveC, float4 DriveD,
    out float3 BaseColor, out float3 NormalTS, out float3 Emission)
{
    float3 rawNormal = SAMPLE_TEXTURE2D(OpticalNormal.tex, OpticalNormal.samplerstate, UV).rgb * 2.0 - 1.0;
    float3 n = normalize(rawNormal + float3(0, 1e-5, 0));
    float3 response = SAMPLE_TEXTURE2D(Response.tex, Response.samplerstate, UV).rgb;
    float3 v = normalize(View + float3(0, 1e-5, 0));
    // The facets the bake laid out, found again from the position: a wheel's
    // sectors round its centre, ribs' strips across their direction, which
    // the bake tells apart by a half pitch made negative in z, or rings'
    // strips out from their centre, the half pitch pushed past minus a
    // thousand. Strips are counted from far off, so the index stays positive
    // and the neighbours either side need no wrapping round. A gem, its sides
    // pushed past two thousand, has facets no angle alone finds again, so its
    // variation turns round the centre smoothly, one value a side, with no
    // step to land in the middle of a facet.
    bool ringed = Starburst.z <= -1000.0;
    bool ribbed = Starburst.z < 0.0 && !ringed;
    bool gem = Starburst.z >= 2000.0;
    float sectors = Starburst.z < 0.0 ? 1e6 : gem ? max(Starburst.z - 2000.0, 1.0) : max(Starburst.z, 1.0);
    float coordinate;
    if (ringed) {
        coordinate = length(Position.xz - Starburst.xy) / max(-Starburst.z - 1000.0, 1e-6) + 4096.0;
    } else if (ribbed) {
        coordinate = dot(Position.xz, Starburst.xy) / -Starburst.z + 4096.0;
    } else {
        float2 radial = Position.xz - Starburst.xy;
        float angle = atan2(radial.y, radial.x);
        coordinate = frac(angle * (1.0 / 6.28318530718) + 1.0) * sectors;
    }
    float sector = floor(coordinate);
    float3 random = InsertFacetRandom(sector, Seed);
    // Fade the random coefficients across less than one screen pixel at a
    // crease. Their identity comes from the fan, never quantized bake noise.
    float width = gem ? 0.5 : min(0.45, max(fwidth(coordinate), 0.01));
    float part = frac(coordinate);
    float previous = fmod(sector + sectors - 1.0, sectors);
    float next = fmod(sector + 1.0, sectors);
    random = lerp(InsertFacetRandom(previous, Seed), random, smoothstep(-width, width, part));
    random = lerp(random, InsertFacetRandom(next, Seed), smoothstep(1.0 - width, 1.0 + width, part));
    float floorMask = (Starburst.z < 0.0 ? 1.0 : step(4.0, Starburst.z)) * smoothstep(0.35, 0.7, response.b)
        * smoothstep(Starburst.w - 0.00015, Starburst.w + 0.00015, Position.y);
    float3 cells = Position / max(MottleSize * 0.001, 0.0001) + Seed * float3(7.13, 3.71, 5.29);
    float mottle = (InsertCellular(cells) - 0.5) * InsertDetailVisibility(cells);
    float3 grainPosition = Position * 4000.0 + Seed * float3(13.1, 17.7, 11.3);
    float grain = (InsertGrain(grainPosition) - 0.5) * InsertDetailVisibility(grainPosition);
    float finish = 1.0 + grain * Grain;
    // Optical normal strength is independent of surface reflection strength.
    // Flattening steep walls keeps this deliberately artistic ray pointed down.
    n = normalize(float3(n.x * Warp, max(abs(n.y), 0.25), n.z * Warp));
    // The view ray enters the plastic through its flat top, bending towards
    // the vertical, and leaves it through the facet underneath into the air
    // of the cavity, bending back the other way, the facet a prism, before it
    // runs on down to the bulb. Traced so, the bulb sits its full depth below
    // the insert: it shifts against the body by the whole of the depth times
    // the view's tilt as the eye moves, and each eye of a headset sees it
    // where its own ray meets it. One refraction with the facet's normal kept
    // the steeper in-plastic ray all the way down, half the parallax, and bent
    // it the wrong way round the facet. What the facet lets through falls
    // with the exit's Fresnel reflectance.
    //
    // A facet steep enough traps the ray, total internal reflection, or lets
    // it out so near the grazing that it runs off sideways and never meets
    // the lamp: a starburst of narrow spokes over a tall ridge has such
    // facets everywhere, and seen along its length, where the in-plastic ray
    // crosses every facet at its full tilt, the whole bulb went missing while
    // it showed from the side. Through a V groove that steep a trapped ray
    // leaves by the facet next door, thrown out to the side, so a mirrored
    // path would not find the bulb either. What such a facet would trap or
    // throw away goes straight on instead, as through a flat bottom, blended
    // in as the facet's exit nears the critical angle so nothing jumps: the
    // bulb keeps its place and its parallax from every angle, and the facets
    // keep their prism wherever they pass the ray.
    float eta = max(Ior, 1.001);
    float3 inside = refract(-v, float3(0.0, 1.0, 0.0), rcp(eta));
    float reflectance0 = (eta - 1.0) / (eta + 1.0);
    reflectance0 *= reflectance0;
    // The sine of the facet's exit angle, past one where it traps the ray.
    float cosInside = saturate(dot(-inside, n));
    float sinFacet = eta * sqrt(saturate(1.0 - cosInside * cosInside));
    float passing = 1.0 - smoothstep(0.75, 1.0, sinFacet);
    float3 facetRay = passing > 0.0 ? refract(inside, n, eta) : -v;
    float cosFacet = saturate(dot(facetRay, -n));
    float facetExit = 1.0 - (reflectance0 + (1.0 - reflectance0) * pow(1.0 - cosFacet, 5.0));
    float flatExit = 1.0 - (reflectance0 + (1.0 - reflectance0) * pow(1.0 - saturate(v.y), 5.0));
    float3 ray = normalize(lerp(-v, facetRay, passing));
    float exit = lerp(flatExit, facetExit, passing);
    float facing = saturate(dot(v, n));
    float fresnel = 0.04 + 0.96 * pow(1.0 - facing, 5.0);
    // Opposite slopes couple very differently to the same internal light.
    // This contrast fills the face instead of outlining its rounded edge.
    float2 bias = v.xz + float2(0.38, -0.21);
    bias *= rsqrt(max(dot(bias, bias), 1e-5));
    float coupling = saturate(0.5 + 1.8 * dot(n.xz, bias));
    float faceGain = lerp(1.0, 0.12 + 1.8 * pow(coupling, 1.3), Contrast * floorMask);
    faceGain *= 1.0 + Variation * floorMask * ((random.z - 0.5) * 0.18 + mottle * 0.7);
    // An insert has one bulb or a few, a banana its three or four in a row:
    // every bulb whose radius is set adds its own halo, each fading with its
    // own spread; the baked glow, one for the whole body, fades with the
    // nearest bulb, and a clear body's face looks onto whichever cavity its
    // ray lands on.
    float3 bulbA = InsertOpticsBulb(Lamp, Position, ray, faceGain, fresnel, exit, response.b, BulbDefinition, CavitySpread, Spread);
    float3 bulbB = LampB.w > 0.0 ? InsertOpticsBulb(LampB, Position, ray, faceGain, fresnel, exit, response.b, BulbDefinition, CavitySpread, Spread) : float3(0.0, 0.0, 0.0);
    float3 bulbC = LampC.w > 0.0 ? InsertOpticsBulb(LampC, Position, ray, faceGain, fresnel, exit, response.b, BulbDefinition, CavitySpread, Spread) : float3(0.0, 0.0, 0.0);
    float3 bulbD = LampD.w > 0.0 ? InsertOpticsBulb(LampD, Position, ray, faceGain, fresnel, exit, response.b, BulbDefinition, CavitySpread, Spread) : float3(0.0, 0.0, 0.0);
    // Each bulb is driven on its own, the first by the lamp colour and level,
    // the others by a colour with the level in w: its halo carries its own
    // colour, and the baked glow, one for the whole body, is the plain sum of
    // every bulb's light, each reaching as far as its spread lets it, colour
    // and all. So one bulb of a banana lights its end and fades along the
    // body, undimmed by the bulbs beside it that are off, two bulbs on add up
    // where they meet, and a bulb fading in colours every point it reaches in
    // proportion to its level, from the first glimmer on, instead of its
    // colour creeping in from the bulb once it is bright enough to win.
    float3 driveA = max(DriveColor, 0.0) * saturate(Level);
    float3 driveB = max(DriveB.rgb, 0.0) * saturate(DriveB.w);
    float3 driveC = max(DriveC.rgb, 0.0) * saturate(DriveC.w);
    float3 driveD = max(DriveD.rgb, 0.0) * saturate(DriveD.w);
    float3 transmitted = bulbA.x * bulbA.z * driveA + bulbB.x * bulbB.z * driveB + bulbC.x * bulbC.z * driveC + bulbD.x * bulbD.z * driveD;
    float4 level = float4(max(driveA.r, max(driveA.g, driveA.b)), max(driveB.r, max(driveB.g, driveB.b)),
        max(driveC.r, max(driveC.g, driveC.b)), max(driveD.r, max(driveD.g, driveD.b)));
    float4 reach = float4(bulbA.z, bulbB.z, bulbC.z, bulbD.z);
    float3 glowDrive = reach.x * driveA + reach.y * driveB + reach.z * driveC + reach.w * driveD;
    // The light's colour at unit level, for the tint's hue.
    float3 chroma = glowDrive / max(dot(reach, level), 1e-6);
    // How far the bulbs' colour reaches for the tint: as the light does, full
    // to a tenth of the bulb's own and fading beyond, from a bulb that counts
    // as on from a tenth of full up, so a bulb fading in tints its whole
    // reach at once and grows brighter within it, never creeping outwards
    // from the bulb as the light itself climbs.
    float tinted = dot(saturate(reach * 10.0), saturate(level * 10.0));
    float seen = saturate(bulbA.y + bulbB.y + bulbC.y + bulbD.y);
    float glow = Scatter * response.r * lerp(1.0, faceGain, floorMask * 0.65)
        + EdgeGlow * response.g * (0.25 + 0.75 * response.r);
    // The material sets full-intensity brightness and its emission colour; ROM
    // events only change each bulb's normalized level and RGB through the
    // renderer's property block.
    Emission = max(LampColor, 0.0) * max(Brightness, 0.0) * (glow * glowDrive + LampGain * transmitted) * finish;
    // Darken the unlit plastic without keeping the bulb or spill light on.
    // Fade back to the authored albedo as the ROM raises lamp intensity.
    float bodyBrightness = lerp(saturate(MinBrightness), 1.0, saturate(Level));
    float cavity = 0.35 + 0.65 * response.r;
    float3 tintedBody = Tint * cavity * bodyBrightness;
    // Clear plastic absorbs nothing: an off clear insert shows the bright lamp
    // board or reflector under it, lit by room light that came in through the
    // top, lamp or no lamp. Each face shows the part of that cavity its
    // refracted view ray lands on, as the bulbs found above. The walls and the
    // channel get their share by internal reflection: the baked irradiance.
    float cavityView = lerp(1.0, seen, saturate(CavityContrast));
    // Room light reaches the whole cavity, so its irradiance shapes the clear
    // body less than it darkens the tinted one.
    float3 clearBody = CavityColor * (0.6 + 0.4 * response.r) * lerp(0.6, cavityView, floorMask);
    BaseColor = lerp(tintedBody, Tint * clearBody, saturate(Clarity)) * finish;
    // A grey body under a coloured lamp milks its colour. Tinted with the lamp,
    // the body's hue and saturation turn to the light's, the bulbs' colours
    // summed as the glow sums them, as far as their light reaches, its value
    // untouched, so nothing of the lamps stays once they are off.
    BaseColor = InsertOpticsTowardsLampHue(BaseColor, chroma, saturate(LampTint) * saturate(tinted));
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
