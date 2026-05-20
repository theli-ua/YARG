## 1. ComputeBuffer infrastructure in HighwayCameraRendering

- [x] 1.1 Add three static `ComputeBuffer` fields (`s_cameraMatrixBuffer` — 96 Matrix4x4 interleaved, `s_curveFactorBuffer` — 32 floats, `s_fadeParamsBuffer` — 64 floats) and dirty tracking arrays (`s_dirtyMatrices`, `s_dirtyFade`)
- [x] 1.2 Allocate all buffers in `Awake()` behind null-guard (static ctor blocked by Unity) — never dispose
- [x] 1.3 Bind buffers to shader once via `Shader.SetGlobalBuffer(id, buffer)` in static constructor — single `_YargCamMatrices` ID replaces 3 separate IDs

## 2. Dirty-tracking update logic (view-driven, interleaved)

- [x] 2.1 Collapse 3 staging arrays (`_camViewMatrices`, `_camInvViewMatrices`, `_camProjMatrices`) into single `_camMatrices[MAX_MATRICES * 3]` with `MAT_VIEW/MAT_INV_VIEW/MAT_PROJ` offset constants
- [x] 2.2 Simplify `UploadDirtyBuffers()`: single `SetData(_camMatrices, i*3, i*3, 3)` per dirty highway uploads all 3 matrices. Clear flags after.
- [x] 2.3 In `OnPreCameraRender`: compare only view. If different → update `_camMatrices[i*3+VIEW]` and `[i*3+INV_VIEW]`, mark `s_dirtyMatrices[i] = true`, call `UploadDirtyBuffers()`
- [x] 2.4 In `UpdateCameraProjectionMatrices`: compare only view. Mark dirty if changed. Always update proj staging. Call `UploadDirtyBuffers()`
- [x] 2.5 Remove old `Shader.SetGlobalMatrixArray()` calls entirely
- [x] 2.6 Update all other `_camViewMatrices[i]` access sites (WorldToViewport, GetTrackPositionScreenSpace, GetTrackBoundsScreenSpace) to use `_camMatrices[i*3+MAT_VIEW]`

## 3. Curve factor buffer

- [x] 3.1 In `UpdateCurveFactor()`: compare new value vs existing `_curveFactors[index]`. If different → update staging array, `s_curveFactorBuffer.SetData(_curveFactors, index, index, 1)`
- [x] 3.2 Remove old `Shader.SetGlobalFloatArray(YargCurveFactorsID, ...)` call

## 4. Fade params buffer

- [x] 4.1 In `RecalculateFadeParams()`: compare new fade start/end vs existing `_fadeParams[index*2]` / `_fadeParams[index*2+1]`. If different → mark `s_dirtyFade[index] = true`
- [x] 4.2 Upload dirty fade entries: `s_fadeParamsBuffer.SetData(_fadeParams, i*2, i*2, 2)` per dirty index. Clear `s_dirtyFade` after. Skip entirely if no dirty entries.
- [x] 4.3 Remove old `Shader.SetGlobalFloatArray(YargFadeParamsID, ...)` call

## 5. Shader-side migration (highways.hlsl + HighwaysAlphaMask.shader)

- [x] 5.1 Replace 3 `StructuredBuffer<float4x4>` with single `StructuredBuffer<float4x4> _YargCamMatrices` (96 elements, interleaved)
- [x] 5.2 Add inline accessors: `YargGetViewMatrix(i)`, `YargGetInvViewMatrix(i)`, `YargGetProjMatrix(i)` using `i*3+offset`
- [x] 5.3 Change `uniform float _YargCurveFactors[MAX_MATRICES]` to `StructuredBuffer<float> _YargCurveFactors`
- [x] 5.4 Change `uniform float _YargFadeParams[MAX_MATRICES * 2]` to `StructuredBuffer<float> _YargFadeParams` (access `[index*2]` syntax unchanged)
- [x] 5.5 Verify no other `.hlsl`/`.shader` files reference old uniform names

## 6. Verify shader binding

- [x] 6.1 Confirm all structured buffer names match `Shader.PropertyToID()` used in `SetGlobalBuffer()`
- [ ] 6.2 Verify in frame capture that all 3 structured buffers are bound and data read correctly

## 7. Cleanup and verification

- [x] 7.1 Keep existing `Shader.PropertyToID()` constants (consolidated to `YargHighwayCamMatricesID`) — reused for `SetGlobalBuffer()` binding
- [x] 7.2 Keep `_camMatrices`, `_curveFactors`, `_fadeParams` staging arrays — serve as "last uploaded" baseline for dirty comparison
- [ ] 7.3 Verify visual parity: multi-highway scene renders identical to before
- [ ] 7.4 Profile: confirm `SetGlobalMatrixArray` / `SetGlobalFloatArray` calls eliminated from frame capture
