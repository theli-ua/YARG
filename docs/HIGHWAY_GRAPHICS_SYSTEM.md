# Highway Graphics System (Instanced Note Rendering)

This document describes the instanced rendering system for highway notes in YARG. It replaces the per-note GameObject/MeshRenderer approach with Unity's `BatchRendererGroup` (BRG) API and DOTS-style GPU instancing.

## Architecture Overview

```
TrackPlayer.GameplayUpdate()
    ├── NoteTracker.UpdatePositions()        — (no-op; Z computed in UploadToGPU)
    ├── NoteTracker.RemoveExpired()          — backward scan, swap-remove notes past strike line
    ├── NoteTracker.UpdateBatchAssignments() — (no-op placeholder; SP mesh switching deferred)
    └── NoteTracker.UploadToGPU(trackLocalToWorld)
            ├── BeginUploadFrame()           — reset ALL batches' activeCount=0 (once/frame)
            ├── for each active note:
            │   └── HighwayElementGraphicsSystem.UploadInstance()
            │           ├── SparseUploader.AddUpload(objectToWorld)    — 48 bytes
            │           ├── SparseUploader.AddUpload(worldToObject)    — 48 bytes
            │           ├── SparseUploader.AddUpload(baseColor)        — 16 bytes
            │           └── batch.activeCount = instanceIndex + 1      — drives culling visibleCount
            └── HighwayElementGraphicsSystem.UploadDirtyData()
                    └── SparseUploader.Commit()
                            ├── Compute shader path (default)
                            │   ├── LockBufferForWrite → write ops+data → UnlockBufferAfterWrite → Dispatch
                            └── Direct path (fallback)
                                └── scatter into NativeArray → single GraphicsBuffer.SetData

BRG Culling (render thread, each frame)
    └── OnPerformCullingCallback()
            └── iterate _batches.Values directly (NOT through trackers):
                └── for each batch with activeCount > 0:
                    └── BatchDrawCommand { visibleCount = activeCount }
                    └── visibleInstances = [0, 1, ..., activeCount-1]  (inline, no managed alloc)
```

## Components

### HighwayElementGraphicsSystem

Central manager for instanced highway rendering. One instance per highway camera, shared by all players on that camera.

**Responsibilities:**
- Owns the `BatchRendererGroup` and its culling callback
- Owns the shared GPU `GraphicsBuffer` (all instance data lives here)
- Manages `HeapAllocator` for batch memory within the GPU buffer
- Manages `SparseUploader` for incremental GPU updates
- Registry of `ElementBatch` instances (mesh+material groups)
- Registry of `INoteTracker` instances (one per TrackPlayer)
- `BeginUploadFrame()` — resets all batches' `activeCount` to 0 once per frame

**GPU Buffer Layout:**
```
Offset 0:      64 bytes of zeros (BRG safety zone — unset metadata reads from addr 0)
Offset 64+:    HeapAllocator-managed regions
    Each batch region:
        [objectToWorld: 48*N] [worldToObject: 48*N] [baseColor: 16*N]
        Total: 112 bytes per instance, N = batch capacity
```

**ElementBatch** — groups instances sharing the same mesh, material, and submesh. Class (not struct) so mutations via `UploadInstance` persist in the registry. Each batch owns a contiguous region in the GPU buffer with Structure-of-Arrays (SoA) layout:
- `objectToWorldOffset` — byte offset to packed float3x4 array (48 bytes/instance)
- `worldToObjectOffset` — byte offset to packed float3x4 array (48 bytes/instance)
- `baseColorOffset` — byte offset to float4 array (16 bytes/instance)
- `activeCount` — instances written THIS FRAME (drives culling `visibleCount`). Owned exclusively by per-frame uploads — see invariants below.
- `meshLocalOffset` — per-mesh local transform (captured at theme extraction)

**BatchKey** — unique identifier for a batch: `(meshID, materialID, submeshIndex, sourceRendererID)`.

#### Critical Invariants

**1. `activeCount` ownership:**
`batch.activeCount` is owned EXCLUSIVELY by per-frame upload logic. The only permitted writers are:
- `BeginUploadFrame()` — resets ALL batches' `activeCount` to 0 (once per frame, idempotent via `Time.frameCount`)
- `UploadInstance()` — bumps to `max(activeCount, instanceIndex + 1)`

