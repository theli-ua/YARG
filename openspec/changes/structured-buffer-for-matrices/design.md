## Context

Current highway rendering uploads 4 arrays per update via `Shader.SetGlobalMatrixArray()` / `SetGlobalFloatArray()`:
- `_YargCamViewMatrices` (world-to-camera, 32 x Matrix4x4)
- `_YargCamInvViewMatrices` (camera-to-world, 32 x Matrix4x4)
- `_YargCamProjMatrices` (projection, 32 x Matrix4x4)
- `_YargCurveFactors` (curve radius, 32 x float)

Total: ~3.2 KB/update CPU->GPU. Most frames only 2-4 active highways, and matrices often don't change between frames (camera presets stable during gameplay). `highways.hlsl` consumes these as `uniform` arrays.

## Goals / Non-Goals

**Goals:**
- Eliminate redundant uploads when entries unchanged
- Reduce CPU->GPU bandwidth for highway camera state
- Maintain identical visual output (no behavioral change)
- Avoid disposal-related crashes during scene transitions

**Non-Goals:**
- Reducing MAX_MATRICES from 32
- Changing highway camera logic or layout
- Optimizing other global state (fade params)

## Decisions

### Static buffers, never disposed
Buffers are `static`, allocated in `Awake()` behind a null-guard (static ctor blocked by Unity — `ComputeBuffer` touches `SystemInfo.maxGraphicsBufferSize`), never disposed. Rationale: single instance of `HighwayCameraRendering`, hardcoded 32 entries = ~6.4 KB GPU total. Disposal during scene transitions caused UI rendering crashes (shaders still referencing disposed buffers). Negligible memory cost vs stability.

### StructuredBuffer vs persistent global array
StructuredBuffer (`ComputeBuffer` with `StructuredBuffer<float4x4>` shader side) chosen over keeping global arrays. Rationale: `ComputeBuffer.SetData(index, data)` supports partial updates at arbitrary indices. Global arrays require full upload each time.

### Single interleaved buffer for matrices, separate buffers for curve factors and fade params
All 3 matrices per highway always update together (view-driven dirty). Single `ComputeBuffer` with 96 `Matrix4x4` elements: `[i*3+0]=view, [i*3+1]=invView, [i*3+2]=proj`. One `SetData(src, i*3, i*3, 3)` per dirty highway replaces 3 separate calls. Curve factors and fade params each in their own `ComputeBuffer<float>` (different update frequencies, per-index dirty tracking).

### Dirty tracking: single bool array, view-driven
Single `bool[32]` tracking which highway indices need upload. View matrix is the only one that changes mid-gameplay (camera preset changes). When view changes at index `i`, all three matrix buffers are marked dirty at `i` and uploaded together. This is simpler than per-type tracking and correct (invView is inverse of view, proj is derived alongside view).

Curve factors use their own dirty logic: `UpdateCurveFactor()` uploads immediately for the changed index only (not frame-driven).

### Buffer lifecycle
`Awake()` with null-guard: allocate 3 buffers, bind to shader once via `Shader.SetGlobalBuffer()` (same property IDs as old globals). No disposal, no recreation, no rebinding. Matrix data, curve factors and fade params are resolution-independent.

## Risks / Trade-offs

[SRP Batcher incompatibility] -> StructuredBuffers from ComputeBuffer are not SRP Batcher compatible by default. Mitigation: highway shaders already use global state, SRP Batcher not a current concern for this path.

[Shader variant explosion] -> StructuredBuffer vs uniform array creates shader variant split. Mitigation: single code path migration (no `#if` guard), old uniform arrays removed entirely.

[GPU memory overhead] -> 1 × 96 × 64 + 1 × 32 × 4 = ~6 KB persistent GPU memory. Mitigation: negligible, allocated once, never freed.

[Static constructor blocked by Unity] -> `ComputeBuffer` ctor touches `SystemInfo.maxGraphicsBufferSize`, not allowed from MonoBehaviour static ctor. Mitigation: allocate in `Awake()` with null-guard on static field.
