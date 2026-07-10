# Highway Graphics System (Instanced Note Rendering)

Instanced highway note heads, sustain strips, and beatlines via Unity `BatchRendererGroup` (BRG) + DOTS instancing.

**Mental model:** fixed-capacity highway particle/instancer — dense CPU staging + contiguous GPU `SetData`. **Not** Entities Graphics.

Replaces per-note GameObject/MeshRenderer note heads, sustain strips, and beatlines.

**Sustains (unit mesh):** one static strip mesh (X∈[-0.5,0.5], Z∈[0,1]). Per instance:
`T(baseX, 0, noteZ+startZ) × S(width, 1, visibleLength)`. No mesh rebuild.
Hitting clips `startZ` so start sits on strike line. State → color / `_IsActive` / `_WhammyAmount`.

## Architecture Overview

```
GameManager.Update
    BeginHighwayInstanceUploads()              ← BeginUploadFrame
    foreach GameplayUpdate()                   ← expire/spawn/SP (no GPU)
    foreach CollectHighwayInstanceUploadDirtiness()
    foreach UploadHighwayInstances()           ← staging; transforms optional
    FlushHighwayInstanceUploads()              ← SetData (xforms if dirty)
    HighwayCameraRendering.LateUpdate          ← EndUploadFrame backup

No SparseUploader. No HeapAllocator. No buffer grow. No batch GC.
Fixed caps in HighwayInstancingLimits. GPU buffer sized once at create.
Bump-allocate batch SoA only; free only on HEGS Dispose.

BRG Culling (render thread, highway camera viewID only)
    └── OnPerformCullingCallback()
            if viewID != highwayCamera → empty
            else for each batch with activeCount > 0:
                  BatchDrawCommand { visibleCount = activeCount }
                  visibleInstances = [0 .. activeCount-1]
```

## Fixed limits (`HighwayInstancingLimits`)

| Cap | Value | Role |
|-----|------:|------|
| MaxPlayers | 4 | Shared-batch multiplier |
| MaxNotesPerPlayer | 512 | CPU tracker + spawn gate |
| MaxSustainsPerPlayer | 256 | CPU tracker |
| MaxBeatlinesPerPlayer | 64 | CPU tracker |
| MaxBatches | 96 | Theme mesh×material combos |
| Shared note instances / batch | 2048 | GPU (CB window may clamp) |
| Shared sustain instances / batch | 1024 | GPU (CB window may clamp) |
| Shared beatline instances / batch | 256 | GPU (CB window may clamp) |

ConstantBuffer platforms clamp capacity so one batch SoA fits `GetConstantBufferMaxWindowSize()`.

## Components

### HighwayElementGraphicsSystem

One instance per highway camera, shared by all players on that camera.

**Owns:** `BatchRendererGroup`, fixed `GraphicsBuffer`, batch registry, `_highwayCameraID`.

**Does not own:** tracker lists.

**GPU buffer:**
```
Offset 0:      ≥64B zeros (BRG safety; CB-aligned)
Offset bump+:  fixed SoA per batch (bump only)
  Note/beatline:
    [O2W 48*N][W2O 48*N][color 16*N][emission 16*N][randF][randV]
  Sustain:
    [O2W][W2O][color][emission][_IsActive][_WhammyAmount]
```

**Upload path:**
1. `UploadInstance` / `UploadSustainInstance` write `NativeArray` staging on `ElementBatch`
2. Matrices use **rest-Z** = `STRIKE + hitTime * noteSpeed` (no `- visualTime`)
3. `highways.hlsl` `YargApplyDotsScroll` (DOTS only): `z -= _YargVisualTime * _YargNoteSpeeds[highway]`
4. `EndUploadFrame` → contiguous `GraphicsBuffer.SetData` of `[0..activeCount)` per SoA region

Scroll is in the shared HLSL include — no Shader Graph property changes. FadePass uses the same scroll for distance.

**ElementBatch:** BRG IDs, fixed capacity, activeCount, GPU offsets, staging arrays, emission bake.

**BatchKey:** `(meshInstanceID, materialInstanceID, submeshIndex, sourceRendererID)`

**Invariants:**
1. `activeCount` only reset in BeginUploadFrame / bumped in Upload*
2. Shared-batch append via `batch.activeCount` write slot
3. Begin always / dense flush once per frame
4. No grow, no free mid-song

### NoteTracker / SustainTracker / BeatlineTracker

Per-TrackPlayer CPU state. Flat arrays, swap-remove. Capacities from `HighwayInstancingLimits`.

### ThemeMeshCache

Load-time extract. Render groups: Colored / NoStarPower / Metal / Static.

## Frame Timeline

```
Main Thread Update:
    BeginUploadFrame → activeCount=0, dirty=false
    Trackers → write staging
    EndUploadFrame → SetData dirty regions

Render Thread (highway camera only):
    BRG OnPerformCulling → draw commands
    Forward Hybrid Batch Group into highway RT
    FadePass: HighwaysAlphaMask + DOTS instance ID
    HighwayComposite: SrcAlpha blend to backbuffer
```

## Fade Pass + BRG

See `docs/RENDERING_PIPELINE.md`. Override material needs DOTS instance ID setup.

## Shader Requirements

- `_BaseColor` DOTS instanced
- `_Emission` / `_EmissionColor`, `_RandomFloat`, `_RandomVector` if theme uses them
- `m_EnableInstancingVariants: 1`
- Highway clip via `highways.hlsl`

## Thread Safety

- Upload / Begin / End — main thread only
- OnPerformCulling — render thread; `_highwayCameraID` set once before render

## Known Limitations

1. Miss removes head immediately (no lingering miss mesh)
2. No frustum cull of instances (global bounds; camera filter only)
3. ConstantBuffer platforms clamp shared-batch capacity
4. Material categories still ×N draws/uploads per logical note
5. Dirty-only transforms: skip O2W/W2O/random SetData when topology/track/speed stable; appearance always. Hitting sustains force transforms.
6. Sustain UV is unit-relative vs old absolute-length UV
7. Lanes / frets / track effects still GameObjects
8. Debug logs: `HighwayElementGraphicsSystem.DebugLogging`, `ThemeMeshCache.DebugLogging`

## Key Files

| File | Role |
|------|------|
| `HighwayInstancingLimits.cs` | Fixed capacity constants |
| `HighwayElementGraphicsSystem.cs` | BRG, fixed buffer, dense upload, culling |
| `NoteTracker.cs` | CPU note lifecycle + upload |
| `SustainTracker.cs` | Unit-mesh sustains |
| `BeatlineTracker.cs` | Instanced beatlines |
| `ThemeMeshCache.cs` | Theme extract |
| `PackedMatrix.cs` | float3x4 + affine inverse |
| `NoteData.cs` | Blittable note + spawn |
| `HighwaysAlphaMask.shader` | Fade A-channel (DOTS-safe) |
| `TrackPlayer.cs` | Spawn / GameplayUpdate / SP edge |
| `HighwayCameraRendering.cs` | Owns HEGS; flush API |
| `GameManager.cs` / `TrackViewManager.cs` | Begin/End upload frame boundary |
