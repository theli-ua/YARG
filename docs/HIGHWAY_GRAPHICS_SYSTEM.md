# Highway Graphics System (Instanced Note Rendering)

Instanced highway note heads via Unity `BatchRendererGroup` (BRG) + DOTS instancing.
**Mental model:** highway particle/instancer with EGS-inspired GPU buffer layout — not a mini Entities Graphics clone.

Replaces per-note GameObject/MeshRenderer note heads. Sustains and beatlines remain GameObjects (deferred).

## Architecture Overview

```
GameManager.Update
    └── foreach TrackPlayer.GameplayUpdate()
            ├── NoteTracker.RemoveExpired()
            ├── NoteTracker.UpdateStarPowerColors() / pulse (when needed)
            └── NoteTracker.UploadToGPU(trackLocalToWorld)
                    ├── BeginUploadFrame()     — reset ALL batches' activeCount=0 (once/frame)
                    └── for each active note × assignments:
                          UploadInstance(O2W, W2O, baseColor, emission, random*)
            // SP mesh reassignment deferred (no UpdateBatchAssignments)

GameManager.Update (after all TrackPlayers)  ← primary flush site
    └── TrackViewManager.FlushHighwayInstanceUploads()
            └── EndUploadFrame()
                    ├── SparseUploader.Commit()
                    ├── framesUnused accounting
                    └── periodic GarbageCollectEmptyBatches (every ~300 frames)
    HighwayCameraRendering.LateUpdate → EndUploadFrame backup only
    (Do not rely on HCR LateUpdate alone — missed commits stuck SparseUploader full)

BRG Culling (render thread)
    └── OnPerformCullingCallback()
            └── for each batch with activeCount > 0:
                  BatchDrawCommand { visibleCount = activeCount, splitVisibilityMask = 0xffff }
                  visibleInstances = [0 .. activeCount-1]
```

## Components

### HighwayElementGraphicsSystem

One instance per highway camera, shared by all players on that camera.

**Owns:** `BatchRendererGroup`, shared `GraphicsBuffer`, `HeapAllocator`, `SparseUploader`, batch registry.

**GPU Buffer Layout:**
```
Offset 0:      64 bytes of zeros (BRG safety — unset metadata reads addr 0)
Offset 64+:    HeapAllocator-managed regions
    Each batch (SoA, 160 bytes/instance):
        [objectToWorld: 48*N]   // packed float3x4
        [worldToObject: 48*N]   // packed float3x4 (affine inverse)
        [baseColor:     16*N]   // float4 _BaseColor
        [emission:      16*N]   // float4 _EmissionColor + _Emission (same region)
        [randomFloat:    4*N]   // float _RandomFloat (stride must match shader)
        [randomVector:  16*N]   // float4 _RandomVector (xy used)
        // region starts 16-aligned; heap block sized ~160*N
```

**Initial buffer:** 8 MB (grows up to 64 MB on demand). Growth commits pending uploads, copies existing contents, remaps batch buffer handles, recreates SparseUploader.

**ElementBatch** (class — mutations persist in registry):
- BRG IDs, capacity, activeCount, SoA byte offsets
- `meshLocalOffset`, `emissionAddition`, `emissionMultiplier`
- `framesUnused` — for GC (not live activeCount)

**BatchKey:** `(meshInstanceID, materialInstanceID, submeshIndex, sourceRendererID)`

**Capacity:** new batches size to `capacityPerPlayer × max(playerCount, 4)`. Overflow grows via `EnsureCapacity` (re-AddBatch; dense rewrite means no need to preserve GPU slots).

#### Critical Invariants

**1. `activeCount` ownership**
Only writers:
- `BeginUploadFrame()` — reset all to 0 once/frame (`Time.frameCount`)
- `UploadInstance()` — bump to cover written slot

`Add()` / `Remove()` / `RemoveExpired()` MUST NOT touch `activeCount`.

**2. Shared-batch append**
Batches shared across trackers (same theme → same key). Write slot = `batch.activeCount` so trackers append, never overwrite.

**3. Single commit/frame**
Trackers only `AddUpload`. `EndUploadFrame()` (HCR `LateUpdate`) commits once.

**4. GC never uses live `activeCount` alone**
`BeginUploadFrame` zeros counts. GC uses `framesUnused` after `EndUploadFrame`.

### SparseUploader

EGS-style scatter into destination GraphicsBuffer.

