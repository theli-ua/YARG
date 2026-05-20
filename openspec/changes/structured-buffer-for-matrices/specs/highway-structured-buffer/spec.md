## ADDED Requirements

### Requirement: Highway camera matrices stored in persistent static interleaved ComputeBuffer
The system SHALL use a single static `ComputeBuffer` storing all three camera matrix types (view, inverse view, projection) interleaved per highway: `[highway*3+0]=view, [highway*3+1]=invView, [highway*3+2]=proj`. Buffer SHALL be allocated once at first Awake() (null-guarded, static fields) and SHALL NOT be disposed during the application lifetime (~5.8 KB GPU, single instance, hardcoded 32 entries × 3 matrices).

#### Scenario: Buffers allocated on first Awake
- **WHEN** `HighwayCameraRendering` first calls `Awake()` (null-guard ensures single allocation)
- **THEN** static `ComputeBuffer`s are created with `ComputeBufferType.Default`, size 96 elements of `Matrix4x4` (32 highways × 3), bound to shader via `Shader.SetGlobalBuffer()`

#### Scenario: Buffers persist across scene transitions
- **WHEN** `HighwayCameraRendering` is disabled or scene unloaded
- **THEN** buffers are NOT disposed, remaining valid for shader use during UI rendering

### Requirement: Simplified dirty-entry updating (view-driven)
The system SHALL track dirty entries via a single `bool[32]` array. A view matrix change at any index marks ALL three matrix types (view, invView, proj) dirty at that index, since they are derived together.

#### Scenario: View change marks all matrices dirty
- **WHEN** a highway camera's view matrix changes at index `i`
- **THEN** dirty flag at `i` is set, causing all three buffers to upload at that index

#### Scenario: Single SetData uploads all 3 matrices per dirty highway
- **WHEN** highway `i` is dirty
- **THEN** one `ComputeBuffer.SetData(sourceArr, i*3, i*3, 3)` uploads all 3 matrices (view, invView, proj) for that highway

#### Scenario: Clean frame skips upload
- **WHEN** no entries are dirty
- **THEN** no `SetData` calls are made that frame

#### Scenario: Dirty flags cleared after upload
- **WHEN** dirty entries are uploaded to GPU
- **THEN** dirty flags are reset for next frame comparison

### Requirement: Curve factors stored in persistent ComputeBuffer
The system SHALL use a static `ComputeBuffer` to store highway curve factor data on the GPU as a `StructuredBuffer<float>`. This buffer is updated only when curve factors change (not every frame).

#### Scenario: Curve factors buffer allocated on first Awake
- **WHEN** `HighwayCameraRendering` first calls `Awake()` (null-guard ensures single allocation)
- **THEN** a static `ComputeBuffer` is created with 32 elements of `float` (4 bytes each), bound to shader via `Shader.SetGlobalBuffer()`

#### Scenario: Curve factor change uploads only changed index
- **WHEN** `UpdateCurveFactor()` is called for a specific index
- **THEN** only that index is uploaded via `ComputeBuffer.SetData()`, not the full array

#### Scenario: Curve factors persist across scene transitions
- **WHEN** scene is unloaded
- **THEN** curve factor buffer is NOT disposed

### Requirement: Fade params stored in persistent ComputeBuffer
The system SHALL use a static `ComputeBuffer` to store highway fade parameter data (fadeStart, fadeEnd per highway) on the GPU as a `StructuredBuffer<float>`. This buffer is updated only when fade params change.

#### Scenario: Fade params buffer allocated on first Awake
- **WHEN** `HighwayCameraRendering` first calls `Awake()` (null-guard ensures single allocation)
- **THEN** a static `ComputeBuffer` is created with 64 elements of `float` (32 highways × 2 params), bound to shader via `Shader.SetGlobalBuffer()`

#### Scenario: Fade param change uploads only changed indices
- **WHEN** `RecalculateFadeParams()` detects changed fade values at index `i`
- **THEN** only `s_fadeParamsBuffer.SetData(_fadeParams, i*2, i*2, 2)` for dirty indices, not the full array

#### Scenario: Fade params persist across scene transitions
- **WHEN** scene is unloaded
- **THEN** fade params buffer is NOT disposed

### Requirement: Shader-side StructuredBuffer consumption
The highway shader SHALL read camera matrices and curve factors from `StructuredBuffer` instead of `uniform` arrays. Matrices are accessed via inline helpers computing interleaved offsets.

#### Scenario: Matrix lookup via interleaved structured buffer
- **WHEN** `highways.hlsl` needs a matrix for a given highway index
- **THEN** it reads from single `StructuredBuffer<float4x4>` at `index * 3 + typeOffset` (0=view, 1=invView, 2=proj) via inline accessor

#### Scenario: Curve factors available as structured buffer
- **WHEN** any highway shader function needs curve factor for a given highway index
- **THEN** it reads from `StructuredBuffer<float>` at that index (declared in `highways.hlsl`)

#### Scenario: Fade params available as structured buffer in alpha mask shader
- **WHEN** `HighwaysAlphaMask.shader` needs fade start/end for a given highway index
- **THEN** it reads from `StructuredBuffer<float> _YargFadeParams` at `index*2` / `index*2+1` (declared locally in that shader, not in `highways.hlsl`)

### Requirement: No visual regression
The change SHALL produce identical visual output to the previous `SetGlobalMatrixArray` / `SetGlobalFloatArray` approach.

#### Scenario: Multiplayer highway rendering unchanged
- **WHEN** multiple highways are rendered with different camera transforms
- **THEN** each highway renders with correct perspective, position, and curve matching prior behavior

#### Scenario: Single highway fallback unchanged
- **WHEN** `_YargHighwaysN < 1` (no custom highway cameras)
- **THEN** default `UNITY_MATRIX_VP` path is used, identical to prior behavior
