# Realistic Plastic Ramp Rendering Plan

Status: Investigation complete; implementation not started.

Target: Unity 6000.3 / HDRP 17.3 with the camera and HDRP Asset configured for Deferred Lit Shader Mode, plus forward-compatible validation against later Unity 6 HDRP releases.

## Executive decision

Plastic ramp rendering should be implemented as a tiered system rather than as one universal transparent material. Standard HDRP raster refraction cannot correctly show a refractive transparent object through another refractive transparent object because all default-pass refractive materials sample the same pre-refraction color pyramid. Shader Graph cannot change that buffer lifetime or render order. The project's deferred setting does not remove this limitation because HDRP still renders transparent Lit materials through its forward transparent path.

The recommended architecture is:

1. Ship a non-DXR baseline that uses physically calibrated HDRP materials, camera-authored optical surface patches, non-refractive rear patches in the Before Refraction pass, and at most one default-pass refractive patch layer along a camera ray.
2. Offer HDRP Recursive Rendering as an ultra-quality DXR mode using closed, physically thick ramp geometry.
3. Build a custom ordered ping-pong compositor only if comparison against the DXR/path-traced reference demonstrates that the baseline's missing rear-layer refraction is materially visible from the production camera.

This ordering prioritizes the visual characteristics that most strongly identify real molded plastic—Fresnel highlights, edge thickness, molded curvature, reflections, scratches, dust, absorption, and contact with table lighting—while containing the complexity of multilayer refraction.

## Goals

- Make clear and tinted plastic ramps read as molded PMMA, polycarbonate, or another explicitly selected plastic rather than generic alpha-blended glass.
- Keep the playfield, inserts, lamps, ball, wireforms, and rear ramps legible through one or more ramp layers.
- Produce stable results for the mostly fixed production camera without transparency-order popping, missing rear layers, or temporal shimmer.
- Support a scalable non-DXR mode and an opt-in high-end DXR mode.
- Keep custom rendering isolated to `VisualPinball.Unity.Hdrp`; the core engine and collider representation must remain render-pipeline independent.
- Optimize render topology for optical fidelity even when that requires new visual-mesh generation, more renderers or material slots, additional authoring metadata, or replacement meshes for individual tables.
- Preserve imported and packaged tables that do not opt into the new ramp optics system.
- Establish a path-traced or recursive-rendered reference against which raster approximations can be judged.

## Non-goals

- Do not attempt a general-purpose solution for every transparent object in every scene.
- Do not replace HDRP's complete forward lighting loop with a VPE-maintained copy unless every less invasive approach has failed.
- Do not change ramp physics or collider geometry to satisfy rendering requirements.
- Do not require DXR for the standard player configuration.
- Do not adopt weighted blended OIT as a claimed solution for multilayer refraction; it solves order-independent alpha compositing, not ordered spatially displaced transmission.

## Verified starting point

### Project and HDRP behavior

The target `vpe-tron` project uses Unity 6000.3.6f1 and HDRP 17.3.0 with Deferred Lit Shader Mode. All three current VPE HDRP quality assets serialize `supportedLitShaderMode: 2`, which is `DeferredOnly` in HDRP 17.3. In this configuration, opaque Lit table geometry is written to the GBuffer and lit through the deferred path, but HDRP forces transparent Lit materials through forward rendering. The frame is therefore hybrid: the opaque playfield and opaque mechanisms are resolved first through deferred rendering, then the ramp materials are shaded in transparent forward passes.

The same quality assets currently serialize `supportCustomPass: 0`, `supportRayTracing: 0`, and one-sample MSAA. Tier 3 therefore requires Custom Pass support to be enabled in every quality asset that exposes the compositor, while tier 2 requires a ray-tracing-capable quality asset and platform configuration. These are explicit project prerequisites, not shader-only changes.

HDRP renders pre-refraction transparents, creates a color pyramid from the resolved camera color, and then renders default transparents. Standard refractive materials sample that pyramid, which means default-pass refractive objects are not present when other default-pass refractive objects calculate their transmission. Selecting deferred rendering for opaque Lit materials does not introduce a transparent GBuffer, deferred transparent lighting, or multilayer transparent refraction.

Unity's HDRP 17.3 refractive-material page describes the first refraction color pyramid as containing only opaque objects, while the same package's Custom Pass injection-point documentation and rendering execution order state that Transparent PreRefraction objects are rendered before the pre-refraction color pyramid and are included in the buffers available at `BeforeTransparent`. Treat the opaque-only wording as a simplified description and use a Frame Debugger capture in the exact 17.3 project as the implementation gate. The invariant on which this plan relies is unambiguous: default-pass refractive objects are absent from the snapshot sampled by other default-pass refractive objects, so they cannot refract one another correctly. Unity recommends Recursive Rendering when more than one refractive surface per pixel is required.

Unity's bundled Transparency sample provides additional expected behavior that must be reproduced in a local test scene:

- Rasterization supports multiple non-refractive transparent layers.
- Rasterization does not support multiple refractive transparent layers.
- A transparent object rendered Before Refraction can be included in the color pyramid and seen through a later refractive material.
- Recursive Rendering supports multilayer refraction but forces reflected and refracted rays to be smooth.
- Under Recursive Rendering, Thin refraction uses a fixed 5 mm approximation and does not bend recursive rays, while Planar and Sphere modes use the real closed geometry until maximum recursion depth is reached. The 5 mm value is pinned to `REFRACTION_THIN_DISTANCE 0.005` in the installed HDRP 17.3 `Runtime/Material/Lit/Lit.hlsl`, rather than inferred from older documentation that has used a different value.

### Render-geometry freedom