`NoteTracker.Add()` and `Remove()` MUST NOT touch `activeCount`. If they did, the culling callback's `visibleCount` would disagree with the actual GPU data written that frame, rendering stale slots → flicker.

**2. Shared-batch append semantics:**
Batches are SHARED across trackers (same theme → same `BatchKey` → same `ElementBatch`). Multiple trackers writing to the same batch in the same frame MUST append, not overwrite. `UploadToGPU` uses `batch.activeCount` as the instance write slot:
- Tracker 0: `BeginUploadFrame` resets `activeCount=0`. Writes notes to slots 0..N-1. `activeCount=N`.
- Tracker 1: `BeginUploadFrame` is a no-op (same frame). Writes notes to slots N..N+M-1 (appends). `activeCount=N+M`.

Using a per-tracker counter that resets to 0 would cause tracker 1 to overwrite tracker 0's GPU data → one highway goes empty, the other populated, oscillating frame-to-frame = flicker.

### SparseUploader

Uploads scattered data into the GPU GraphicsBuffer. Two paths:

#### Compute Shader Path (default)

Follows the Unity.Entities.Graphics (EGS) architecture:

1. **`LockBufferForWrite<byte>(0, chunkSize)`** — maps persistent intermediate buffer into CPU address space
2. **Write operations** at buffer start (grow forward), **write data** at buffer end (grow backward)
3. **`UnlockBufferAfterWrite<byte>(chunkSize)`** — flushes CPU writes, makes data visible to GPU
4. **`ComputeShader.Dispatch()`** — each thread group (64 threads) processes one upload operation

The compute shader (`Assets/Resources/SparseUploader.compute`) reads Operation structs + data from the intermediate buffer and scatters writes to the destination buffer. Supports operation types: Upload, Matrix_4x4, Matrix_Inverse_4x4, Matrix_3x4, Matrix_Inverse_3x4, StridedUpload.

**Buffer layout (intermediate, 16 MB chunk):**
```
[Op0][Op1][Op2]...[padding]...[DataN][DataN-1]...
 ↑ ops grow forward        ↑ data grows backward
```

A ring of `NumFramesInFlight + 1` intermediate buffers prevents CPU overwriting GPU-read buffers.

#### Direct Path (fallback)

