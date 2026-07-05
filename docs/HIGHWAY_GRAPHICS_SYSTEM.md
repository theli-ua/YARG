# Highway Graphics System (Instanced Note Rendering)

This document describes the instanced rendering system for highway notes in YARG. It replaces the per-note GameObject/MeshRenderer approach with Unity's `BatchRendererGroup` (BRG) API and DOTS-style GPU instancing.

## Architecture Overview

```
TrackPlayer.GameplayUpdate()
    ├── NoteTracker.UpdatePositions()      — compute Z from visual time
    ├── NoteTracker.RemoveExpired()        — swap-remove notes past strike line
    ├── NoteTracker.UpdateBatchAssignments() — SP state change detection
    └── NoteTracker.UploadToGPU(trackLocalToWorld)
            ├── for each active note:
            │   └── HighwayElementGraphicsSystem.UploadInstance()
            │           ├── SparseUploader.AddUpload(objectToWorld)    — 48 bytes
            │           ├── SparseUploader.AddUpload(worldToObject)    — 48 bytes
            │           └── SparseUploader.AddUpload(baseColor)        — 16 bytes
            └── HighwayElementGraphicsSystem.UploadDirtyData()
                    └── SparseUploader.Commit()
                            ├── Compute shader path (default)
                            │   ├── LockBufferForWrite → write ops+data → UnlockBufferAfterWrite → Dispatch
                            └── Direct path (fallback)
                                └── scatter into NativeArray → single GraphicsBuffer.SetData

BRG Culling (render thread, each frame)
    └── OnPerformCullingCallback()
            └── for each batch with activeCount > 0:
                    └── generate BatchDrawCommand + visible instance indices
```

## Components

### HighwayElementGraphicsSystem

Central manager for instanced highway rendering. One instance per highway camera.

**Responsibilities:**
- Owns the `BatchRendererGroup` and its culling callback
- Owns the shared GPU `GraphicsBuffer` (all instance data lives here)
- Manages `HeapAllocator` for batch memory within the GPU buffer
- Manages `SparseUploader` for incremental GPU updates
- Registry of `ElementBatch` instances (mesh+material groups)
- Registry of `INoteTracker` instances (one per TrackPlayer)

**GPU Buffer Layout:**
```
Offset 0:      64 bytes of zeros (BRG safety zone — unset metadata reads from addr 0)
Offset 64+:    HeapAllocator-managed regions
    Each batch region:
        [objectToWorld: 48*N] [worldToObject: 48*N] [baseColor: 16*N]
        Total: 112 bytes per instance, N = batch capacity
```

**ElementBatch** — groups instances sharing the same mesh, material, and submesh. Each batch owns a contiguous region in the GPU buffer with Structure-of-Arrays (SoA) layout:
- `objectToWorldOffset` — byte offset to packed float3x4 array (48 bytes/instance)
- `worldToObjectOffset` — byte offset to packed float3x4 array (48 bytes/instance)
- `baseColorOffset` — byte offset to float4 array (16 bytes/instance)
- `activeCount` — number of visible instances (updated per-frame by UploadInstance)
- `meshLocalOffset` — per-mesh local transform (captured at theme extraction)

**BatchKey** — unique identifier for a batch: `(meshID, materialID, submeshIndex, sourceRendererID)`.

### SparseUploader

Uploads scattered data into the GPU GraphicsBuffer. Two paths:

#### Compute Shader Path (default)

Follows the Unity.Entities.Graphics (EGS) architecture:

1. **`LockBufferForWrite<byte>(0, chunkSize)`** — maps persistent intermediate buffer into CPU address space
2. **Write operations** at buffer start (grow forward), **write data** at buffer end (grow backward)
3. **`UnlockBufferAfterWrite<byte>(chunkSize)`** — flushes CPU writes, makes data visible to GPU
4. **`ComputeShader.Dispatch()`** — each thread group (64 threads) processes one upload operation

The compute shader (`Assets/Resources/SparseUploader.compute`) reads Operation structs + data from the intermediate buffer and scatters writes to the destination buffer. Supports operation types: Upload, Matrix_4x4, Matrix_Inverse_4x4, Matrix_3x4, Matrix_Inverse_3x4, StridedUpload.

**Buffer layout (intermediate):**
```
[Op0][Op1][Op2]...[padding]...[DataN][DataN-1]...
 ↑ ops grow forward        ↑ data grows backward
```

#### Direct Path (fallback)