`RampMeshGenerator` and collision-derived topology are explicitly outside the visual design constraints for this work. They are not evidence for the topology, thickness, renderer partitioning, or material layout of the production ramp visuals. Collision output may remain exactly as it is while the visual ramp uses a completely different generated or authored mesh hierarchy.

Visual fidelity is the governing requirement. The render topology, number of renderers, submesh and material-slot layout, physical thickness, fillets, edge bands, end caps, normals, UVs, tangents, and camera-specific patch decomposition may all be changed to support the selected optics path. Procedural generation from ramp spline/profile data is the default production strategy, with authored replacement meshes available as a hero-table override.

### Current material paths

Legacy VPX scene import resolves translucent materials through `VisualPinball.Unity.Hdrp/Runtime/MaterialConverter.cs` and `Assets/Resources/Materials/TableTranslucent.mat`. Despite its name, the current template is configured as an opaque/alpha-tested material with no refraction and depth writing enabled. Automatic import therefore does not provide a physically transparent refractive ramp material.

The packaged VPE material path in `VisualPinball.Unity.Hdrp/Runtime/HdrpMaterialResolver.cs` and `VisualPinball.Engine/VisualPinball.Unity/VisualPinball.Unity/Packaging/VpeMaterial.cs` already supports transparent surfaces, transmission, transmittance color, absorption distance, thickness, IOR, sorting priority, and Thin/Planar/Sphere refraction models. This is the preferred material-description foundation.

The packaged material profile defaults IOR to `1`, which produces no bending relative to air. Ramp profiles must explicitly provide the physical IOR. The current thin translucent template also enables Transparent Depth Prepass, which is unsafe for overlapping ramps because the nearest prepassed transparent surface can prevent rear transparent color from being rendered.

## Rendering principles

### Respect the deferred-to-forward boundary

Treat opaque table rendering and ramp rendering as two connected but distinct stages. The opaque scene benefits from HDRP's deferred material and lighting path, while every transparent ramp still needs a valid forward transparent pass or a Custom Pass shader. A custom ramp shader cannot rely on being decoded and lit from the opaque GBuffer, and changing the opaque Lit Shader Mode does not change transparent ordering. The deferred output is useful as the already-lit background captured into the refraction pyramid, not as a place to store ramp surface data.

### Refraction is ordered transmission, not alpha compositing

Correct multilayer refraction requires each front layer to sample the already shaded and composited radiance behind it at a displaced coordinate. This operation is sequential and order-dependent. A commutative transparency accumulation such as weighted OIT discards the ordered layer data required by refraction.

### Shader Graph cannot solve missing pipeline data

An HDRP Lit Shader Graph can author normals, roughness, IOR, thickness, absorption, and refraction parameters, but it cannot make the output of one default transparent draw appear in a color pyramid captured before that draw. An HD Scene Color node reads the same pipeline-provided scene-color source. This limitation can only be changed by altering render order, introducing intermediate buffers, or tracing rays through actual geometry. In the deferred project, a transparent Lit Shader Graph still compiles and executes a forward transparent pass; it does not become a deferred transparent shader.

### Transparent depth must be treated as a global resource

Transparent Depth Prepass writes into shared camera depth before transparent color. If two stacked ramp renderers both participate, the front ramp can cause the rear ramp's color fragments to fail depth testing. Transparent Depth Prepass must therefore be disabled on any ramp material that can overlap another transparent object. Transparent Depth Postpass may be enabled for depth of field or later post-processing because it runs after transparent color.

### Geometry and lighting dominate the first impression

At the thickness of a typical pinball ramp, background displacement is often subtle compared with grazing-angle Fresnel reflection, visible edge thickness, molded radii, scratches, dust, fingerprints, and elongated reflections from table lights. The first implementation phase must correct those characteristics before funding a complex multilayer compositor.

## Quality tier 1: shippable non-DXR raster baseline

### Layer classification per surface patch

For the production camera, decompose each visual ramp into role-addressable optical surface patches such as near wall, floor, far wall, rims, edge bands, and end caps, then split those patches again wherever their screen-space near-to-far relationship changes. This classification must cover both crossings between ramps and stacks inside one U-channel ramp: a camera ray can traverse the near wall and far wall of the same ramp before reaching the playfield, which is already a multilayer-refraction case.

At every supported camera ray, all optical patches behind the nearest refractive interface use a non-refractive transparent dielectric material in the Before Refraction pass, and at most the nearest optical patch uses stock default-pass refraction. Rear patches contribute alpha, Fresnel reflection, tint, absorption approximation, surface detail, and direct lighting to camera color. Because HDRP builds the refraction pyramid after the Before Refraction pass, these patches become visible to the nearest refractive patch.

This ordering also preserves opaque objects inside a channel: a ball between the far and near walls depth-tests in front of the Before Refraction far-wall patch, then becomes part of the background sampled through the default-pass near-wall patch. The regression scene must verify this exact case with a moving ball.

The nearest patch uses a default transparent HDRP material with Planar refraction. It samples a pyramid containing the opaque playfield, ball, inserts, lamps, and the already composited rear ramp patches. Patch roles and optical layer indices must be emitted with the visual mesh so material assignment and sorting are deterministic rather than inferred later from renderer names or bounds.

Apply the same scoped ordering policy to non-ramp transparent objects visible behind a refractive ramp patch, including playfield plastics, post sleeves, light covers, and flasher domes. If an object is wholly behind the nearest refractive patch, render it Before Refraction; if it crosses an order boundary, split it or explicitly enroll it in the ordered compositor. The authoring validator must report overlapping stock default-pass transparents that would otherwise disappear from the ramp's refraction. This is not a general transparency rewrite: only objects participating in a ramp optical stack require metadata or reassignment.

