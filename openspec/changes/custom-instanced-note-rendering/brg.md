# BRG Custom Instanced Note Rendering — Status & Debug Log

**Change**: [custom-instanced-note-rendering](./)  
**Design**: [`design.md`](./design.md)  
**Tasks**: [`tasks.md`](./tasks.md)  
**Branch**: `renderi`

---

## Problem Statement

YARG renders gameplay notes using GameObject-based rendering: each note is a MonoBehaviour with Transform, MeshRenderer(s), and child GameObjects. This causes:

- **6,086 MonoBehaviour invokes** per frame across 29 types
- **`CreateSharedRendererScene` at 0.517ms median** — single largest CPU cost (97% children: PrepareSceneNodes, cull rebuilds)
- **N individual draw calls** per note (no GPU instancing)
- **Scene graph churn** on every spawn/return

**Goal**: Replace GameObject note rendering with Unity's `BatchRendererGroup` (BRG) API for massive performance improvement, following the same architecture as the existing highway camera rendering system.

---

## Architecture (from design.md)

Reference: `EntitiesGraphicsSystem` from `com.unity.entities.graphics` — adapted without ECS dependencies.

| Component | File | Purpose |
|-----------|------|---------|
| `HighwayElementGraphicsSystem` | `Assets/Script/Gameplay/Visuals/Instancing/HighwayElementGraphicsSystem.cs` | BRG wrapper: holds `BatchRendererGroup`, `GraphicsBuffer`, `HeapAllocator`, `SparseUploader`, batch registry |
| `NoteTracker` | `Assets/Script/Gameplay/Visuals/Instancing/NoteTracker.cs` | Per-track note lifecycle: NativeArray-backed active notes, swap-remove, chart→index lookup, GPU upload |
| `ThemeMeshCache` | `Assets/Script/Gameplay/Visuals/Instancing/ThemeMeshCache.cs` | Extracts Mesh/Material from theme prefab GameObjects at song start |
| `HeapAllocator` | `Assets/Script/Gameplay/Visuals/Instancing/HeapAllocator.cs` | Manages batch memory within shared GraphicsBuffer |
| `SparseUploader` | `Assets/Script/Gameplay/Visuals/Instancing/SparseUploader.cs` | Incremental GPU uploads (compute shader path + direct fallback) |
| `PackedMatrix` | `Assets/Script/Gameplay/Visuals/Instancing/PackedMatrix.cs` | Column-major float3x4 (48 bytes), w-row dropped |
| `NoteData` | `Assets/Script/Gameplay/Visuals/Instancing/NoteData.cs` | 68-byte blittable struct: colors, flags, randoms |
| `NoteSpawnData` | `Assets/Script/Gameplay/Visuals/Instancing/NoteSpawnData.cs` | 32-byte struct: hitTime, baseX, noteHeight, noteType, isSP |

### Data Flow
```
Chart note → TrackPlayer.SpawnNote → NoteTracker.Add()
  → ThemeMeshCache.GetRenderGroups() → HighwayElementGraphicsSystem.GetOrCreateBatch()
  → BRG.AddBatch(metadata, bufferHandle)
  → TrackPlayer.GameplayUpdate → NoteTracker.UploadToGPU()
  → SparseUploader → GraphicsBuffer.SetData()
  → BRG.OnPerformCulling → BatchDrawCommands → GPU
```

### SoA GPU Layout (112 bytes per instance)
```
Offset 0:     64 bytes zeros (BRG convention: addr 0 = zero matrix)
Offset 64:    objectToWorld array (48 bytes × capacity)
Offset 64+48N: worldToObject array (48 bytes × capacity)
Offset 64+96N: baseColor array (16 bytes × capacity)
```

---

## What We've Tried — Results

### ✅ Fixed / Working

