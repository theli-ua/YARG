# YARG Rendering Pipeline

This document describes the rendering architecture of YARG. Keep it updated when rendering changes are made.

## Overview

YARG uses two primary rendering paths that share a common highway composition stage:

1. **Venue path** — 3D venue renders to backbuffer, post-processing applied, highways composited on top
2. **No-venue path** — background texture or held venue frame rendered, highways composited on top

Both paths use the same `HighwayCompositePass` to alpha-blend highways into the final image.

## Venue Rendering Path

### Frame Sequence

```
1. Venue camera → backbuffer (targetTexture = null)
2. URP built-in post-processing (Bloom, FilmGrain, AA, tonemapping, etc.)
3. YargVenuePPPass (optional) — custom effects: posterize, scanlines, trails
4. MirrorEffectPass (optional) — mirror UV distortion
5. VenueFrameCopyPass — backbuffer → _previousFrameTexture
6. HighwayCompositePass — alpha-blend highways → backbuffer
```

### Venue Camera

- **`VenueCameraRenderer`** — attached to the venue's main camera
- Renders directly to backbuffer (`targetTexture = null`)
- Camera is disabled by default; explicitly enabled only when a venue frame is due
- `allowDynamicResolution = true` when `renderScale != 1.0` (URP DRS)
- Per-camera URP antialiasing setting (see [Anti-Aliasing](#anti-aliasing))

### FPS Capping (Accumulator-Based)

Venue rendering is capped to a target FPS (e.g., 30 FPS on a 60 FPS game) to save GPU:

- **Accumulator** adds `deltaTime` each game frame
- When accumulator >= `1 / targetFPS`, venue renders and accumulator subtracts the frame interval
- This averages to exact target FPS regardless of game frame rate
- `SlowFPSComponent` volume can further reduce venue FPS at runtime
- `_effectiveFps = min(FPS cap, SlowFPS divisor result)`

### Skipped Venue Frames

When the venue skips rendering:

- Venue camera is disabled
- `NoVenueCamera` is enabled instead
- `NoVenueBackgroundPass` blits `_previousFrameTexture` (last rendered venue frame) to backbuffer
- `HighwayCompositePass` composites highways on top
- Highways render every game frame at full FPS regardless of venue FPS

### URP Built-in Post-Processing

Applied automatically by URP after venue camera rendering. Configured via the URP Asset and Volume Profile:

- **Bloom** — controlled via `GraphicsManager.BloomEnabled`
- **Film Grain** — controlled via `GraphicsManager.FilmGrainEnabled`
- **Antialiasing** — FXAA, SMAA, or TAA (see [Anti-Aliasing](#anti-aliasing))
- **Tonemapping** — configured in URP Asset (ACES)
- **Color Adjustments / Color Curves** — standard URP volume components

### YARG Custom Post-Processing

Two separate `ScriptableRenderPass` instances, enqueued at `AfterRenderingPostProcessing`:

#### YargVenuePPPass

Single fullscreen pass for custom effects. Uses `VenuePostProcessingFrameData` ping-pong texture chain. Effects gated by uniform `if` branches (material properties — free on modern GPUs when disabled):

- **Posterize** — quantizes color to N steps (`_YargPosterizeSteps > 0`)
- **Scanlines** — horizontal line overlay with easing (`_YargScanlineSize > 0`)
- **Trails** — blends with `_previousFrameTexture` using luminance mask (`_YargTrailLength > 0`)

Params set via `VolumeComponent`s (`PosterizeComponent`, `ScanlineComponent`, `TrailsComponent`) read in `VenueCameraRenderer.LateUpdate()`.

#### MirrorEffectPass

Separate pass for mirror UV distortion. Runs after YargVenuePP (`AfterRenderingPostProcessing + 1`). Requires texture sampling (not framebuffer fetch) for arbitrary UV transformation:

- Modes: `YARG_MIRROR_LEFT`, `YARG_MIRROR_RIGHT`, `YARG_MIRROR_CLOCK_CCW`, `YARG_MIRROR_NONE`
- Controlled via `MirrorComponent` volume
- Uses `multi_compile_local` keywords for mode selection

#### Ping-Pong Frame Data

`VenuePostProcessingFrameData` manages source/destination texture handles between passes:

- First pass: source = `activeColorTexture` (URP PP output), dest = `_venuePPTexture`
- Each pass swaps source ↔ dest via `Swap()`
- `cameraColor` set to dest after each pass to chain correctly in RenderGraph

### VenueFrameCopyPass

Copies the final venue + post-processed frame to `_previousFrameTexture` (screen-resolution HDR RT with dynamic scale). Used by:

- No-venue camera to display the last venue frame during FPS skips
- Trails effect (samples previous frame for motion trail blend)

Event: `AfterRenderingPostProcessing + 2` (after MirrorEffectPass).

---

## Highway Rendering

### Architecture

All highways and vocal tracks are rendered in a **single forward pass** by one "highway renderer" camera. Per-highway cameras exist **only as sources for view/projection matrices** — they never render.

### How It Works

1. Highway geometry is partitioned by world X position: each highway occupies a ~100-unit wide zone
2. Per-vertex, `highways.hlsl` determines which highway a vertex belongs to: `index = (positionWS.x + 10) / 100`
3. The vertex shader fetches the respective view/projection matrices from a GPU structured buffer
4. Per-vertex curving is applied based on the player's curve factor
5. All highways render together into `_highwaysColorTexture`

### Injected Shader: `highways.hlsl`

Included in all highway shaders. Provides:

- **`WorldPosToIndex(float3)`** — maps world X position to highway index [0, N-1]
- **`YargGetViewMatrix(int)`** / **`YargGetProjMatrix(int)`** / **`YargGetInvViewMatrix(int)`** — fetch from structured buffer
- **`YargTransformWorldToHClip(float3)`** — full world-to-clip transform with per-highway matrix + curving
- **`YargObjectToClipPos(float3)`** — object-to-clip convenience wrapper
- **`YargWorldSpaceCameraPos(float3)`** — per-highway camera world position
- **`YargTransformWorldToView(float3)`** — world-to-view using per-highway matrix

### Structured Buffers (Persistent, Allocated Once)

Allocated at `BeforeSceneLoad`, never disposed. Global shader bindings:

| Buffer | Size | Content |
|--------|------|---------|
| `_YargCamMatrices` | 32 × 3 `float4x4` (6.2KB) | Interleaved: [view, invView, proj] per highway |
| `_YargCurveFactors` | 32 `float` (128B) | Per-highway curve radius factor |
| `_YargFadeParams` | 32 × 2 `float` (256B) | Per-highway [fadeStart, fadeEnd] distances |

Matrices uploaded only when dirty (view matrix changed). Single `SetData()` call per dirty highway uploads all 3 matrices.

### Per-Highway Camera Matrices

Each `TrackPlayer` has a `TrackCamera` (never renders). `HighwayCameraRendering` extracts:

- **View matrix** — `camera.worldToCameraMatrix`
- **Inverse view matrix** — `camera.cameraToWorldMatrix` (for camera world position)
- **Projection matrix** — modified with NDC post-projection for viewport tiling

Post-projection matrix scales and offsets each highway's clip space to tile them side-by-side:

- Screen divided into N equal horizontal regions
- Each highway scaled to fit its region with padding (`MULTI_LANE_SCALE_FACTOR = 0.90`)
- Capped to max 45% screen width, 55% screen height (72% height single player)
- Horizontal tilt offset via `HighwayTiltMultiplier` setting

### Vocal Track Rendering

Vocal track is rendered alongside highways in the same pass:

- Uses orthographic projection mapped to vocal layout rect
- No curve or tilt (curveFactor = 0, raisedRotation = 0)
- No fade (fadeStart = `float.MaxValue`, fadeSize = 0)
- Positioned at vocal track world position (partitioned like highways)

### Highway Render Texture

- `_highwaysColorTexture` — screen-resolution HDR RT with 16-bit depth
- Highway camera's `targetTexture`
- RGB = color, A channel = fade alpha (written by FadePass)

### HighwayCopyPass

Copies `_highwaysColorTexture` (color + depth) to `_highwaysDepthlessColorTexture` (color-only HDR RT). Required because RenderGraph `ImportTexture` cannot use combined color+depth textures as sources.

Event: `AfterRendering`.

---

## Fade Pass (Alpha Channel)

`FadePass` writes per-vertex fade alpha into the A channel of `_highwaysColorTexture`:

- Event: `BeforeRenderingPostProcessing`
- Uses `HighwaysAlphaMask` shader (override material on all highway renderers)
- `ColorMask A`, `BlendOp Min`, no clear — empty areas stay alpha = 1.0
- Excludes `FadeExclude` layer
- Per-vertex fade computed from camera distance:
  - `dist < fadeStart` → alpha = 1.0 (fully visible)
  - `dist > fadeEnd` → alpha = 0.0 (fully faded)
  - Between → smoothstep interpolation
- Fade params from `_YargFadeParams` structured buffer (per-highway [fadeStart, fadeEnd])

Result: highway geometry areas get fade value, empty areas stay at 1.0. Final composite uses `Blend SrcAlpha OneMinusSrcAlpha` so faded geometry blends correctly.

---

## Highway Composition

`HighwayCompositePass` alpha-blends the highway render texture onto the backbuffer:

- Event: `AfterRendering` (100)
- Source: `_highwaysDepthlessColorTexture` (HDR, RGBA)
- Target: `activeColorTexture` (backbuffer)
- Material: `Hidden/YARG/HighwayComposite` shader
- Blend: `SrcAlpha OneMinusSrcAlpha`
- Y-flip via `scaleBias` on Vulkan/DX12/Metal (`SystemInfo.graphicsUVStartsAtTop`)

Runs after all post-processing, so highways are drawn on top of the final image.

---

## No-Venue Camera

Used when venue skips rendering (FPS cap) or no venue is loaded:

- Created once as singleton by `VenueCameraRendererStatics.EnsureNoVenueCamera()`
- Orthographic camera, `cullingMask = 0` (renders no scene objects)
- `allowDynamicResolution = false`, `allowMSAA = false`
- `clearFlags = SolidColor` (black background)
- `NoVenueCameraRenderer` component enqueues passes on `beginCameraRendering`:
  1. `NoVenueBackgroundPass` — blits background texture to backbuffer
  2. `HighwayCompositePass` — alpha-blends highways on top

### NoVenueBackgroundPass

Fullscreen quad blit of a background texture:

- `_previousFrameTexture` for venue FPS skip (last rendered venue frame)
- `_backgroundRT` for image/video backgrounds
- Texture set via `NoVenueBackgroundPass.backgroundTexture` by `BackgroundManager`

---

## Anti-Aliasing

### Venue Camera AA Settings

Configured per-camera in `VenueCameraRenderer.Awake()` from `GraphicsManager.Instance.VenueAntiAliasing`:

| Setting | URP Mode | Compatible With |
|---------|----------|-----------------|
| `None` | `AntialiasingMode.None` | Everything |
| `FXAA` | `AntialiasingMode.FastApproximateAntialiasing` | DRS, everything |
| `SMAA` | `AntialiasingMode.SubpixelMorphologicalAntiAliasing` | DRS, everything |
| `TAA` | `AntialiasingMode.TemporalAntiAntiAliasing` | **Not** DRS |

### TAA Limitations

TAA is incompatible with:

- **MSAA** — mutually exclusive in URP
- **Dynamic Resolution Scaling (DRS)** — TAA requires stable resolution
- **Camera Stacking** — not supported with TAA

### DRS Interaction

- DRS is only enabled when `renderScale != 1.0`
- When DRS is active, TAA cannot be used (would "trip" TAA)
- `_renderCamera.allowDynamicResolution = true` only set when `renderScale != 1.0`
- `ScalableBufferManager.ResizeBuffers()` called on scale change

### Highway Camera AA

Highway camera has `allowMSAA = true` by default (inherited from URP Asset msaaSampleCount). Highway AA is separate from venue AA — highways render to their own RT.

### No-Venue Camera AA

`allowMSAA = false` — no AA needed on a fullscreen quad blit.

---

## Key Files

| File | Purpose |
|------|---------|
| `VenueCameraRenderer.cs` | Venue camera, FPS capping, DRS, pass enqueue, frame copy |
| `VenueCameraRenderer.VenueCameraRendererStatics` | Shared static state: textures, passes, no-venue camera, FPS tracking |
| `VenueCameraRenderer.VenueFrameCopyPass` | Copy venue frame to `_previousFrameTexture` |
| `HighwayCameraRendering.cs` | Highway camera matrices, structured buffers, fade params, screen space calculations |
| `HighwayCameraRendering.FadePass` | Write fade alpha to highway RT A channel |
| `HighwayCameraRendering.HighwayCopyPass` | Copy highway RT to depthless texture |
| `HighwayCompositePass.cs` | Alpha-blend highways to backbuffer |
| `YargVenuePPPass.cs` | Custom venue PP effects (posterize, scanlines, trails) |
| `MirrorEffectPass.cs` | Mirror UV distortion effect |
| `VenuePostProcessingFrameData.cs` | Ping-pong texture chain for multi-pass venue PP |
| `NoVenueCameraRenderer.cs` | No-venue camera setup and pass enqueue |
| `NoVenueBackgroundPass.cs` | Fullscreen background blit for no-venue mode |
| `highways.hlsl` | Injected per-vertex highway transform (matrix selection, curving) |
| `HighwayComposite.shader` | Highway composite blit shader |
| `HighwaysAlphaMask.shader` | Fade alpha mask shader |
| `YargVenuePP.shader` | Custom venue PP effects shader |
| `MirrorEffect.shader` | Mirror UV distortion shader |
| `GraphicsManager.cs` | Graphics settings (AA method, render scale, bloom, film grain) |