Do not attempt to make the rear Before Refraction material sample and distort camera color directly. At that stage there is no refraction pyramid, and reading from the target being written creates an unsafe read/write dependency. Rear-layer distortion requires an explicit temporary-buffer Custom Pass and belongs only in the tier 3 investigation.

### Refraction model

Use Planar refraction for the raster baseline because a molded ramp sheet is locally close to a parallel slab and Planar allows physical thickness to be authored. Thin refraction fixes thickness at 5 mm and is primarily useful as a simplified/path-tracing-oriented model. A prototype may include Thin as a comparison, but it should not be the default when the physical ramp is thinner or thicker than 5 mm.

The raster generator must enforce one camera-facing optical entry surface per physical sheet, with back-face culling and physical thickness supplied by the Planar material. HDRP already approximates the exit surface from that thickness, so the generated raster mesh must not contain a paired back surface that applies transmission twice. A validator should treat duplicate optical interfaces as an error rather than relying on material discipline.

### Sorting and segmentation

Use explicit HDRP material Sorting Priority or `MeshRenderer.rendererPriority` after splitting geometry into optical patches. Higher priority draws later and therefore appears closer.

Object-level sorting cannot solve a mesh whose front/back relationship changes within its screen-space bounds. Such a patch must be split into stable optical segments. An editor visualization should display patch role, refractive/Before Refraction assignment, and optical layer, and should warn when patch bounds overlap with an ambiguous order.

Generate each independently ordered optical layer as a separate renderer with stable bounds and explicit priority. Do not rely on submesh order inside one renderer, inherited floor/wall renderer grouping, offline triangle presorting, paired front/back material passes, or transparent depth prepass as substitutes for the required patch decomposition. A custom direct-draw implementation may batch patches internally only if it preserves identical explicit layer selection and order.

Own sorting priorities through one table-level allocator shared by ramp patches and opted-in non-ramp transparent participants. Convert the authored optical order into HDRP material/renderer priorities centrally, detect cycles and range exhaustion, and forbid unrelated assets from assigning colliding ad hoc priorities inside the reserved ramp-optics range.

### Raster mesh representation

Make a physically thick, closed solid ramp the canonical visual source. Generate it from the ramp spline and a molded channel profile with measured sheet thickness, filleted inner and outer transitions, rounded rims, outward-facing normals, and capped ends. The recursive tier uses this solid directly, while the raster and custom-compositor tiers derive their camera-facing single-entry surface patches from the same source topology.

The derived raster topology must provide role-addressable near-wall, floor, far-wall, rim, edge-band, and end-cap patches; continuous positions and normals across real molded floor/wall fillets; matching normals across artificial segment cuts; spline-continuous UVs and tangents for scratches, roughness, dirt, and decals; stable per-patch renderer bounds for sorting; and generated patch-to-layer metadata. Rear Before Refraction patches and nearest refractive patches use this identical topology and differ only in material/pass assignment.

Rims, edge bands, and end caps should be separate patches with visible physical thickness and appropriate Fresnel and absorption, but they must always use a non-refractive material so they cannot introduce a second stock-refraction layer. Render them Before Refraction when they are behind the nearest refractive patch and as default non-refractive transparency when they are in front; split any patch whose role changes across the supported camera envelope. The raster extractor should select the appropriate camera-facing entry faces from the solid master for the supported camera envelope and validate that the extracted silhouette remains identical to the recursive solid.

### Topology contract by quality path

| Path | Render geometry | Partitioning and thickness |
| --- | --- | --- |
| Tier 1 nearest stock refraction | First-hit single-entry patches extracted from the canonical solid | One renderer per stable optical layer; Planar material supplies measured physical thickness; no raster back surface |
| Tier 1 rear Before Refraction | The same extracted patches and shading data | One renderer per stable optical layer; non-refractive material; optional generated thickness/curvature data approximates absorption without adding another interface |
| Tier 2 Recursive Rendering | Complete closed canonical solid master | Real inner and outer surfaces, measured geometric thickness, fillets, rims, and caps; participation is atomic per crossing/table quality mode |
| Tier 3 ordered compositor | The same first-hit patches used by tier 1 | One registered renderer per custom optical layer; custom shader receives thickness and optical metadata derived from the solid master |

### Raster material targets

Start from measured material families rather than a generic glass preset:

| Material | IOR starting point | Normal-incidence Fresnel F0 | Notes |
| --- | ---: | ---: | --- |
| PMMA/acrylic | 1.49 | approximately 3.9% | High clarity; verify the actual table part |
| Polycarbonate | 1.586 | approximately 5.1% | Impact-resistant and common for molded transparent parts |
| PETG/copolyester | Use grade data | Derive from IOR | Do not silently reuse PMMA values |

Use an initial polished smoothness range of approximately 0.92–0.98, then introduce spatial variation through a roughness map. These are starting values, not acceptance values; final settings must be calibrated against photographs or a physical ramp under comparable lighting.

Use transmittance color and absorption distance for tint and thickness-dependent darkening. Alpha remains a rendering control and should not be treated as a physical extinction coefficient. Dirt, fingerprints, mold flow, and scuffs should primarily modify roughness and normals, with opacity used only where actual deposits block light.

Place reflection probes so the ramp sees the cabinet, playfield, and table lighting from the production camera. Transparent SSR may supplement probes, but it is screen-space limited and does not solve refractive stacking.

### Raster shadows

Validate three policies because standard raster transparent shadows may appear opaque or otherwise inconsistent with the material:

1. Disable ramp shadow casting when the opaque result is more distracting than the missing shadow.
2. Use a simplified shadow-only proxy that produces a soft low-opacity shadow.
3. Use a custom projected or baked attenuation mask for important under-ramp lighting arrangements.