| Fix | File | Result |
|-----|------|--------|
| BRG culling callback: pointer access, `drawCommandsType=Direct`, `AlignOf<long>()` alignment | `HighwayElementGraphicsSystem.cs` | **Fixed SIGSEGV crash** (was addr:0x54) |
| 64-byte zero prefix before instance data | `HighwayElementGraphicsSystem.cs` OnCreate | BRG convention for addr-0 reads |
| `BufferTarget` check (Raw vs Constant) | `HighwayElementGraphicsSystem.cs` | Correct buffer creation per API |
| `SetGlobalBounds()` call | `HighwayElementGraphicsSystem.cs` OnCreate | Required for culling |
| Heap allocator offset adjustment (+64 for prefix) | `HighwayElementGraphicsSystem.cs` | Correct buffer offsets |
| `CreateNoteSpawnData` sets `noteHitTime` from chart note | `TrackPlayer.cs` | Notes no longer expire instantly |
| Disabled compute shader path (buffer pointer bug) | `SparseUploader.cs` | Direct upload fallback works |
| Disposal guards in culling/upload | `HighwayElementGraphicsSystem.cs` | No shutdown crashes |
| `DOTS_INSTANCING_ON` shader with URP macros | `Assets/Art/Shaders/Gameplay/Notes/NoteBRGUnlit.shader` | DOTS-compatible shader created |
| Swap materials to DOTS shader | `ThemeMeshCache.cs` | Test shader active |
| Fix DOTS block name: Custom→MaterialPropertyMetadata | `NoteBRGUnlit.shader` | **Shader now reads per-instance _BaseColor** |
| Add property caching pattern from UnlitInput.hlsl | `NoteBRGUnlit.shader` | **Correct DOTS instancing access** |
| Expose BRG getter + assign to camera | `HighwayElementGraphicsSystem.cs`, `HighwayCameraRendering.cs` | **BRG committed to render camera** |

### 🟡 Partially Working

| Item | Status |
|------|--------|
| BRG culling callback fires | ✅ 8,277 calls during 15s benchmark |
| Batches created | ✅ 2 batches (colored + metal) |
| Notes spawn | ✅ activeCount 4-9 |
| Benchmark completes | ✅ 15s, 2156 frames, no crash |
| Draw commands in Frame Debugger | ❌ **ZERO draw commands visible** |
| Batches persist 100% of frames | ❌ Only 78% (22% show batches=0, but Dispose NOT called) |

### ❌ Not Working (Pre-Fix)

| Issue | Evidence | Status |
|-------|----------|--------|
| Notes not rendering | Frame Debugger shows zero draw commands | **FIXED?** — awaiting build verification |
| Batches=0 for 22% of frames | Unexplained — `Dispose()` never called, `_batches.Clear()` only in Dispose | **FIXED?** — awaiting build verification |

---

## Debug Timeline

### Phase 1: Crash Investigation
1. **SIGSEGV at addr:0x48/0x54** during BRG draw execution
2. Discovered compute shader path had buffer pointer bug → disabled it
3. Found culling callback used struct copy instead of pointer → fixed
4. Missing `drawCommandsType = Direct` → added
5. Wrong alignment (4 instead of `AlignOf<long>()`) → fixed
6. **Result**: Crash eliminated, benchmark completes

### Phase 2: Shader Compatibility
1. Shader Graph doesn't generate DOTS instancing variant → created `NoteBRGUnlit.shader`
2. Used `UniversalDOTSInstancing.hlsl` with `UNITY_DOTS_INSTANCED_PROP` macros
3. Swapped materials to use test shader
4. **Result**: Benchmark completes, but notes still not visible

### Phase 3: Pipeline Verification
1. Added diagnostic logs throughout pipeline
2. **Confirmed**: BRG culling callback fires 8,277 times
3. **Confirmed**: 2 batches created (colored + metal)
4. **Confirmed**: `GetRenderGroups()` returns valid data
5. **Confirmed**: `Dispose()` is NOT called during benchmark
6. **Confirmed**: Notes spawn (activeCount 4-9)
7. **Problem**: Frame Debugger shows ZERO draw commands

