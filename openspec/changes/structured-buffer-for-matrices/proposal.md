## Why

`SetGlobalMatrixArray()` called 3× per highway frame (up to 32 matrices each = ~3KB/frame CPU→GPU upload). When camera presets are stable, most of this upload is redundant — matrices don't change frame-to-frame. Persistent structured buffers with dirty-entry updates cut unnecessary bandwidth.

## What Changes

- Replace 3 `Shader.SetGlobalMatrixArray()` calls with persistent `ComputeBuffer` instances (structured buffers)
- Update only dirty matrix entries instead of full array each frame
- Shader-side: change `uniform float4x4 _YargCamViewMatrices[MAX_MATRICES]` to `StructuredBuffer<float4x4>` semantics
- Track per-highway dirty state (view/invView/proj independently)

## Capabilities

### New Capabilities
- `highway-structured-buffer`: Persistent ComputeBuffer for highway camera matrices with dirty-entry updating

### Modified Capabilities
- (none — no existing spec-level behavior changes)

## Impact

- `Assets/Script/Gameplay/Visuals/HighwayCameraRendering.cs` — ComputeBuffer lifecycle, dirty tracking, update logic
- `Assets/Art/Shaders/highways.hlsl` — uniform arrays → StructuredBuffer
- Any other shaders referencing `_YargCamViewMatrices`, `_YargCamInvViewMatrices`, `_YargCamProjMatrices` (search needed)
- `Shader.SetGlobalMatrixArray()` calls removed → `ComputeBuffer.SetData()` on dirty indices only