The selected policy must be evaluated with inserts and lamps beneath a clear ramp, not only under neutral studio lighting.

## Quality tier 2: HDRP Recursive Rendering on DXR hardware

### Purpose

Recursive Rendering is the high-end path for physically ordered intersections through multiple refractive surfaces. It should be exposed as an optional quality feature with hardware capability checks and a safe fallback to tier 1. It is an alternate ray-traced rendering path for the selected ramp objects and is not made more or less correct by the camera's deferred opaque mode; compatibility and cost must still be verified with the actual deferred camera, post-processing, and upscaler configuration.

Treat recursive participation as atomic for each optical crossing, and preferably for the complete table quality mode. Do not mix a recursively rendered ramp with a raster-refractive ramp in the same crossing unless a targeted frame capture proves the composition order: recursive objects use an alternate pass, and the stock raster refraction pyramid must not be assumed to contain a recursive ramp rendered behind it. The authoring validator should flag mixed recursive/raster crossings.

### Solid mesh representation

Use the canonical solid master as the Recursive Rendering mesh. It must be:

- Closed and watertight.
- Physically thick using a measured thickness or an authorable prototype default.
- Correctly oriented with outward-facing normals.
- Free from coplanar duplicate faces and accidental internal surfaces.
- Rounded or beveled at molded edges.
- Separated from adjacent transparent solids by enough distance to avoid ray-bias self-intersection.
- Generated and consumed independently from collision geometry so optical topology never inherits collider constraints.

The material must use the double-sided configuration expected by HDRP recursive traversal. Planar or Sphere may be selected according to the asset, but Planar is the semantic default for a ramp sheet. Do not use Thin as the recursive quality material because HDRP's Thin recursive path does not bend the ray through the actual geometry.

### Recursive settings

Benchmark at least maximum depths 4 and 6 on the target GPU set. Maximum depth should be selected from observed crossings, not assumed globally; each additional entry/exit interface consumes traversal depth and can expand the reflection/refraction tree.

Set maximum ray length relative to the complete table scale so rays do not incorrectly terminate in the sky. Validate ray bias at ramp intersections, seams, and tiny gaps.

### Recursive limitations and fallbacks

- Reflected and refracted rays are forced smooth, so rough or blurred transmission from scuffs is not represented accurately.
- Recursive Rendering uses an independent render pass and cannot combine other ray-traced effects on the same ramp objects.
- Recursive Rendering must be enabled per complete crossing or quality mode; mixed recursive/raster refractive crossings are unsupported until explicitly validated.
- Test every non-ramp transparent participant visible behind a recursive ramp. If recursive rays do not reproduce that object correctly, enroll it in the recursive quality-mode set or document and implement a raster fallback; do not assume stock raster composition will match recursive traversal.
- Decals and scratch overlays require explicit validation and may need a separate raster overlay.
- Shadowing through recursive ramps may fall back to raster behavior; a shadow-only proxy or disabled shadow casting may still be required.
- Cost grows rapidly with maximum depth and screen coverage, so the mode must be profiled at the production resolution and with dynamic resolution/upscaling settings enabled.
- Player capability checks must verify an active DirectX 12 graphics device and HDRP ray-tracing support in the selected quality asset, not only the GPU model.

## Quality tier 3: ordered custom-pass compositor

### Go/no-go condition

Do not start tier 3 merely because stock HDRP is theoretically incomplete. Start it only when the tier 1 result is compared against tier 2 and a path-traced reference from the production camera, and the absence of rear-layer refractive displacement is judged materially visible during play.

### Recommended algorithm

Use a manually ordered screen-space ping-pong compositor rather than weighted OIT or general depth peeling. The fixed camera allows the canonical raster patches to be partitioned into a small number of stable optical layers offline.

The `BeforeTransparent` injection point runs after the opaque scene has been resolved to camera color, so the compositor can begin from the lit output of the deferred opaque path. The Custom Pass must nevertheless render optical patch layers with a forward-compatible shader. The stock refraction color pyramid remains a fixed snapshot for that part of the frame; it is not regenerated after each custom patch draw, which is why the compositor still needs explicit ping-pong color buffers.

At `BeforeTransparent`, perform the following sequence for each optical layer from farthest to nearest:

1. Copy the currently composited camera color into a dynamically scaled HDR RTHandle.
2. Bind that copy globally as `_RampBehindColor` or the eventual equivalent.
3. Render only the current optical patch layer while sampling the previous color at a refracted screen coordinate.
4. Repeat so the next layer sees every layer behind it.
5. Leave unrelated stock transparents to render afterward unless they physically lie between optical patch layers, in which case they need explicit participation in the ordering system.

Use two ping-pong color RTHandles rather than reading and writing the same render target. Allocate through HDRP's RTHandle system and apply `_RTHandleScale` so dynamic resolution and camera scaling remain correct. Smooth clear plastic can begin with mip level zero; add a per-layer mip chain only if rough transmission is proven important.

### Shader composition investigation

The first prototype should use an HDRP Lit Shader Graph or generated Lit shader where the custom sampled background represents transmitted radiance while HDRP continues to provide direct/specular surface lighting. A plausible starting point is black or energy-neutral diffuse response, custom transmission through emissive/transmission composition, preserve-specular lighting, Fresnel weighting, and alpha chosen solely to produce the intended final blend.

This must be validated mathematically and visually. Shader Graph cannot literally replace HDRP's internal refraction color source, so the graph must avoid accidentally applying both custom transmission and stock refraction, double alpha attenuation, or unwanted diffuse lighting.