Used when compute shader is unavailable (e.g., platform doesn't support it):

1. `AddUpload` — eagerly copies data into a growing CPU staging buffer, tracks dirty range `[minOffset, maxOffset)`
2. `Commit` — scatters staged data into a single `NativeArray<int>` covering the dirty range, calls `GraphicsBuffer.SetData` once. Resets staging size to 0 (prevents unbounded growth).

Reduces ~600 per-note SetData calls to 1 call per frame.

### NoteTracker

Per-TrackPlayer manager for active notes. Flat arrays, no per-note GameObjects.

**Data structures:**
- `NativeArray<NoteData> _notes` — per-note color/flag data (68 bytes each)
- `NativeArray<NoteSpawnData> _spawnData` — per-note spawn-time data (28 bytes each)
- `NoteBatchAssignment[][] _batchAssignments` — note index → batch assignments (3 per note: Colored/NoStarPower/Metal)
- `object[] _noteObjects` — chart note references for reverse lookup
- `Dictionary<object, int> _noteToIndex` — chart note → flat index for hit/miss
- `GameManager _gameManager` — cached at construction (avoids `FindAnyObjectByType` per frame)

**NoteData** (68 bytes, blittable):
- `Vector4 color` — SP/miss-aware color for ColoredMaterials
- `Vector4 colorNoStarPower` — always non-SP fret color
- `Vector4 metalColor` — color for ColoredMetalMaterials
- `int highwayIndex` — from BasePlayer.HighwayIndex
- `float randomFloat` — random value [-1, 1]
- `Vector2 randomVector` — random 2D vector for theme variation
- `uint packedFlags` — bitfield: noteType (8 bits), isStarPower, isSustain, isOpenNote

**NoteSpawnData** (28 bytes, blittable):
- `float noteHitTime` — chart note's hit time (for Z position)
- `float baseX` — pre-computed X from GetElementX with lefty-flip
- `Vector3 scale` — per-instrument scale (replaces `noteHeight` float). Guitar/ProKeys: `S(1, noteHeight, 1)`, FiveLaneKeys: `S(5/6, noteHeight*5/6, 1)`, Drums: `S(NoteScaleFactor, noteHeight*NoteScaleFactor, NoteScaleFactor)`
- `ThemeNoteType noteType` — for render group lookup
- `bool isStarPowerVisible` — captured at spawn
- `byte colorIndex` — fret/pad/key index for dynamic color lookups (SP activation)
- `bool isStarPowerActivator` — drums SP-activator flag for pulse effect

**Per-frame update cycle (in `TrackPlayer.GameplayUpdate`):**
1. `UpdatePositions()` — no-op (Z is computed in `UploadToGPU` where `trackLocalToWorld` is available)
2. `RemoveExpired()` — backward iteration, swap-remove notes with `z < -4`. No managed allocations (no `List<int>`).
3. `UpdateBatchAssignments()` — no-op placeholder (SP mesh variant switching is deferred)
4. **SP activation color update** — if `Engine.BaseStats.IsStarPowerActive` changed since last frame, call `NoteTracker.UpdateStarPowerColors()` to recompute `color` and `metalColor` for in-flight SP-visible notes
5. **Drums SP-activator pulse** — `TrackPlayer.UpdateStarPowerActivatorPulse()` (virtual, no-op default). `DrumsPlayer` overrides to pulse SP-activator note colors based on `StrongBeat.CurrentPercentage`
4. `UploadToGPU(trackLocalToWorld)` — calls `BeginUploadFrame`, iterates active notes, computes `worldMatrix = trackLocalToWorld × T(baseX,0,z) × S(spawn.scale) × batch.meshLocalOffset`, writes to `batch.activeCount` slot (shared-batch append), flushes via `UploadDirtyData`

**Hit/miss lifecycle:**
- On hit (non-sustain) or miss: `NoteTracker.TryRemoveByNote(chartNote)` — swap-removes from CPU arrays. `batch.activeCount` is NOT decremented (rebuilt next frame by `UploadToGPU`).
- Sustain note heads are removed on hit; the `SustainLine` GameObject continues independently.

### PackedMatrix

Column-major 4×4 matrix packed into 12 floats (48 bytes). The w-row (0, 0, 0, 1) is dropped because DOTS instancing shaders expect a packed float3x4 and implicitly use (0, 0, 0, 1).

- `FromMatrix4x4(Matrix4x4)` — extracts 3 components per column, drops w
- `FromInverse(Matrix4x4)` — computes full inverse, then packs

### ThemeMeshCache

Static cache keyed by `(ThemeName, ThemeNoteType, StarPowerVariant)`. Extracted from theme prefabs at song load.

**RenderGroup** — `(Mesh, SubmeshIndex, Material, MeshLocalOffset, SourceRendererID)`. Three RenderGroup arrays per note type (Colored/NoStarPower/Metal), each with its own material.

**Extraction** (`ExtractFromTheme`):
1. Instantiate theme prefab once
2. For each ThemeNote child: extract `sharedMesh` + `sharedMaterials[materialIndex]` for all three material arrays
3. Capture `meshLocalOffset = modelRootTransform.worldToLocalMatrix * childMesh.transform.localToWorldMatrix`
4. Store in cache, destroy instantiated GameObject
5. Materials are used directly (no cloning) — instancing already enabled on assets

**Lookup** (`GetRenderGroups`): returns render groups for `(theme, noteType, isStarPowerVisible)`. Falls back to non-SP variant if SP variant absent, then to Wildcard type if specific type absent.

### HeapAllocator

Ported from Unity.Entities.Graphics (`com.unity.entities.graphics`). Manages contiguous memory regions within the GPU buffer. Supports allocation, release, and coalescing of adjacent free blocks.

## Frame Timeline

```
Main Thread (Update phase):
    TrackPlayer.GameplayUpdate()
        NoteTracker.UpdatePositions()         (no-op)
        NoteTracker.RemoveExpired()           (backward scan, swap-remove, no alloc)
        NoteTracker.UpdateBatchAssignments()  (no-op)
        NoteTracker.UploadToGPU()
            BeginUploadFrame()                (reset activeCount=0, once/frame)
            UploadInstance() × 3 per note × 3 batches
                SparseUploader.AddUpload()    (queue packed matrix + color)
            SparseUploader.Commit()
                LockBufferForWrite → write → UnlockBufferAfterWrite → Dispatch

GPU (asynchronous):
    Compute shader executes (scattered writes to destination buffer)

Render Thread (SRP culling):
    BRG OnPerformCullingCallback()
        Iterate _batches.Values directly (no tracker query)
        Generate BatchDrawCommand[] + visibleInstances[] via UnsafeUtility.Malloc(TempJob)
    BRG issues draw calls (one per batch)

GPU (render):
    Draw instanced notes (one draw call per batch, N instances)
```

## GPU Memory

- **Single GraphicsBuffer** — all instance data for all notes across all players
- **Initial size:** 2 MB (~18,000 instances at 112 bytes each; ~36 batches at 500 capacity each)
- **HeapAllocator** manages batch regions within the buffer
- **SparseUploader intermediate buffer:** 16 MB chunk (compute shader path), ring of `NumFramesInFlight + 1` buffers

## Performance Characteristics

| Metric | GameObject path | BRG instanced path |
|--------|----------------|--------------------|
| Draw calls | 1 per note | 1 per batch (~3-30) |
| CreateSharedRendererScene | Per-note per frame | Zero (no GameObjects) |
| MonoBehaviour invokes | Per-note LateUpdate | Zero (no MonoBehaviours) |
| GPU upload | Automatic (Unity) | SparseUploader (1 compute dispatch or 1 SetData) |
| CPU allocations | High (per-note) | Low (NativeArrays, no per-frame managed alloc) |

## Shader Requirements

Note shaders must use `_BaseColor` (not `_Color`) as the DOTS instanced property name. Shader Graph assets have `m_OverrideReferenceName: "_BaseColor"` set on the color property block. All note theme materials have `m_EnableInstancingVariants: 1` enabled.

The shader receives per-instance:
- `unity_ObjectToWorld` — packed float3x4 (48 bytes)
- `unity_WorldToObject` — packed float3x4 (48 bytes)
- `_BaseColor` — float4 (16 bytes)

Emission properties are constant (set on the material), not per-instance.

## Thread Safety

- `AddUpload` runs on the main thread only (no Burst job parallelism)
- `OnPerformCullingCallback` runs on the SRP render thread — NO managed allocations (`UnsafeUtility.Malloc(Allocator.TempJob)` only)
- GPU buffer writes are ordered by the driver: compute dispatch completes before render pass reads

## Known Limitations

1. **No Burst job parallelism** — all AddUpload calls are main-thread. EGS uses ThreadedSparseUploader for job-parallel uploads.
2. **No per-instance emission** — emission is material-level constant. SP color updates and SP-activator pulse work via `NoteData.color` mutation (Tasks 10.1/10.2 implemented).
3. **Single GraphicsBuffer** — no dynamic growth mechanism. 2 MB initial size covers typical scenarios (~36 batches). If exhausted, `HeapAllocator.Allocate` returns empty block.
4. **No frustum culling** — all notes within the highway bounds are rendered (huge global bounds prevent BRG from culling anything).
5. **Sustain lines still use GameObjects** — only note heads are instanced. Sustain lines remain as GameObject-based `SustainLine` components (deferred to future change).
6. **Beatlines still use GameObjects** — beatline instancing is deferred. Beatlines remain as GameObject-based `BeatlineElement` components. The `HighwayElementGraphicsSystem` architecture supports adding beatlines without changes.
7. **Per-instrument scale implemented** (Task 10.3). `NoteSpawnData.scale` (Vector3) replaces `noteHeight` (float). Per-instrument `CreateNoteSpawnData` computes correct scale: Guitar/ProKeys use `S(1, noteHeight, 1)`, FiveLaneKeys uses `S(5/6, noteHeight*5/6, 1)` when not using open lane, Drums uses `S(NoteScaleFactor, noteHeight*NoteScaleFactor, NoteScaleFactor)` for non-kick/non-wildcard pads.
8. **SP mesh variant switching deferred** (Task 10.4). `UpdateBatchAssignments` is a no-op. SP notes use the mesh captured at spawn. SP *color* updates (Task 10.1) provide the primary visual feedback; SP *mesh* switching (different geometry) is secondary and deferred.
9. **SP-activator pulse implemented** (Task 10.2). Drums SP-activator notes pulse their color each frame based on `StrongBeat.CurrentPercentage` via `NoteTracker.PulseStarPowerActivators()`.