---

## Root Causes Found (2026-06-28)

### Root Cause 1: Shader uses invalid DOTS instancing block name

**File**: `Assets/Art/Shaders/Gameplay/Notes/NoteBRGUnlit.shader`

The shader used `CustomPropertyMetadata` as the DOTS instancing block name. Unity only recognizes three block names:
- `BuiltinPropertyMetadata` — for `unity_ObjectToWorld`, `unity_WorldToObject`
- `MaterialPropertyMetadata` — for material properties like `_BaseColor`
- `UserPropertyMetadata` — for custom user properties

**Fix**: Changed `CustomPropertyMetadata` → `MaterialPropertyMetadata`. Also added property caching pattern from URP's `UnlitInput.hlsl` reference, removed unnecessary `Lighting.hlsl` include and extra multi_compile pragmas.

### Root Cause 2: BRG never assigned to camera

**File**: `Assets/Script/Gameplay/Visuals/HighwayCameraRendering.cs`

The `BatchRendererGroup` was created but never assigned to `_renderCamera.batchRendererGroup`. While `SetEnabledViewTypes(Camera)` makes the culling callback fire, the draw commands may not be committed to the Frame Debugger or the correct render target without explicit camera association.

**Fix**: Added `_renderCamera.batchRendererGroup = _graphicsSystem.BatchRendererGroup` after `OnCreate()`. Exposed `BatchRendererGroup` getter in `HighwayElementGraphicsSystem`.

### Additional Issues Found (not yet fixed)

| Issue | File | Severity |
|-------|------|----------|
| `SparseUploader.AddUploadDirect` source offset always 0 | `SparseUploader.cs:249` | Low (latent) |
| Shader swap creates new material, may lose emission props | `ThemeMeshCache.cs:107-114` | Medium |
| BRG disposed on scene unload, gameplay loop may still run | `HighwayCameraRendering.cs:437-463` | High |

---

## Workflow & Delegation

### Research → scout / unity-scout
All investigation tasks delegated to specialized subagents:
- **scout**: Examine our codebase, find patterns, locate files, trace data flow
- **unity-scout**: Research Unity API, BRG internals, URP source code, documentation

### Implementation & Testing → grunt
All code changes and builds delegated to grunt subagents:
- Reads Player.log after each build to verify changes
- Uses benchmark automation (`-benchmark -duration N`) for testing
- Builds with Unity batch mode, runs player, checks logs
- Reports build status, crash status, benchmark completion, log evidence

### Benchmark Command
```bash
./autobuild -benchmark -duration 30 -screen-fullscreen 0 -screen-width 1280 -screen-height 720
```
Log location: `~/.config/unity3d/YARC/YARG/Player.log`

---

## Open Questions

1. Why do draw commands not appear in Frame Debugger despite being generated?
2. Why are batches=0 for 22% of frames when Dispose is never called?
3. Does the test shader actually read DOTS instancing data, or fall back to defaults?
4. Is BRG properly integrated with the highway camera's render pipeline?
5. Should we try the original Shader Graph materials (with `_BaseColor` rename) instead of the test shader?

---

## Next Steps

1. **VERIFY**: Build and run benchmark — check if draw commands appear in Frame Debugger
2. **IF STILL NOT RENDERING**: Check if `DOTS_INSTANCING_ON` shader variant is actually generated (material may need `enableInstancing = true`)
3. **IF STILL NOT RENDERING**: Check if highway camera's `targetTexture` prevents BRG rendering (BRG may render to main camera, not offscreen RT)
4. **IF RENDERING BUT WRONG**: Debug transform/color — verify packed matrices and _BaseColor values
5. **FIX**: Address remaining issues (SparseUploader offset, emission props, scene unload)
6. **VERIFY**: Full benchmark with all instruments
