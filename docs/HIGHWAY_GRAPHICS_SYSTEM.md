# Highway Graphics System (Instanced Note Rendering)

Instanced highway note heads via Unity `BatchRendererGroup` (BRG) + DOTS instancing.
**Mental model:** highway particle/instancer with EGS-inspired GPU buffer layout — not a mini Entities Graphics clone.

Replaces per-note GameObject/MeshRenderer note heads. Sustains and beatlines remain GameObjects (deferred).  
**Performance claim scope:** note-head path only — sustains/beatlines still contribute to `CreateSharedRendererScene`.

Authoritative design: `openspec/changes/custom-instanced-note-rendering/design.md`.

## Architecture Overview

```
GameManager.Update
    BeginHighwayInstanceUploads()          ← BeginUploadFrame (always; even 0 notes)
    └── foreach TrackPlayer.GameplayUpdate()
            ├── NoteTracker.RemoveExpired()
            ├── SP edge → UpdateStarPowerColors (dedicated flag)
            ├── drums SP-activator pulse
            └── NoteTracker.UploadToGPU     ← queue ops only
    FlushHighwayInstanceUploads()          ← EndUploadFrame → Commit
    HighwayCameraRendering.LateUpdate      ← EndUploadFrame backup only

No batch GC. Memory upper-bounded by theme batch set × fixed capacity.
All batches freed only when HEGS is disposed (song/session end).

BRG Culling (render thread, highway camera viewID only)
    └── OnPerformCullingCallback()
            if viewID != highwayCamera → empty
            else for each batch with activeCount > 0:
                  BatchDrawCommand { visibleCount = activeCount, splitVisibilityMask = 0xffff }
                  visibleInstances = [0 .. activeCount-1]
```

## Components

### HighwayElementGraphicsSystem

One instance per highway camera, shared by all players on that camera.

**Owns:** `BatchRendererGroup`, shared `GraphicsBuffer`, `HeapAllocator`, `SparseUploader`, batch registry, `_highwayCameraID`.

**Does not own:** tracker lists (trackers hold HEGS ref).

**GPU Buffer Layout:**
```
Offset 0:      64 bytes of zeros (BRG safety — unset metadata reads addr 0)
Offset 64+:    HeapAllocator-managed regions
    Each batch (SoA, ~160 bytes/instance budget):
        [objectToWorld: 48*N]   // packed float3x4
        [worldToObject: 48*N]   // packed float3x4 (affine inverse)
        [baseColor:     16*N]   // float4 _BaseColor
        [emission:      16*N]   // float4 _EmissionColor + _Emission (same region)
        [randomFloat:    4*N]   // float _RandomFloat
        [randomVector:  16*N]   // float4 _RandomVector (xy used)
```

**Initial buffer:** 8 MB (grows up to 64 MB on demand for *new* heap allocations). Growth commits pending uploads, copies existing contents, remaps batch buffer handles, recreates SparseUploader.

**ConstantBuffer platforms (e.g. Metal):** `AddBatch(metadata, handle, bufferOffset, windowSize)` with alignment + max window caps. Batch create fails if SoA does not fit max window.

**ElementBatch** (class — mutations persist in registry):
- BRG IDs, capacity, activeCount, SoA byte offsets
- `meshLocalOffset`, `emissionAddition`, `emissionMultiplier`

**BatchKey:** `(meshInstanceID, materialInstanceID, submeshIndex, sourceRendererID)`

**Capacity (fixed at create):** `capacityPerPlayer × max(playerCount, 4)`.  
**No mid-frame grow after instances written this frame** (would wipe prior tracker appends). Overflow → drop + log.

#### Critical Invariants

1. **`activeCount` ownership** — only `BeginUploadFrame` (reset) and `UploadInstance` (bump)
2. **Shared-batch append** — write slot = `batch.activeCount`
3. **Begin always / Commit once** — GM boundary; trackers never Commit
4. **No batch GC mid-song**
5. **Highway camera filter** — `viewID.GetInstanceID() == _highwayCameraID`
6. **No capacity grow after write this frame**

### SparseUploader

EGS-style scatter into destination GraphicsBuffer. Dense rewrite access pattern (historical name).