Used when compute shader is unavailable (e.g., platform doesn't support it):

1. `AddUpload` — eagerly copies data into a growing CPU staging buffer, tracks dirty range `[minOffset, maxOffset)`
2. `Commit` — scatters staged data into a single `NativeArray<int>` covering the dirty range, calls `GraphicsBuffer.SetData` once

Reduces ~600 per-note SetData calls to 1 call per frame.

### NoteTracker

Per-TrackPlayer manager for active notes. Flat arrays, no per-note GameObjects.

**Data structures:**
- `NativeArray<NoteData> _notes` — per-note color/flag data (68 bytes each)
- `NativeArray<NoteSpawnData> _spawnData` — per-note spawn-time data (32 bytes each)
- `NoteBatchAssignment[][] _batchAssignments` — note index → batch assignments (3 per note: Colored/NoStarPower/Metal)
- `object[] _noteObjects` — chart note references for reverse lookup
- `Dictionary<object, int> _noteToIndex` — chart note → flat index for hit/miss

**NoteData** (68 bytes, blittable):
- `Vector4 color` — SP/miss-aware color for ColoredMaterials
- `Vector4 colorNoStarPower` — always non-SP fret color
- `Vector4 metalColor` — color for ColoredMetalMaterials
- `int highwayIndex` — from BasePlayer.HighwayIndex
- `float randomFloat` — random value [-1, 1]
- `Vector2 randomVector` — random 2D vector for theme variation
- `uint packedFlags` — bitfield: noteType (8 bits), isStarPower, isSustain, isOpenNote

**NoteSpawnData** (32 bytes, blittable):
- `float noteHitTime` — chart note's hit time (for Z position)
- `float baseX` — pre-computed X from GetElementX with lefty-flip
- `float noteHeight` — captured from HighwayPreset at spawn
- `ThemeNoteType noteType` — for render group lookup
- `bool isStarPowerVisible` — captured at spawn, updated on SP toggle

**Per-frame update cycle:**
1. `UpdatePositions()` — single loop, recompute Z = STRIKE_LINE_POS + (noteHitTime - visualTime) * noteSpeed
2. `RemoveExpired()` — backward iteration, swap-remove notes past strike line
3. `UpdateBatchAssignments()` — detect SP state changes, reassign notes between SP/non-SP batches
4. `UploadToGPU(trackLocalToWorld)` — compute world matrices, upload per-instance data

### PackedMatrix

Column-major 4×4 matrix packed into 12 floats (48 bytes). The w-row (0, 0, 0, 1) is dropped because DOTS instancing shaders expect a packed float3x4 and implicitly use (0, 0, 0, 1).

- `FromMatrix4x4(Matrix4x4)` — extracts 3 components per column, drops w
- `FromInverse(Matrix4x4)` — computes full inverse, then packs

### ThemeMeshCache

Static cache keyed by `(ThemeName, ThemeNoteType, StarPowerVariant)`. Extracted from theme prefabs at song load.

**RenderGroup** — `(Mesh, submeshIndex, Matrix4x4 meshLocalOffset)`. Three RenderGroups per note type (Colored/NoStarPower/Metal), each with its own material.

**Extraction** (`ExtractFromTheme`):
1. Instantiate theme prefab once
2. For each ThemeNote child: extract sharedMesh + sharedMaterials[materialIndex] for all three material arrays
3. Capture `meshLocalOffset = modelRootTransform.worldToLocalMatrix * childMesh.transform.localToWorldMatrix`
4. Store in cache, destroy instantiated GameObject
5. Materials are used directly (no cloning) — instancing already enabled on assets

### HeapAllocator

Ported from Unity.Entities.Graphics. Manages contiguous memory regions within the GPU buffer. Supports allocation, release, and coalescing of adjacent free blocks.

## Frame Timeline

```
Main Thread (Update phase):
    TrackPlayer.GameplayUpdate()
        NoteTracker.UpdatePositions()
        NoteTracker.RemoveExpired()
        NoteTracker.UpdateBatchAssignments()
        NoteTracker.UploadToGPU()
            SparseUploader.AddUpload() × 3 per note × 3 batches
            SparseUploader.Commit()
                LockBufferForWrite → write → UnlockBufferAfterWrite → Dispatch

GPU (asynchronous):
    Compute shader executes (scattered writes to destination buffer)

Render Thread (SRP culling):
    BRG OnPerformCullingCallback()
        Generate BatchDrawCommand[] + visible instance indices[]
    BRG issues draw calls (one per batch)

GPU (render):
    Draw instanced notes (one draw call per batch, N instances)
```

## GPU Memory

- **Single GraphicsBuffer** — all instance data for all notes across all players
- **Initial size:** 2 MB (~17,857 instances at 112 bytes each)
- **HeapAllocator** manages batch regions within the buffer
- **SparseUploader intermediate buffer:** 1 MB (compute shader path)

## Performance Characteristics

| Metric | GameObject path | BRG instanced path |
|--------|----------------|--------------------|
| Draw calls | 1 per note | 1 per batch (~3-30) |
| CreateSharedRendererScene | Per-note per frame | Zero (no GameObjects) |
| MonoBehaviour invokes | Per-note LateUpdate | Zero (no MonoBehaviours) |
| GPU upload | Automatic (Unity) | SparseUploader (1 compute dispatch or 1 SetData) |
| CPU allocations | High (per-note) | Low (NativeArrays, pooled) |

## Shader Requirements

Note shaders must use `_BaseColor` (not `_Color`) as the DOTS instanced property name. Shader Graph assets have `m_OverrideReferenceName: "_BaseColor"` set on the color property block. All note theme materials have `m_EnableInstancingVariants: 1` enabled.

The shader receives per-instance:
- `unity_ObjectToWorld` — packed float3x4 (48 bytes)
- `unity_WorldToObject` — packed float3x4 (48 bytes)
- `_BaseColor` — float4 (16 bytes)

Emission properties are constant (set on the material), not per-instance.

## Thread Safety

- `AddUpload` runs on the main thread only (no Burst job parallelism)
- `OnPerformCullingCallback` runs on the SRP render thread
- GPU buffer writes are ordered by the driver: compute dispatch completes before render pass reads

## Known Limitations

1. **No Burst job parallelism** — all AddUpload calls are main-thread. EGS uses ThreadedSparseUploader for job-parallel uploads.
2. **No per-instance emission** — emission is material-level constant. SP-activator pulse and dynamic emission changes don't work with BRG.
3. **Single GraphicsBuffer** — no dynamic growth mechanism. 2 MB initial size covers typical scenarios.
4. **No frustum culling** — all notes within the highway bounds are rendered (huge global bounds prevent BRG from culling anything).
5. **Sustain lines still use GameObjects** — only note heads are instanced.