HDRP camera-color RTHandles are pre-exposed, while HDRP 17.3 helpers such as Shader Graph scene-color sampling and `CustomPassSampleCameraColor` remove that pre-exposure. The prototype must document the color space returned by every sampling path and apply `GetCurrentExposureMultiplier` or `GetInverseCurrentExposureMultiplier` exactly once where required by the chosen output path. Test both fixed and adapting exposure; a raw RTHandle copy, a helper-based sample, and an emissive re-injection cannot be assumed to use the same exposure convention.

If an energy-correct composition cannot be achieved with a Lit graph, use a handwritten Custom Pass shader. That escalation raises maintenance risk because transparent surface lighting still requires the HDRP forward light loop even though the opaque camera path is deferred, and reproducing or including that internal code can break across HDRP releases. Do not copy the entire HDRP light loop into VPE until a narrower integration route has been exhausted.

### Proposed runtime integration

Require Custom Pass support in the active DeferredOnly HDRP quality asset before enabling tier 3. Keep the compositor unavailable with an explicit diagnostic when the feature flag is off; do not silently switch the camera or asset to Forward or Both.

Add an opt-in `RampOpticsComponent` or similarly named component in `VisualPinball.Unity` with only pipeline-independent metadata:

- Optical segment or layer index.
- Surface-patch role such as near wall, floor, far wall, rim, edge band, or end cap.
- Physical thickness in millimeters.
- Material family or explicit IOR.
- Transmittance color and absorption distance.
- Raster representation selection.
- Solid recursive representation reference or generation flag.
- Participation in the custom layered compositor.
- Optional shadow policy.

Add `RampRefractionPass : CustomPass` in `VisualPinball.Unity.Hdrp`. It owns RTHandles, renderer selection, ordered layer execution, global texture bindings, debug output, and profiling markers.

Extend `HdrpMaterialResolver` with dedicated ramp templates or an explicit ramp-optics material variant. Do not infer every transparent plastic as a ramp; inserts, posts, covers, lenses, and playfield plastics have different geometry and rendering requirements.

Provide minimal opt-in optical-layer metadata for non-ramp transparent renderers that overlap a ramp stack so authoring tools can assign Before Refraction, default non-refractive, or custom-compositor participation without converting those objects into ramps.

Keep custom-pass ramp renderers out of the stock transparent draw to prevent duplicate rendering. Use a dedicated Unity rendering layer, layer mask, or renderer registry rather than attempting to filter directly on a MonoBehaviour field during renderer-list construction.

The visual generator must register each compositor patch renderer together with its generated optical layer. The compositor consumes that generated patch list directly and must not reconstruct ordering from scene hierarchy, object names, triangle order, or renderer bounds.

### Custom-pass compatibility requirements

- Static ramps must remain stable under TAA from the fixed camera.
- Validation must use the production deferred camera and its actual temporal anti-aliasing or upscaler path; do not assume an MSAA-based solution that would require changing the camera to forward rendering.
- Deferred Lit Shader Mode does not support MSAA, which simplifies the compositor because it does not need multisampled color/depth resolve handling; MSAA-based transparency techniques are out of scope for this camera configuration.
- A moving opaque ball visible through multiple layers must not acquire a one-frame lag, ghost trail, or mismatch with motion blur.
- RTHandles must resize correctly under dynamic resolution, window resize, render scale, and upscalers.
- Scene-view and reflection-camera behavior must either be supported or explicitly fall back to tier 1.
- Other transparent effects must have a documented ordering policy relative to the ramp compositor.
- A Windows player build using the DeferredOnly HDRP Asset must retain every forward transparent and Custom Pass shader variant used by the ramps. If build stripping removes a required variant, add an explicit Shader Variant Collection or narrowly scoped stripper exemption; switching the whole asset to `Both` is a last-resort tradeoff because it increases variants, build size, and memory.
- The pass must expose profiling samples for copies, ramp draws, and optional mip generation.
- HDRP 17.3 must be the implementation baseline, with a small compatibility test scene retained for validation after package upgrades.

## Alternatives not recommended initially

### Weighted or stochastic OIT

Weighted OIT can remove alpha sorting dependence but collapses fragment data into commutative accumulation and revealage buffers. It cannot sequentially refract the shaded result of deeper layers, so it does not solve the target problem. Accumulating distortion vectors and applying one final background offset is another approximation, not a correctness tier.

### General depth peeling or K-buffer/A-buffer

A fixed-K peel or per-pixel fragment list can recover layer order, but refracted sampling is spatial: a front pixel may sample a neighboring pixel in the next layer. The implementation therefore needs full-screen layer images or a more complex ray-aware resolve, not merely a same-pixel fragment list. It also creates substantial color/depth/attribute memory, multiple geometry passes, difficult MSAA/TAA integration, and pressure to reproduce HDRP lighting in custom shaders.

This may become relevant if the production camera becomes freely movable and authored segment ordering is no longer viable, but it is excessive for the current fixed-camera use case.

### Per-ramp cameras or captures

A camera or planar-capture approach preserves stock material lighting but re-renders much of the scene for every optical layer. Dynamic balls and lamps require fresh captures every frame, and nested captures must include prior ramp layers. This scales poorly across a full playfield and should be limited to isolated hero shots, if used at all.

## Implementation phases

### Phase 0: reference scene and measurement harness

Tasks:

- Create a dedicated ramp-optics test scene in the HDRP LookDev project using the worst real table crossing available.
- Lock the HDRP Asset and test camera to Deferred Lit Shader Mode, record the exact Frame Settings overrides, and make any accidental switch to Forward mode visible in the test setup.
- Record and validate the camera, reflection-camera, and Scene View Frame Settings that gate Refraction, Transparent SSR, Custom Passes, Transparent Depth Prepass/Postpass, and Depth Prepass within Deferred.
- Include a single U-channel whose near and far walls overlap, a ball inside that channel, two and three overlapping ramps, a crossing whose near/far order changes across the screen, a moving chrome ball below and between layers, high-frequency playfield art, inserts, point/spot/area lights, dark and bright backgrounds, and a screen-edge case.
- Import or reproduce Unity's HDRP Transparency sample stacking cases as a control.
- Capture the current imported material, calibrated raster material, Recursive Rendering, and Path Tracing from identical cameras.
- Add GPU profiling markers or a repeatable capture procedure using Unity Profiler, RenderDoc, or the platform GPU profiler.
- Capture a Frame Debugger trace proving this exact sequence in HDRP 17.3: opaque GBuffer and deferred lighting, Transparent PreRefraction, pre-refraction color-pyramid generation, the `BeforeTransparent` Custom Pass point, and default forward transparents.

Exit criteria:

- Reproducible screenshots and frame captures exist for every quality path.
- The stock raster limitation and transparent-depth-prepass failure can be deliberately reproduced.
- The reference capture proves that opaque table objects use deferred rendering while all transparent ramp materials use forward transparent passes.
- The production camera and permitted camera movement envelope are documented.

### Phase 1: physically calibrated raster material

Tasks:

- Hand-author a representative canonical U-channel section that already conforms to the Phase 2 solid-master and patch-role contract; use it to calibrate materials without blocking on the generator, and do not calibrate on collision-derived or legacy visual sheets that the implementation will discard.
- Add a dedicated Planar ramp material/template with explicit IOR, thickness, transmittance color, absorption distance, smoothness, normal/roughness maps, preserve-specular lighting, transparent SSR configuration, and depth prepass disabled.
- Verify that every ramp template contains the required HDRP forward transparent pass and renders correctly when the HDRP Asset supports Deferred rather than Both or Forward Lit Shader Mode.
- Verify the same material and pass behavior in a Windows player build using the DeferredOnly quality asset; Editor rendering alone does not validate shader-variant stripping.
- Add a matching non-refractive Before Refraction material for rear optical layers.
- Ensure the packaged material schema carries physical ramp values and does not fall back to IOR 1.
- Define PMMA and polycarbonate presets as authoring conveniences while preserving explicit overrides.
- Validate reflection-probe placement and screen-edge fallback.
- Establish a shadow policy for clear ramps.

Exit criteria:

- A single canonical U-channel ramp, including its floor/wall fillets, rims, end caps, and near-wall/far-wall overlap, looks credible against the physical/path-traced reference under table lighting.
- The ball, playfield art, and inserts remain legible through the ramp.
- No depth-prepass culling or sorting pop occurs in the test camera envelope.

### Phase 2: canonical visual geometry and patch ordering

Tasks:

- Implement a dedicated visual-mesh generator from ramp spline/profile data; collision topology is neither an input constraint nor a fallback render representation.
- Generate the physically thick closed solid master with measured thickness, inner and outer fillets, rounded rims, outward normals, and capped ends.
- Derive first-hit, camera-facing single-entry raster patches from the solid master for the supported camera envelope and emit near-wall, floor, far-wall, rim, edge-band, and end-cap roles plus optical layer metadata.
- Split patches at intra-ramp and inter-ramp order changes, assign explicit priorities, and emit each independently ordered layer as a separate renderer.
- Preserve positions, normals, tangents, and UV continuity across artificial segment cuts so the refractive displacement and highlights do not reveal the split.
- Add an editor visualization for patch role, pass assignment, and optical layer.
- Add validation errors for paired raster entry/exit surfaces and warnings for ambiguous patch order, mixed recursive/raster crossings, or transparent depth prepass on overlapping patches.
- Add validation warnings for non-ramp stock default-pass transparent objects behind a refractive patch and for generated sorting priorities that collide with or cannot correctly interleave with other table transparents.
- Keep collision meshes on their existing independent path and add only a regression guard that visual-mesh work does not alter collision output unintentionally.

Exit criteria:

- The near wall and far wall of a single U-channel, plus all multi-ramp test crossings, have deterministic ordering from the production camera and allowed offsets.
- Every rear Before Refraction patch appears through the nearest refractive patch, including the far wall of a single ramp.
- Raster patches and the recursive solid share positions, silhouette, molded radii, and shading normals by construction.
- Artificial segment cuts produce no visible normal, reflection, refraction-displacement, UV, or silhouette seam.
- No hole, missing face, inverted entry surface, or silhouette change appears anywhere inside the documented production-camera envelope.
- Edges and grazing highlights read as molded plastic rather than infinitely thin sheets.

### Phase 3: DXR Recursive Rendering tier

Tasks:

- Use the Phase 2 canonical watertight solid masters with measured thickness; authored hero replacements must provide the same patch-role and layer metadata contract.
- Create a recursive Planar ramp material and quality-mode selection.
- Test maximum depth 4 and 6, maximum ray length, bias, double-sided state, scratches/decals, and shadow behavior.
- Validate an all-recursive crossing and confirm that the authoring tools reject or warn about mixed recursive/raster crossings.
- Validate transparent playfield plastics, post sleeves, light covers, and flasher domes behind recursive ramps, and define their recursive participation or fallback policy from observed behavior.
- Add capability detection and automatic fallback to tier 1.
- Require DirectX 12 plus HDRP ray-tracing support in the active quality asset before selecting tier 2.
- Profile representative tables on the supported DXR GPU range.

Exit criteria:

- Two- and three-ramp stacks transmit and refract in the correct physical order.
- No persistent self-intersection, black-medium, or sky-fallback artifacts occur.
- Performance and known visual limitations are documented per quality setting.

### Phase 4: custom compositor go/no-go and prototype