- **Compute path (default):** ring of `NumFramesInFlight+1` intermediate buffers
- **Direct fallback:** stage + single `SetData` over dirty range
- **CommitCompute:** always clears lock + offsets

### NoteTracker

Per-TrackPlayer CPU state. Flat arrays, swap-remove, chart-note reverse lookup.

**Per frame:**
1. `RemoveExpired` — Z < -4
2. SP color / drums activator pulse when needed
3. `UploadToGPU` — world = trackL2W × T(baseX,0,z)×S(scale) × meshLocal

**Hit/miss:** immediate `TryRemoveByNote` for **all** hits including sustain heads. Line stays GO.

**Spawn Add:** pooled exact-size assignment arrays (rent/return on remove).

**SP colors:** `TrackPlayer.ResolveInstancedStarPowerColors` virtual; dedicated `WasStarPowerActiveForNotes` edge (not scoop flag).

### ThemeMeshCache

Load-time extract. `RenderGroup`: mesh, submesh, material, meshLocalOffset, sourceRendererID, emission add/mul.

## Frame Timeline

```
Main Thread Update:
    BeginUploadFrame → activeCount=0 all batches
    TrackPlayers → NoteTracker.UploadToGPU (queue only)
    EndUploadFrame → SparseUploader.Commit

Render Thread (highway camera only):
    BRG OnPerformCulling → draw commands
    Forward Hybrid Batch Group into highway RT
    FadePass: HighwaysAlphaMask + DOTS instance ID
    HighwayComposite: SrcAlpha blend to backbuffer
```

## Fade Pass + BRG

See `docs/RENDERING_PIPELINE.md`. Override material needs:
- `#pragma multi_compile _ DOTS_INSTANCING_ON`
- `UNITY_VERTEX_INPUT_INSTANCE_ID` + `UNITY_SETUP_INSTANCE_ID` in vert

## Performance

| Metric | GameObject path | BRG path |
|--------|-----------------|----------|
| Draw calls (note heads) | 1 per note mesh | 1 per batch |
| CreateSharedRendererScene (heads) | Per-note churn | Zero for heads |
| MonoBehaviour LateUpdate (heads) | Per note | Zero for heads |
| GPU upload | Unity automatic | Dense rewrite, 1 commit/frame |

**Known cost:** 3 material categories × same transforms (Decision 18 deferred — ConstantBuffer window constraints).

## Shader Requirements

- `_BaseColor` DOTS instanced
- `_Emission` / `_EmissionColor`, `_RandomFloat`, `_RandomVector` if theme uses them
- `m_EnableInstancingVariants: 1`
- Highway clip via `highways.hlsl`

## Thread Safety

- `AddUpload` / `UploadInstance` / Begin/End — main thread only
- `OnPerformCulling` — render thread; `_highwayCameraID` written once at setup before render
- Commit before render via GM flush (+ LateUpdate backup)

## Known Limitations

1. Sustain lines / beatlines still GameObjects
2. SP **mesh** variant switching deferred; SP **color** works
3. Miss removes head immediately (no lingering miss mesh)
4. Dense SparseUploader use — fine at current N
5. No frustum cull of instances (global bounds; camera filter only)
6. Three category batches triple transform bandwidth
7. Debug logs gated: `HighwayElementGraphicsSystem.DebugLogging`, `ThemeMeshCache.DebugLogging`

## Key Files

| File | Role |
|------|------|
| `HighwayElementGraphicsSystem.cs` | BRG, buffer, batches, culling, upload API |
| `NoteTracker.cs` | CPU note lifecycle + per-frame upload |
| `ThemeMeshCache.cs` | Theme mesh/mat/emission extract |
| `SparseUploader.cs` + `.compute` | Scatter GPU writes |
| `PackedMatrix.cs` | float3x4 + affine inverse |
| `NoteData.cs` | Blittable note + spawn structs |
| `HighwaysAlphaMask.shader` | Fade A-channel (DOTS-safe) |
| `TrackPlayer.cs` | Spawn / GameplayUpdate / SP edge |
| `HighwayCameraRendering.cs` | Owns HEGS; SetHighwayCamera; flush API |
| `GameManager.cs` / `TrackViewManager.cs` | Begin/End upload frame boundary |