- **Compute path (default):** ring of `NumFramesInFlight+1` intermediate buffers, LockBufferForWrite, ops from start / data from end, Dispatch
- **Direct fallback:** stage + single `SetData` over dirty range
- **Guards:** ops+data must not meet in the middle (drop + error log if full)
- **CommitCompute:** always clears lock + `m_OperationOffset`/`m_DataOffset` (next frame re-locks clean)
- **repeatCount:** one Operation with `count=N` (EGS semantics), not N ops

### NoteTracker

Per-TrackPlayer CPU state. Flat arrays, swap-remove, chart-note reverse lookup.

**Per frame (GameplayUpdate):**
1. `RemoveExpired` — Z &lt; -4, backward scan, no alloc
2. SP color / drums activator pulse when needed
3. `UploadToGPU` — Z from visual time, world = trackL2W × T(baseX,0,z)×S(scale) × meshLocal

**Color / emission parity (matches old NoteGroup):**
- Colored / NoSP: `baseColor = color + EmissionAddition`, `emission = baseColor * EmissionMultiplier`
- Metal: `baseColor = emission = metalColor`
- `_RandomFloat` / `_RandomVector` uploaded per instance from `NoteData`

**Hit/miss:** immediate `TryRemoveByNote` — intentional; no sliding miss-colored head on BRG path.

### ThemeMeshCache

Load-time extract from theme prefabs (caller instantiates once, destroys after).

`RenderGroup`: mesh, submesh, material, meshLocalOffset, sourceRendererID, emission add/mul.

**Submesh:** material index ≠ always submesh. Single-submesh → 0; multi → clamp material index to submesh count.

**Materials:** `sharedMaterials` (no per-note clone). Instancing enabled on assets.

### PackedMatrix

48-byte float3x4. `FromAffineInverse` for TRS notes (no full 4x4 inverse each instance).

## Frame Timeline

```
Main Thread Update:
    TrackPlayers → NoteTracker.UploadToGPU (queue scatter ops only)

Main Thread LateUpdate (HighwayCameraRendering):
    EndUploadFrame → SparseUploader.Commit → compute dispatch

Render Thread:
    BRG OnPerformCulling → draw commands
    Forward: Hybrid Batch Group note draws into highway RT
    FadePass: same batches + HighwaysAlphaMask override (DOTS instance ID required)
    HighwayComposite: SrcAlpha blend to backbuffer
```

## Fade Pass + BRG

FadePass override material **does** apply to Hybrid Batch Group in Frame Debugger, but only writes correct pixels if `HighwaysAlphaMask`:
- `#pragma multi_compile _ DOTS_INSTANCING_ON`
- `UNITY_VERTEX_INPUT_INSTANCE_ID` + `UNITY_SETUP_INSTANCE_ID` in vert

Without instance setup, alpha lands at wrong screen positions → notes stay full opacity past fade.

See `docs/RENDERING_PIPELINE.md` Fade Pass section.

## Performance

| Metric | GameObject path | BRG path |
|--------|-----------------|----------|
| Draw calls | 1 per note mesh | 1 per batch |
| CreateSharedRendererScene | Per-note churn | Zero for note heads |
| MonoBehaviour LateUpdate | Per note | Zero for note heads |
| GPU upload | Unity automatic | Dense rewrite via SparseUploader (1 commit/frame) |

**Known cost:** 3 material categories × same transforms (Colored / NoSP / Metal). Acceptable; optional later: share O2W/W2O SoA across category batches.

**Not Burst-parallel yet.** Main-thread fill is fine for hundreds of visible notes.

## Shader Requirements

Note shaders:
- `_BaseColor` DOTS instanced (Shader Graph override reference)
- Prefer `_Emission` / `_EmissionColor` readable as DOTS props (same buffer region)
- `_RandomFloat`, `_RandomVector` if theme uses them
- `m_EnableInstancingVariants: 1`
- Highway clip via `highways.hlsl` / Yarg transforms (multi-highway)

## Thread Safety

- `AddUpload` / `UploadInstance` — main thread only
- `OnPerformCulling` — render thread, no managed alloc (`UnsafeUtility.Malloc(TempJob)`)
- Commit before render via LateUpdate ordering

## Known Limitations

1. Sustain lines / beatlines still GameObjects
2. SP **mesh** variant switching deferred (`UpdateBatchAssignments` no-op); SP **color** works
3. Hit/miss removes head immediately (no lingering miss mesh on BRG path)
4. Dense SparseUploader use (full rewrite) — not sparse dirty-bits; fine at current N
5. No frustum cull (huge global bounds)
6. Three category batches still triple transform bandwidth
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
| `TrackPlayer.cs` | Spawn / GameplayUpdate integration |
| `HighwayCameraRendering.cs` | Owns HEGS; `EndUploadFrame` in LateUpdate |
