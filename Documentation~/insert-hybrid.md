# Hybrid insert optics

The `Visual Pinball/Insert Hybrid` shader combines a Blender bake with a virtual lamp underneath the insert. It is an opaque HDRP Lit material: reflections and shadows use HDRP, while the apparent transmitted light is emitted by the insert itself. It needs neither a screen-space refraction buffer nor a real light below the playfield to show the lamp.

## Authoring

1. Generate an insert and enable **Bake Hybrid Optics** in its inspector. Bake with Blender 5 using the bundled scripts; no insert add-on is required. UVPackMaster remains optional.
2. Click **Use HDRP Hybrid Material** after the bake. This creates a new material and keeps the previous material asset. Undo restores the previous assignment; generated assets stay in the bake folder for reuse or manual cleanup. The button requires the HDRP package and an unshared, fresh bake; a duplicated insert must bake its own data first. Rebaking a duplicate preserves its hybrid shader and tuned parameters in a separate material.
3. Click **Add Brightness Controls and Spill Light**. The new **Insert Light** component exposes **Brightness**, **Level**, **Color**, **Spill Lumens** and **Spill Range**. Brightness scales both the lens and the real light; Level is the 0–1 dimmer. Color and Level preview the lamp in edit mode. The generated point light sits just above the insert so it can illuminate the playfield and nearby objects; its local position is editable. The initial spill has no shadows for a low-cost glow; enable shadows on the light if surrounding occluders need them.
4. **Virtual Lamp** is local-space XYZ and radius W, all in metres. Its default sits below the mesh. Move it sideways to shift the bright patch, lower it to increase parallax, or increase its radius to soften the moving image. **Bulb Definition** adds a tighter source image inside that halo. All faces refract the same source; their brightness variation does not move or blur the bulb independently. These controls do not require a rebake. The bake's broad scattered-light component stays fixed when the virtual lamp is moved.
5. **Facet Contrast** emphasizes opposite starburst slopes. **Mottle Amount** adds restrained, softly blended cellular variation and small differences between faces. **Mottle Size (mm)** sets the cell spacing (smaller means finer detail), and **Grain** adds a separate fine texture to the illuminated and unlit plastic. **Variation Seed** chooses another pattern. Both textures are fixed in local space and fade out when their detail becomes smaller than a pixel. The bake supplies the actual fan centre and sector count for the face variation. None of these controls needs a rebake.
6. Keep **Edge Glow** and **Surface Normal Strength** low: bevels provide small reflections, while the broader face response carries the pattern. Balance **Baked Scatter** against **Virtual Lamp Strength**; too much scatter or brightness washes out the starburst. **Optical Facet Strength** changes ray distortion independently of surface highlights.

The material's **Lamp Emission (HDR)** is the nominal radiance, initially 600 per channel. Keep it neutral white for an RGB insert. This is an exposure-dependent artistic value, not a physical bulb calibration; **Spill Lumens** independently calibrates the actual light's output at brightness one. With Insert Light attached, use that component's Brightness and Level controls; it overrides the corresponding material preview properties per renderer. Without the component, the material's Brightness and Lamp Level remain usable.

The geometry and baked atlas remain those of the insert generator. Rebaking updates all three textures together, including optics whenever the current baked material uses them. It preserves the material's tuned parameters. Geometry edits invalidate the bake but keep the current material as a preview. An inspector warning asks you to bake again, since the old textures may no longer align with the new geometry. Until then, the mesh uses its generated UVs and freshly calculated tangents; stale baked vertex data is never reused. **Clear Bake** restores the original unbaked material.

## Runtime RGB

Place the renderer on or below a VPE `LightComponent`, as with other emissive lamp meshes. VPE drives `_InsertLampLevel` and `_InsertLampColor` through a `MaterialPropertyBlock`, without changing the shared material or any textures. The RGB property is a linear vector; VPE converts incoming Unity `Color` values with `.linear`. Colour events do not change the current level, fading does not reset the last colour, and an off insert keeps its visible reflective plastic surface. At startup, an ordinary child Unity light supplies the initial colour when present; otherwise it starts white.

`InsertLightComponent` follows VPE's renderer colour and level in `LateUpdate` and applies them to its real spill light, including the per-insert Brightness multiplier. VPE excludes this managed light from its independent native-light cache so there is only one owner. Lamp Manager previews also update the component's Color and Level in edit mode. Removing the hybrid material disables the spill. Without a parent VPE Light, the component's Color and Level control it at runtime as well as in the editor. The virtual lamp remains an image within the insert; the real point light supplies environmental illumination. Ordinary emissive HDRP material behaviour and any enabled SSGI still apply.

## Bake data

| Asset | Encoding | Use |
| --- | --- | --- |
| Normal Map | Existing tangent-space normal bake | HDRP reflections, with adjustable strength |
| Optical Normals | Linear RGB = sharp local normal × 0.5 + 0.5 | Optical direction without rounded creases, independent of atlas rotation |
| Optical Response | Linear R: normalized cavity irradiance; G: rounded-edge response; B: transmission mask | Broad internal light, ridge/rim glow and view-dependent lamp weighting |

The optical maps use the same regular/flat projections, UVs and padding as the normal bake. Blender illuminates a white diffuse cavity proxy from above, bakes direct and indirect light, normalizes it and lightly filters the low-frequency result. It separately bakes sharp optical normals and bevel edge angles. The existing surface normal bake retains its bevels for reflections. All optical coefficients are neutral: lamp hue is applied only in Unity, so RGB changes never require a new bake. The two data textures import with sRGB and CPU readability disabled, clamping and no compression. Older hybrid bakes need one rebake for sharp optical normals and the fan metadata.

The shader refracts the object-space view direction through the baked optical normal and intersects a virtual lamp plane. A tight source inside a soft halo approximates the refracted bulb, with another broad lobe for an internal reflected image. View position is evaluated per fragment and per eye. It does not sample the screen, so the lamp remains available off-screen and behind opaque playfield geometry.

## Limits

This is an artistic light-transport approximation. The generated mesh is an open tray, so the bake is not transmission through a closed plastic solid, and the runtime lobe is not a simulated caustic. There is no separate flat clear top surface, printed insert artwork layer, real thickness-dependent absorption, or full internal reflection solution. The current controls work best at ordinary playfield viewing angles and with a uniformly scaled insert. A more physical bake would first need a closed optical source mesh.

The extra shader cost over Lit includes two texture samples, a refracted direction, analytic lamp evaluations, procedural facet variation, a nine-cell soft cellular pattern and fine value noise. Each enabled spill is also a real point light. The optical maps add texture memory; 1024² uncompressed RGBA textures with mipmaps take roughly 5.3 MiB each. Atlas packing and lower bake resolutions are useful when a table has many distinct inserts.
