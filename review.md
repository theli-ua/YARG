# Review: Rendering Pipeline Separation

**Branch:** `rendering-overhaul-2026-take-3`
**Diff:** `upstream/dev...HEAD` — 23 files, +809 / −987 lines
**Date:** 2026-05-11

## Where to find the spec

- **OpenSpec change:** `openspec/changes/rendering-pipeline-separation/`
  - `proposal.md` — Why, what changes, impact
  - `design.md` — Context, decisions, risks, migration plan
  - `tasks.md` — Checklisted tasks (all implementation tasks checked, Phase 8 validation unchecked)
  - `specs/` — Per-capability spec files
- **OpenSpec spec:** `openspec/specs/rendering-pipeline-separation/`
  - `spec.md` — Full architecture, problem, goals, frame flow, components
  - `tasks.md` — Original task breakdown (Phases 1–8)

## Change Summary

Separates highway rendering from venue/background rendering to fix HDR compositing. Previously, highways rendered to the backbuffer and the venue was blended on top using `Blend OneMinusDstAlpha DstAlpha`, which reads the backbuffer alpha — unreliable under HDR.

**New pipeline:**

1. Highway Renderer Camera → `HighwaysRT` (DefaultHDR, screen-res)
2. Venue Camera → backbuffer directly (no intermediate RT)
3. `HighwayCompositePass` → blits `HighwaysRT` over backbuffer (`SrcAlpha OneMinusSrcAlpha`)
4. New "No Venue" camera → fullscreen quad sampling `_YargPrevFrame` (for FPS skips and image/video backgrounds)
5. `BackgroundManager` creates its own RT for image/video, routed through `_YargPrevFrame`

**Files changed:**

| File | Change |
|---|---|
| `HighwayCameraRendering.cs` | HighwaysRT creation, depthless copy RT, FadePass depth texture, Cleanup/Copy passes |
| `HighwayCompositePass.cs` | *(new)* RenderGraph pass: blit HighwaysRT → backbuffer |
| `NoVenueBackgroundPass.cs` | *(new)* RenderGraph pass: fullscreen quad for No Venue camera |
| `VenueCameraRenderer.cs` | Backbuffer rendering, No Venue camera toggle, statics class refactor |
| `BackgroundManager.cs` | Image/video RT, `_YargPrevFrame` routing, shader-driven dimmer |
| `GameManager.cs` | Expose `NoVenueCamera` reference |
| `HighwayComposite.shader` | *(new)* Simple alpha-blend composite shader |
| `NoVenueQuad.shader` | *(new)* Fullscreen quad sampling `_YargPrevFrame` |
| `UberPP.shader` | Use `inputColor.a` instead of backbuffer alpha for fade mask |
| `CheckerBoard.shader` | `_YargBackgroundAlpha` dimmer support |
| `FretHit_Flash.mat` / `ParticlesUnlit.mat` | Updated blend modes for highways RT |
| `Gameplay.unity` | Removed UI RawImages (Highways Output, Venue Output, Dimmer, Fade Overlay) |
| `PersistentScene.unity` | Deactivated Dimmer Canvas |
| `GraphicsSettings.asset` | Shader preload update |
| `FakeTrackPlayer.cs` / `CameraPreviewTexture.cs` | Preview compatibility |

---

## 🔴 High Priority

### 1. `allowDynamicResolution` not implemented

✅ **DONE** — Enabled `_renderCamera.allowDynamicResolution = true` in `Awake()`. Added `_previousRenderScale` tracking in `Update()` to call `ScalableBufferManager.ResizeBuffers(renderScale, renderScale)` whenever `VenueRenderScale` changes (not just on screen resize). Commit: `a18b968c`

### 2. `NoVenueBackgroundPass` dead import

✅ **DONE** — Removed dead `RTHandles.Alloc()`, `RenderTargetInfo`, `ImportResourceParams`, and `ImportTexture()`. Pass now renders fullscreen quad via `Blitter.BlitTexture` and the shader reads `_YargPrevFrame` global as intended. Added explanatory comment. Commit: `ac1deaba`

### 3. `GraphicsSettings.asset` null preload entry

✅ **DONE** — Removed the `{fileID: 0}` null entry from `m_AlwaysIncludedShaders`. Commit: `7abc6ca8`

---

## 🟡 Medium Priority

### 4. `VenueCameraRendererStatics` lifecycle

✅ **DONE** — Added `_isInitialized` guard to `Initialize()`. Pass creation in `Awake()` guarded by null-check. `Awake()` now calls `Initialize()` instead of duplicating texture creation. Commit: `3ee37e39`

### 5. NoVenueCamera never destroyed

✅ **DONE** — Added `OnDestroy()` that destroys `NoVenueCamera.gameObject` and nulls the reference. Commit: `fe24ef59`

### 6. Dead `scaling` variable

✅ **DONE** — Removed the unused `scaling` variable from `ResetHighwayAlphaTexture()`. The alpha mask renders at screen resolution as intended. Commit: `fe1a3b9e`

### 7. `FakeTrackPlayer` uses `[Obsolete]` `VenueTexture`

✅ **DONE** — Already addressed in current code. `FakeTrackPlayer.cs` uses `VenueCameraRenderer.VenueCameraRendererStatics._trailsTexture` directly.