Tasks:

- Diff tier 1 against recursive and path-traced references from the production camera.
- Hold an explicit visual review focused on missing rear-layer displacement, crossing silhouettes, the moving ball, and under-ramp lamps.
- Create a DeferredOnly experimental quality asset with Custom Pass support enabled, leaving the production quality assets unchanged until the prototype passes its go/no-go review.
- Feed the prototype directly from the generated patch-renderer and optical-layer list, and verify exact per-layer renderer selection before evaluating image quality.
- If approved, implement a two-layer ping-pong Custom Pass prototype with no rough-transmission mip chain.
- Validate custom sampled transmission plus HDRP surface reflection before expanding to additional layers.
- Measure copy bandwidth, draw cost, temporal stability, and upgrade sensitivity.

Exit criteria:

- Either the team records that tier 1 is visually sufficient and closes tier 3, or the custom prototype demonstrates a material improvement at an acceptable measured cost.
- No implementation proceeds to general depth peeling or custom HDRP light-loop ownership without a second architecture review.

### Phase 5: integration, migration, and documentation

Tasks:

- Integrate the selected material templates into legacy import and packaged material resolution without changing unrelated transparent assets.
- Add authoring UI, tooltips, presets, layer visualization, and validation messages.
- Add automated material/resolver tests for IOR, refraction model, depth-prepass state, render pass, and sorting priority.
- Add mesh tests for solid closure, normal orientation, thickness, raster-face extraction, patch roles, layer metadata, continuity across cuts, silhouette parity, and unchanged collider output.
- Document conversion of an existing table and fallback behavior on non-DXR platforms.
- Retain the reference scene as a render regression fixture.

Exit criteria:

- Existing tables render unchanged until ramp optics is explicitly enabled or migrated.
- A migrated table can select standard, recursive, and fallback modes predictably.
- The reference scene passes visual and performance checks after an HDRP package update.

## Test matrix

| Dimension | Required cases |
| --- | --- |
| Layering | One flat ramp; one U-channel with near-wall/far-wall overlap; ball inside a U-channel; two parallel ramps; three ramps; crossing order change; nearly coplanar layers |
| Geometry | Collision-derived proxy as a control only; canonical single-entry raster patches; closed solid master; inner/outer fillets; rim and edge bands; end cap; artificial patch cut |
| Content behind ramps | Playfield art; inserts; chrome ball; wireform; another ramp; transparent playfield plastic; post sleeve; light cover or flasher dome; emissive display/particle if applicable |
| Lighting | Bright studio; dark table; under-ramp insert; point light; spotlight; area light; reflection-probe edge |
| Camera | Production camera; allowed pitch/yaw offsets; screen edge; scene view fallback |
| Rendering | Deferred camera and opaque GBuffer control; Editor and Windows DeferredOnly player build; existing import; tier 1 Planar forward transparency; tier 1 stratified forward transparency; recursive depth 4; recursive depth 6; path trace; custom two-layer prototype if approved |
| Temporal | Static frame; moving ball; flashing inserts; camera adjustment; fixed and adapting exposure; TAA; motion blur; DLSS/other upscaler |
| Resolution | 1080p; 1440p; 4K; dynamic-resolution changes; window resize |
| Shadows | Cast off; raster cast; shadow proxy; available ray-traced shadow configurations |

## Acceptance criteria

### Visual

- Every rear optical patch intended to remain visible is present through front layers; no far wall, rear ramp, or other layer disappears because of transparent depth.
- Every opted-in non-ramp transparent object behind a refractive patch remains visible in the correct order in raster and recursive quality modes, or uses a documented fallback.
- The ball remains visible through all supported layer arrangements, including between the near and far walls of one U-channel, without a frame-lag artifact.
- Sorting remains stable throughout the supported camera envelope.
- Refraction does not produce objectionable screen-edge seams, doubled transmission, wall/floor discontinuities, or displacement/highlight seams at artificial patch cuts.
- Grazing reflection, edge thickness, molded curvature, roughness variation, and wear remain visible under both bright and dark table lighting.
- PMMA and polycarbonate presets are distinguishable through their calibrated IOR and transmission rather than arbitrary alpha differences.
- The raster result is accepted against recursive/path-traced references from the production camera.

### Performance

- Record GPU frame time, transparent-pass time, overdraw, RTHandle bandwidth, VRAM use, and recursive traversal cost on each target performance class.
- On a complete production table, record generated patch renderer count, CPU render-submission cost, SRP Batcher behavior, draw calls, memory, overdraw, and the global sorting-priority allocation alongside GPU time; a small crossing fixture is not a sufficient performance gate.
- Attribute opaque deferred cost separately from forward transparent ramp cost so the ramp feature is not credited or blamed for unrelated GBuffer and deferred-lighting work.
- Establish the acceptable budget from the existing player frame budget before setting a numeric gate; do not assume a sub-millisecond custom-pass cost without measurement.
- Standard mode must not allocate per-frame render textures or generate per-layer mip chains unless tier 3 is explicitly enabled.
- DXR mode must degrade cleanly to tier 1 when unsupported or over budget.

### Maintainability

- Pipeline-specific code remains in `VisualPinball.Unity.Hdrp`.
- Ramp optics metadata remains portable and does not expose HDRP implementation details in core engine data.
- No duplicated HDRP light loop is accepted without documented upgrade tests and ownership.
- The implementation must not require changing the production camera from Deferred to Forward Lit Shader Mode.
- Custom rendering is opt-in and removable by disabling one component or quality feature.

## Risks and mitigations

| Risk | Impact | Mitigation |
| --- | --- | --- |
| Rear patch disappears through a front patch | Broken intra-ramp or inter-ramp multilayer readability | Disable transparent depth prepass; classify per surface patch; reproduce the near-wall/far-wall and crossing failures in the regression scene |
| Object sorting changes within a channel or crossing | Popping or inverted layers | Split patches at order changes; assign explicit priorities; visualize roles and layers |
| Raster patches and recursive solid diverge | Quality modes show different silhouettes or shading | Extract raster faces from the canonical solid master so correspondence exists by construction; retain a regression check |
| Artificial patch boundary is visible | Refraction, highlight, or texture seam | Share boundary positions, normals, tangents, and UV parameterization across cuts; add image regression views at every split |
| Non-ramp transparent behind a refractive patch disappears | Missing playfield plastic, sleeve, cover, or dome | Detect overlap from the production camera; assign Before Refraction, split at order changes, or enroll in the compositor/recursive set |
| Patch renderer count or priority allocation does not scale | CPU submission cost or incorrect interleaving on complete tables | Profile a production table; define a global transparent-priority allocator; batch only where exact patch order remains preserved |
| Refraction looks like glass rather than plastic | Incorrect material identity | Use physical IOR, absorption, measured thickness, edge geometry, and plastic wear maps |
| Custom graph double-counts transmission | Overbright or overly dark ramps | Validate composition equation and compare with recursive/path-traced reference |
| Custom transmission mishandles HDRP pre-exposure | Brightness pumps or drifts under auto-exposure | Document every sample/output convention; apply HDRP exposure helpers exactly once; test adapting exposure |
| Custom pass breaks after HDRP upgrade | Maintenance burden | Use public Custom Pass and RTHandle APIs; retain the regression scene; avoid internal light-loop copies |
| DeferredOnly player strips a required forward/custom-pass variant | Ramps disappear or use an error shader only in builds | Test a player build; retain variants with a collection or narrowly scoped stripper rule; avoid switching to Both unless necessary |
| Recursive mode is too expensive | Missed frame budget | Capability gating, per-table recursion-depth tuning, screen-coverage profiling, tier 1 fallback |
| Raster and recursive ramps are mixed in one crossing | Missing rear layer or undefined composition | Make quality selection atomic per crossing/table and warn during authoring |
| Recursive mode loses rough transmission or shadows | Visual mismatch | Raster detail overlay where safe; documented shadow proxy/disable policy |
| Camera becomes freely movable | Authored ordering invalid | Reassess depth peeling/K-buffer only if product requirements actually change |

## Open decisions

- Which physical material families must be supported as named presets, and which real ramp samples can be measured or photographed?
- What is the production camera movement envelope, including cabinet/head-tracking variants?
- Should optical segmentation be authored manually, generated from a reference camera, or generated and then manually corrected?
- Which canonical channel profiles, rim shapes, sheet thicknesses, and fillet parameters should be procedural presets, and which require table-specific or authored overrides?
- Which non-DXR shadow policy gives the best result under real table lighting?
- Is the standard mode required to support scene view and freely orbiting editor cameras with correct ordering, or may it fall back to simpler transparency outside the production camera?
- What target GPU classes and frame-time budgets define the standard and ultra quality tiers?

## Primary references

- [Unity HDRP 17.3: Create a refractive material](https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@17.3/manual/create-a-refractive-material.html)
- [Unity HDRP 17.3: Refraction models](https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@17.3/manual/refraction-models.html)
- [Unity HDRP 17.3: Recursive Rendering](https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@17.3/manual/Ray-Tracing-Recursive-Rendering.html)
- [Unity HDRP 17.3: Recursive Rendering reference](https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@17.3/manual/reference-recursive-rendering.html)
- [Unity HDRP 17.3: Rendering execution order](https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@17.3/manual/rendering-execution-order.html)
- [Unity HDRP 17.3: Forward and Deferred rendering](https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@17.3/manual/Forward-And-Deferred-Rendering.html)
- [Unity HDRP 17.3: Custom Pass injection points](https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@17.3/manual/Custom-Pass-Injection-Points.html)
- [Unity HDRP 17.3: Custom Pass GameObject and buffer workflow](https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@17.3/manual/custom-pass-create-gameobject.html)
- [Unity HDRP 17.3: Renderer and material priority](https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@17.3/manual/Renderer-And-Material-Priority.html)
- [Covestro Makrolon 2656 polycarbonate optical data](https://solutions.covestro.com/en/products/makrolon/makrolon-2656_000000000057534269)
- [PLEXIGLAS PMMA technical data](https://www.plexiglas.de/files/plexiglas-content/pdf/technische-informationen/211-1-PLEXIGLAS-GS-XT-EN.pdf)

## Relevant VPE files

- `VisualPinball.Unity.Hdrp/Runtime/MaterialConverter.cs`
- `VisualPinball.Unity.Hdrp/Runtime/HdrpMaterialResolver.cs`
- `VisualPinball.Unity.Hdrp/Runtime/VpeMaterialResolverBootstrap.cs`
- `VisualPinball.Unity.Hdrp/Assets/Resources/Materials/TableTranslucent.mat`
- `VisualPinball.Unity.Hdrp/Assets/Resources/Materials/VpeLitTransparentTemplate.mat`
- `VisualPinball.Unity.Hdrp/Assets/Resources/Materials/VpeLitTranslucentThinTemplate.mat`
- `VisualPinball.Unity.Hdrp/Assets/Resources/Materials/VpeLitTranslucentPlanarTemplate.mat`
- `VisualPinball.Engine/VisualPinball.Unity/VisualPinball.Unity/VPT/Ramp/RampComponent.cs`
- `VisualPinball.Engine/VisualPinball.Unity/VisualPinball.Unity/Packaging/VpeMaterial.cs`
