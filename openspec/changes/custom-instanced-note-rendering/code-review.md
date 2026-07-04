# Code Review: Custom Instanced Note Rendering

**Reviewer:** Principal Engineer
**Scope:** All code in `Assets/Script/Gameplay/Visuals/Instancing/`, changes to Player classes, HighwayCameraRendering, shaders, materials
**Branch:** `rendering-overhaul-2026-take-3-with-notes` vs `rendering-overhaul-2026-take-3`
**Reference:** `EntitiesGraphicsSystem` from `com.unity.entities.graphics`
**Specs:** current implementation based on spec and tasks in openspec/changes/custom-instanced-note-rendering

---

## Executive Summary

Architecture is sound — BRG + shared GraphicsBuffer + HeapAllocator + SparseUploader matches the proven EntitiesGraphicsSystem pattern. However, the implementation has **14 critical issues** (GC allocations on hot paths, missing features, null-ref bugs), **15 high-priority issues** (performance, correctness), and **8 medium-priority issues** (code quality). The compute shader path in SparseUploader is disabled, negating the "sparse" upload benefit. Several designed features (SP-activator pulse, SP toggle color update, per-instrument scale) are not implemented.

**Estimated fix effort:** ~3-5 days for critical + high items.

---

## SEVERITY LEGEND

- **CRITICAL** — Will cause crashes, data corruption, or fails to meet performance goals. Must fix before production.
- **HIGH** — Significant performance degradation, visual correctness issue, or resource leak.
- **MEDIUM** — Code quality, maintainability, or edge-case correctness.
- **LOW** — Nice-to-have improvements.

---

## CRITICAL ISSUES

### C1. [DONE] SparseUploader compute shader path disabled — defeats the entire "sparse" upload purpose

**Fixed:** Reimplemented compute shader path following EGS architecture exactly:
- `LockBufferForWrite<byte>` gives CPU direct pointer to GPU buffer memory
- Operations + data written directly into GPU-mapped buffer (no CPU staging)
- `UnlockBufferAfterWrite<byte>` flushes and makes visible to GPU
- `Dispatch` (not Async) — GPU naturally orders unlock before dispatch
- Chunk size bumped from 256KB to 1MB

Root causes of original SIGSEGV: (1) shader never loaded, (2) null pointer init in Ensure*Space, (3) upload buffer disposed during GPU dispatch (buffer lifetime bug). All fixed.

**Fallback:** Direct path uses eager-copy staging + single SetData covering dirty range [minOffset, maxOffset).

### C2. OnPerformCulling allocates managed memory on render thread

**File:** `HighwayElementGraphicsSystem.cs:362-400`
```csharp
// Fallback: use [0, 1, 2, ...]
visibleInstances = new int[batch.activeCount];  // MANAGED ALLOCATION
```

**Problem:** `OnPerformCulling` runs on the SRP render thread. Allocating managed arrays (`new int[]`) here causes:
- GC pressure on the render thread
- Potential frame stalls if GC triggers during culling
- The fallback path allocates even when it shouldn't be needed

**Fix:**
1. Pre-allocate `visibleInstances` arrays per batch (store in `ElementBatch`)
2. Or use `UnsafeUtility.Malloc` with `Allocator.TempJob` (freed by BRG framework after callback)
3. Eliminate the fallback — if a batch has `activeCount > 0`, it MUST have visible instances from UploadToGPU

### C3. GetVisibleInstancesForBatch is O(n × m) — runs in culling callback

**File:** `NoteTracker.cs:168-183`
```csharp
internal int[] GetVisibleInstancesForBatch(HighwayElementGraphicsSystem.ElementBatch batch)
{
    int count = 0;
    for (int i = 0; i < _activeCount; i++)  // O(n)
    {
        var assignments = _batchAssignments[i];
        if (assignments == null) continue;
        for (int j = 0; j < assignments.Length; j++)  // O(m)
        {
            if (assignments[j].Batch == batch)
            {
                count++;
                break;
            }
        }
    }
    int[] indices = new int[count];  // MANAGED ALLOCATION
    for (int i = 0; i < count; i++) indices[i] = i;
    return indices;
}
```

**Problem:** Called once per batch per culling callback. With 200 active notes × 3 assignments × 10+ batches = 6000+ comparisons per frame, PLUS managed array allocation.

**Root cause:** The architecture requires the culling callback to ask each tracker "which instances are visible in this batch?" because batch active counts are managed by trackers, not the graphics system. This is an architectural mismatch.

**Fix (architectural):**
1. **Option A (recommended):** Track visible instance count per-batch directly in `UploadToGPU`. After UploadToGPU completes, each batch's `activeCount` reflects the actual uploaded count. The culling callback then generates visible instances as `[0, 1, 2, ..., activeCount-1]` WITHOUT asking trackers.
2. **Option B:** Store a `NativeList<int>` of visible instance indices per batch in the tracker, populated during UploadToGPU. The culling callback reads this pre-computed list.

**Specific tasks for Option A:**
1. In `UploadToGPU`, after uploading all instances for a batch, set `batch.activeCount = batchPosition[batchKey]`
2. In `OnPerformCulling`, for each batch with `activeCount > 0`, generate visible instances `[0, 1, ..., activeCount-1]` directly
3. Remove `GetVisibleInstancesForBatch` entirely
4. Remove the tracker iteration loop in `OnPerformCulling` — iterate `_batches.Values` directly

### C4. UploadToGPU allocates Dictionary<int, int> every frame

**File:** `NoteTracker.cs:226`
```csharp
var batchPosition = new System.Collections.Generic.Dictionary<int, int>();
```

**Problem:** Managed Dictionary allocation every frame in the hot update path. With multiple players, this is multiple allocations per frame.

**Fix:**
1. Replace with a pre-allocated `int[]` array indexed by `System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(batch)` — but hash codes aren't stable across AppDomain restarts
2. **Better:** Assign each batch a sequential integer ID at creation time. Use `int[] batchPositions` sized to max batch count, cleared each frame with `Array.Clear`
3. **Best:** Since UploadToGPU iterates notes sequentially and uploads to contiguous positions, track position per-batch using a small fixed-size array (max batches is bounded by render groups × color categories)

### C5. RemoveExpired allocates List<int> every frame

**File:** `NoteTracker.cs:204`
```csharp
var expired = new System.Collections.Generic.List<int>();
```

**Problem:** Managed List allocation every frame. For songs with many expired notes, this List grows and causes GC.

**Fix:**
1. Use a fixed-size `int[64]` stack array (covers typical expired notes per frame)
2. Or use `NativeList<int>(Allocator.Temp)` which has no managed allocation
3. Or iterate backward and remove inline without collecting indices first

### C6. NoteTracker.Add() allocates List<NoteBatchAssignment> every call

**File:** `NoteTracker.cs:91`
```csharp
var assignments = new List<NoteBatchAssignment>();
```

**Problem:** Every note spawn allocates a managed List. Dense charts spawn many notes per second.

**Fix:**
1. Pre-allocate a small fixed-size array `NoteBatchAssignment[8]` (most notes have 1-3 render groups)
2. Count total assignments first, then allocate exact-size array
3. Or use a pooled array system

### C7. HighwayCameraRendering.OnDisable null-ref in GameManager check

**File:** `HighwayCameraRendering.cs:439-442`
```csharp
if (_graphicsSystem != null)
{
    _graphicsSystem.Dispose();
    _graphicsSystem = null;  // <-- NULLIFIED HERE
}
if (GameManager.HighwayElementGraphicsSystemRef == _graphicsSystem)  // <-- COMPARED TO NULL
    GameManager.HighwayElementGraphicsSystemRef = null;
```

**Problem:** After `_graphicsSystem = null`, the comparison `== _graphicsSystem` always compares with `null`. So `GameManager.HighwayElementGraphicsSystemRef` is only cleared if it's already `null` — the opposite of intended behavior. The static reference leaks.

**Fix:**
```csharp
if (_graphicsSystem != null)
{
    _graphicsSystem.Dispose();
    if (GameManager.HighwayElementGraphicsSystemRef == _graphicsSystem)
        GameManager.HighwayElementGraphicsSystemRef = null;
    _graphicsSystem = null;  // Nullify AFTER checking
}
```

### C8. Missing per-instrument scale (drums NoteScaleFactor, FiveLaneKeys 5/6)

**File:** `NoteTracker.cs:235-240`
```csharp
Matrix4x4 noteLocal = Matrix4x4.TRS(
    new Vector3(spawn.baseX, 0f, z),
    Quaternion.identity,
    new Vector3(1f, scale, 1f)  // <-- Always (1, scale, 1)
);
```

**Problem:** The design specifies non-uniform scaling for drums and FiveLaneKeys:
- **Drums:** `S(NoteScaleFactor, noteHeight*NoteScaleFactor, NoteScaleFactor)` — conditionally skipped for kick notes without dedicated lanes and wildcard notes
- **FiveLaneKeys:** `S(5/6, 5/6, 1)` when `!UsingOpenLane` (replaces base scale entirely)

Current code always uses `S(1, noteHeight, 1)`. Drums notes will render at wrong size when lane count differs from baseline. FiveLaneKeys notes will render too large when not using open lane.

**Fix:**
1. Add `Vector3 scale` field to `NoteSpawnData` (replaces single `noteHeight` for scale)
2. In each instrument's `CreateNoteSpawnData`, compute the correct scale vector:
   - Guitar/ProKeys: `new Vector3(1f, noteHeight, 1f)`
   - FiveLaneKeys: `UsingOpenLane ? new Vector3(1f, noteHeight, 1f) : new Vector3(5f/6f, noteHeight*5f/6f, 1f)`
   - Drums: Conditional based on kick/wildcard/dedicated lanes
3. In `UploadToGPU`, use `spawn.scale` instead of computing scale from `spawn.noteHeight`

**Specific drums scale logic:**
```csharp
// In DrumsPlayer.CreateNoteSpawnData:
bool isKick = note.Pad == (int)FourLaneDrumPad.Kick || note.Pad == (int)FiveLaneDrumPad.Kick;
bool isWildcard = note.Pad == (int)FourLaneDrumPad.Wildcard || note.Pad == (int)FiveLaneDrumPad.Wildcard;
bool kickHasLane = NumberOfDedicatedKickLanes > 0;

Vector3 scale;
if ((isKick && !kickHasLane) || isWildcard)
{
    // Centered notes: no scale factor
    scale = new Vector3(1f, Player.HighwayPreset.NoteHeight, 1f);
}
else
{
    float sf = NoteScaleFactor;
    scale = new Vector3(sf, Player.HighwayPreset.NoteHeight * sf, sf);
}
```

### C9. Missing drums SP-activator pulse (Task 6.14)

**Problem:** Task 6.14 specifies: "For drums notes where `NoteRef.IsStarPowerActivator && Player.Engine.CanStarPowerActivate && !IsStarPowerActive`, recompute `color` each `GameplayUpdate` from `GameManager.BeatEventHandler.Visual.StrongBeat.CurrentPercentage` pulse."

This is not implemented. The pulse effect makes SP-activator notes pulse brighter on strong beats when SP is available but not active.

**Fix:**
1. In `TrackPlayer.GameplayUpdate()`, after `UpdatePositions()` and before `UploadToGPU()`, add drums-specific SP-activator pulse logic
2. Iterate active notes, find drums SP-activator notes, recompute color from beat pulse
3. Store the pulse color in `NoteData.color` so UploadToGPU picks it up

### C10. Missing star-power activation change hook (Task 6.13)

**Problem:** Task 6.13 specifies: "In `GameplayUpdate`, detect SP toggle. For each in-flight SP note (`NoteRef.IsStarPower`), recompute `color` (all instruments) and `metalColor` (guitar/drums/ProKeys only)."

This is not implemented. When SP activates/deactivates during gameplay, existing notes should update their colors. Currently, notes keep their spawn-time colors.

**Fix:**
1. Track `_wasStarPowerActive` in `TrackPlayer`
2. In `GameplayUpdate()`, detect SP state change: `if (IsStarPowerActive != _wasStarPowerActive)`
3. On SP activate: for each active note where `noteData.packedFlags` has `isStarPower` bit set, recompute `color` to SP color and `metalColor` to SP metal color
4. On SP deactivate: recompute back to normal colors
5. FiveLaneKeys exception: `metalColor` uses `NoteRef.IsStarPower` (constant, not dynamic) — don't update metalColor for FiveLaneKeys

### C11. UpdateBatchAssignments is a no-op placeholder

**File:** `NoteTracker.cs:217-220`
```csharp
public void UpdateBatchAssignments()
{
    // Phase 1: placeholder. SP state change detection and batch reassignment
    // will be implemented when ThemeMeshCache integration is complete (section 5).
}
```

**Problem:** ThemeMeshCache integration IS complete (ExtractTheme, GetRenderGroups work). This method should handle SP mesh variant changes — when SP activates, SP notes should switch from non-SP mesh batches to SP mesh batches.

**Fix:**
1. Iterate all active notes where `spawnData.isStarPowerVisible` differs from current batch assignment's SP state
2. For affected notes: remove from old batch, add to new batch (call ThemeMeshCache.GetRenderGroups with updated SP state)
3. Update `_batchAssignments` for the affected note
4. Mark affected GPU regions as dirty

### C12. GameManager.HighwayElementGraphicsSystemRef anti-pattern

**File:** `GameManager.cs:34`
```csharp
internal static HighwayElementGraphicsSystem HighwayElementGraphicsSystemRef { get; set; }
```

**Problem:** Holding a static strong reference to prevent GC is a code smell. The BRG should be kept alive through proper object lifecycle. `HighwayCameraRendering` owns the graphics system and lives for the duration of gameplay. If HCR is alive, the graphics system should be alive.

**Root cause:** Likely added because BRG was being GC'd during testing. The real fix is to ensure HCR isn't destroyed prematurely, not to add a static pin.

**Fix:**
1. Remove `GameManager.HighwayElementGraphicsSystemRef`
2. Investigate why BRG was being GC'd — likely HCR was being destroyed or the BRG wasn't being assigned to a camera
3. Ensure `HighwayCameraRendering` lifecycle matches the gameplay session

### C13. Theme extraction double-instantiates ThemeNote GameObjects

**File:** `TrackPlayer.cs:271-285`
```csharp
instance = GameObject.Instantiate(NotePool.Prefab);  // INSTANTIATE #1
foreach (var themeNote in instance.GetComponentsInChildren<ThemeNote>(true))
{
    // Store child GameObjects in dictionaries
}
ThemeMeshCache.ExtractTheme(themeName, themeModels, spModels);  // INSTANTIATE #2 (inside ExtractFromTheme)
GameObject.DestroyImmediate(instance);
```

**Problem:** `ExtractFromTheme` instantiates each ThemeNote GameObject again. So each ThemeNote is instantiated twice — once in TrackPlayer, once in ThemeMeshCache. The first instantiation is wasted.

**Fix:**
1. Pass the already-instantiated child GameObjects directly to ThemeMeshCache
2. Modify `ExtractFromTheme` to accept an already-instantiated GameObject instead of instantiating itself
3. Or: in TrackPlayer, don't instantiate — pass the prefab to ThemeMeshCache and let it handle instantiation

### C14. Debug.LogError used for informational messages in ThemeMeshCache

**File:** `ThemeMeshCache.cs:67,119,181`
```csharp
Debug.LogError($"[ThemeMeshCache] Extracted: theme='{themeName}'...");
Debug.LogError($"[ThemeMeshCache] GetRenderGroups called: theme='{themeName}'...");
Debug.LogError($"[ThemeMeshCache] Lookup miss: theme='{themeName}'...");
```

**Problem:** `Debug.LogError` floods the console with red error messages for normal operation. Should be `Debug.Log` or `Debug.LogWarning` at most. The persistent debug logging flags (`_hasLoggedFirstCall`, `_hasLoggedCacheMiss`) log once but still use Error level.

**Fix:**
1. Change all informational `Debug.LogError` to `Debug.Log`
2. Wrap diagnostic logging in `#if DEBUG` or remove entirely
3. Remove `_hasLoggedFirstCall` and `_hasLoggedCacheMiss` static flags — they persist across songs and themes

---

## HIGH ISSUES

### H1. HeapAllocator never grows the GraphicsBuffer

**File:** `HighwayElementGraphicsSystem.cs:163`
```csharp
private const int InitialBufferSize = 2 * 1024 * 1024; // 2MB initial
```

**Problem:** 2MB = ~17,857 instances (at 112 bytes each). With multiple themes, multiple players, and dense charts, this can be exhausted. When allocation fails, `GetOrCreateBatch` returns null and the note is silently not rendered.

**EntitiesGraphicsSystem comparison:** EGS has `kGPUBufferSizeMax = 1023 * 1024 * 1024` (1GiB potential) and dynamic buffer growth.

**Fix:**
1. Add buffer growth mechanism: when HeapAllocator allocation fails, double the buffer size (up to a max like 64MB)
2. Reallocation requires: create new GraphicsBuffer, copy existing data, update all batch offsets, re-register batches with BRG
3. **Simpler alternative:** Increase initial buffer to 16MB (covers ~142,857 instances — sufficient for any realistic scenario) and add a warning log when usage exceeds 80%

### H2. No profiler markers

**Problem:** No `Profiler.BeginSample`/`Profiler.EndSample` calls anywhere in the instancing code. Impossible to diagnose performance issues without adding instrumentation.

**EntitiesGraphicsSystem comparison:** EGS has profiler markers for every major operation (UpdateAllBatches, OnPerformCulling, CompleteJobs, etc.).

**Fix:** Add profiler markers:
- `Profiler.BeginSample("NoteTracker.UploadToGPU")` in UploadToGPU
- `Profiler.BeginSample("NoteTracker.RemoveExpired")` in RemoveExpired
- `Profiler.BeginSample("HighwayElementGraphicsSystem.OnPerformCulling")` in the culling callback
- `Profiler.BeginSample("SparseUploader.Commit")` in Commit

### H3. System.Linq imported but unused in HighwayElementGraphicsSystem

**File:** `HighwayElementGraphicsSystem.cs:3`
```csharp
using System.Linq;
```

**Problem:** Unused import. If Linq methods are called anywhere, they cause GC allocations (enumerator allocation).

**Fix:** Remove `using System.Linq;`

### H4. ElementBatch is a class (heap allocation) instead of struct

**File:** `HighwayElementGraphicsSystem.cs:62`
```csharp
internal class ElementBatch
```

**Problem:** Each batch is a heap allocation. The design specifies `struct ElementBatch`. While the comment says "class so mutations persist", Dictionary values of structs can be mutated by re-assignment. Using a class means GC pressure on batch creation.

**Fix:**
1. Convert `ElementBatch` to a `struct`
2. For mutations (activeCount updates), use `ref` access: `ref var batch = ref _batches[key]` (requires `ref` return or explicit re-assignment)
3. Or keep as class but document the trade-off explicitly

### H5. PackedMatrix.FromInverse computes full 4x4 inverse

**File:** `PackedMatrix.cs:54-68`
```csharp
public static PackedMatrix FromInverse(Matrix4x4 m)
{
    Matrix4x4 inv = m.inverse;  // Computes full 64-byte inverse
    // ... extracts 48 bytes
}
```

**Problem:** `Matrix4x4.inverse` computes a full 4×4 matrix inverse (including the w-row which is always `0, 0, 0, 1` for affine transforms). Since we know the transform is affine (no perspective), the w-row of the inverse is also `0, 0, 0, 1` and can be skipped.

**Fix:** Implement direct float3x4 inverse for affine transforms:
```csharp
public static PackedMatrix FromInverseAffine(Matrix4x4 m)
{
    // Invert the upper 3x3 rotation/scale matrix
    // Then compute translation: -R^-1 * t
    // Skip w-row entirely (always 0,0,0,1 for affine)
}
```
Saves ~25% of the inverse computation.

### H6. splitVisibilityMask calculation can be simplified

**File:** `HighwayElementGraphicsSystem.cs:378-382`
```csharp
ushort splitVisibilityMask;
if (visibleInstances.Length >= 16)
    splitVisibilityMask = 0xffff;
else
    splitVisibilityMask = (ushort)((1 << visibleInstances.Length) - 1);
```

**Problem:** Works correctly but the >= 16 check is defensive against shift overflow. With Option A from C3 (contiguous indices), this simplifies to:
```csharp
splitVisibilityMask = batch.activeCount >= 16 ? 0xffff : (ushort)((1 << batch.activeCount) - 1);
```

**Fix:** Apply after C3 fix. Low priority but clean.

### H7. Emission properties can't change per-frame with BRG

**Problem:** The design says "Constant properties (emission multiplier/addition, _EmissionColor, etc.) are set on the material at runtime." With BRG, material properties are baked into the `BatchMaterialID` at `RegisterMaterial()` time. Runtime property changes on the material do NOT propagate to existing BRG batches.

**Impact:**
- Drums SP-activator pulse (emission changes every frame) — won't work
- SP activation emission change — won't work for in-flight notes
- Any per-frame emission modulation — won't work

**Fix options:**
1. **Per-instance emission:** Add `_EmissionMultiplier` and `_EmissionColor` as per-instance DOTS properties (like `_BaseColor`). Requires shader changes.
2. **Material recreation:** Create new material with updated emission, register new material, create new batch. Expensive but works.
3. **Accept limitation:** Document that emission is set at spawn time and doesn't change. Remove drums SP-activator pulse from scope.

**Recommendation:** Option 1 for `_EmissionMultiplier` (single float per instance). The shader already has emission logic — just make it DOTS-instanced.

### H8. randomFloat/randomVector stored but never used

**File:** `NoteData.cs:28-34`
```csharp
public float randomFloat;
public Vector2 randomVector;
```

**Problem:** These fields are populated in `CreateNoteData` but never uploaded to GPU or used in shaders. They add 12 bytes per NoteData (68 → 80 bytes if we count the actual struct size).

**Fix:**
1. If theme shaders use `_RandomFloat` and `_RandomVector` as per-instance properties, add them to the DOTS instancing metadata
2. If not needed, remove from NoteData to save memory
3. Current theme shaders use these as material-level constants — per-instance random is not needed

### H9. GraphicsSettings stripping set to 2

**File:** `ProjectSettings/GraphicsSettings.asset`
```
m_InstancingStripping: 2
m_BrgStripping: 2
```

**Problem:** Value `2` means "strip unused variants in all build targets". This could strip DOTS instancing shader variants if the build system doesn't detect them as used. EntitiesGraphicsSystem relies on variants being present.

**Fix:**
1. Verify that DOTS instancing variants are NOT stripped in builds
2. If stripping occurs, set `m_BrgStripping: 0` (never strip) or `1` (strip only in development builds)
3. Test a full build and verify notes render correctly

### H10. NoteTracker._disposed not checked in most methods

**File:** `NoteTracker.cs`

**Problem:** `_disposed` is set in `Dispose()` but not checked in `Add()`, `Remove()`, `UploadToGPU()`, `RemoveExpired()`. Calling these after dispose causes NullReferenceException on disposed NativeArrays.

**Fix:** Add `_disposed` guard at the start of each public method:
```csharp
if (_disposed) return; // or throw ObjectDisposedException
```

### H11. NoteTracker.Remove() doesn't clear NativeArray slots

**File:** `NoteTracker.cs:190-215`

**Problem:** After swap-remove, the `last` slot in `_notes` and `_spawnData` is not cleared. The old data persists in the NativeArray. If a bug causes reading beyond `_activeCount`, stale data is visible.

**Fix:** Clear the last slot after swap-remove:
```csharp
_notes[last] = default;
_spawnData[last] = default;
```

### H12. UploadInstance validates offsets <= 0 but offset 0 is valid

**File:** `HighwayElementGraphicsSystem.cs:435`
```csharp
if (batch.objectToWorldOffset <= 0 || batch.worldToObjectOffset <= 0 || batch.baseColorOffset <= 0)
```

**Problem:** Offset 0 is the zero-matrix safety zone. Offsets are computed as `block.begin + ZeroMatrixSize`, so the minimum offset is 64 (not 0). The `<= 0` check is technically correct but misleading — should be `< ZeroMatrixSize` to catch offsets that fall within the zero zone.

**Fix:** Change to `< ZeroMatrixSize` or remove the check entirely (offsets are computed correctly in GetOrCreateBatch).

### H13. Note_FullHOPO.shadergraph not updated for _BaseColor

**Problem:** Task 5.1 mentions updating `Note_FullHOPO.shadergraph`, but this shader has no color property (both OverrideReferenceNames are empty). No materials reference this shader.

**Fix:**
1. Verify this shader is not used by any theme
2. If unused, remove it or add a comment marking it as deprecated
3. If used, add a `_BaseColor` property matching the other note shaders

### H14. _hasLoggedNullCamera and _hasLoggedGameplayUpdate diagnostic fields unused

**File:** `TrackPlayer.cs:188-189`
```csharp
private static bool _hasLoggedNullCamera;
private bool _hasLoggedGameplayUpdate;
```

**Problem:** These fields are declared but never used. Dead code.

**Fix:** Remove these fields.

### H15. HeapAllocator is near-verbatim copy without attribution

**File:** `HeapAllocator.cs`

**Problem:** This is essentially a copy of Unity's `HeapAllocator` from `com.unity.entities.graphics` with minimal changes. No attribution comment, no version reference. Creates maintenance burden — if Unity fixes bugs, we won't know.

**Fix:**
1. Add a comment at the top: "Ported from Unity.Entities.Graphics HeapAllocator (package version X.Y.Z). Modifications: [list changes]"
2. Consider referencing the package directly instead of copying (if licensing permits)

---

## MEDIUM ISSUES

### M1. SparseUploader.cs has ~200 lines of dead compute shader code

**File:** `SparseUploader.cs:100-200`

**Problem:** The compute shader path is disabled but the code remains. Dead code increases maintenance burden and confusion.

**Fix:**
1. If compute shader will be re-enabled: wrap in `#if USE_COMPUTE_SHADER` and document why it's disabled
2. If compute shader won't be re-enabled: remove the dead code entirely

### M2. NoteData.Size and NoteSpawnData.Size static properties unused

**File:** `NoteData.cs:43,101`

**Problem:** `public static readonly int Size = UnsafeUtility.SizeOf<T>()` is defined but never referenced. Dead code.

**Fix:** Remove or use in assertions (e.g., `Debug.Assert(NoteData.Size == 68)`).

### M3. BatchKey.GetHashCode() uses unchecked overflow

**File:** `HighwayElementGraphicsSystem.cs:114-122`

**Problem:** Hash code computation uses `unchecked` block with `hash * 31 + field`. This is standard practice but should be documented.

**Fix:** Add comment: `// Standard hash combination — overflow is intentional and safe`

### M4. ElementBatch.meshLocalOffset is Matrix4x4 (64 bytes) in a batch that's already a class

**File:** `HighwayElementGraphicsSystem.cs:74`

**Problem:** `Matrix4x4` is 64 bytes. If ElementBatch is converted to struct (H4 fix), this adds significant size. Consider whether the full Matrix4x4 is needed or if a packed matrix suffices.

**Fix:** Evaluate if `PackedMatrix` can replace `Matrix4x4` for `meshLocalOffset`. The offset is computed once at extraction time and used in every UploadToGPU call.

### M5. No validation that batch positions are contiguous after swap-remove

**Problem:** `Remove()` does swap-remove in CPU arrays and decrements batch active counts. `UploadToGPU` uses fresh per-batch positions. If a note is removed and a new note is added, the GPU region for the removed note's position is not cleared until the next upload overwrites it. This is acceptable (the next upload overwrites), but should be documented.

**Fix:** Add comment documenting the invariant: "GPU regions for removed instances contain stale data until overwritten by the next UploadToGPU call. This is safe because activeCount prevents rendering stale data."

### M6. DefaultBatchCapacity of 256 may be insufficient

**File:** `HighwayElementGraphicsSystem.cs:48`
```csharp
private const int DefaultBatchCapacity = 256;
```

**Problem:** 256 instances per batch. With 3 color categories per render group, that's 256 notes per render group. Dense charts with many notes of the same type could exceed this.

**Fix:**
1. Increase to 512 or 1024
2. Or make capacity dynamic based on pool size: `capacity = NotePool.ObjectCap`

### M7. No unit tests for PackedMatrix, HeapAllocator, SparseUploader

**Problem:** Core infrastructure has no automated tests. PackedMatrix should have tests for round-trip (Matrix4x4 → PackedMatrix → verify columns). HeapAllocator should test allocation, release, coalescing, fragmentation.

**Fix:** Add editor unit tests for:
- `PackedMatrix.FromMatrix4x4` / `FromInverse` — verify column values match expected
- `HeapAllocator` — test allocate/release/coalesce/fragmentation
- `SparseUploader` — test direct upload path (compute path is disabled)

### M8. RuntimeAutomation.cs is debug-only code

**File:** `RuntimeAutomation.cs`

**Problem:** 244 lines of automation/benchmarking code committed to the main branch. Should be behind a compile symbol or in a separate branch.

**Fix:** Wrap in `#if AUTOMATION` or move to a debug-only assembly.

---

## LOW ISSUES

### L1. No frustum culling for notes far behind camera

**Problem:** Notes are always rendered regardless of Z position. `RemoveExpired` removes notes with Z < -4, but notes between -4 and the camera's near plane are still rendered.

**Fix:** Add Z-based culling in `OnPerformCulling` or `UploadToGPU`. Skip notes with Z < cameraNearPlane.

### L2. NoteSpawnData struct size is 20 bytes, not 32 as designed

**File:** `NoteData.cs:79-101`

**Problem:** The design documents 32 bytes for NoteSpawnData, but the actual struct is ~20 bytes (3 floats + enum + bool + padding). Not a bug — the design was conservative. The `Size` static property returns the correct value.

**Fix:** Update design.md to reflect actual size (20 bytes).

### L3. NoteData struct padding/alignment

**File:** `NoteData.cs`

**Problem:** `NoteData` has `int highwayIndex` (4 bytes) between `Vector4 metalColor` (16 bytes) and `float randomFloat` (4 bytes). No padding needed — the layout is naturally aligned. Verify with `UnsafeUtility.SizeOf<NoteData>() == 68`.

**Fix:** No action needed. Add assertion: `Debug.Assert(NoteData.Size == 68)`.

---

## SPECIFIC TASKS (ordered by priority)

### Phase 1: Critical fixes (block production)

**Task 1.1: [DONE] Fix SparseUploader direct upload batching**
- Eagerly copy upload data into staging buffer at AddUpload time (like EGS compute path)
- Track dirty range [minOffset, maxOffset], single `GraphicsBuffer.SetData` covering the range
- File: `SparseUploader.cs`
- **Fixed:** Replaced per-call SetData (~600 calls/frame) with eager-copy staging buffer + single SetData. Data copied into `byte*` staging buffer at AddUpload time (no dangling pointer). CommitDirect scatters staged data into one NativeArray covering [minOffset, maxOffset) and calls SetData once. Verified with build + automation test (notes render correctly, FPS 144).

**Task 1.2: Eliminate managed allocations in OnPerformCulling**
- Implement C3 Option A: track visible count per-batch in UploadToGPU
- Generate visible instances `[0, 1, ..., activeCount-1]` directly in culling callback
- Remove `GetVisibleInstancesForBatch` method
- Use `UnsafeUtility.Malloc` for draw command arrays (already done)
- Files: `HighwayElementGraphicsSystem.cs`, `NoteTracker.cs`

**Task 1.3: Eliminate per-frame Dictionary allocation in UploadToGPU**
- Assign sequential integer IDs to batches at creation time
- Use pre-allocated `int[] batchPositions` array, cleared each frame
- File: `NoteTracker.cs`, `HighwayElementGraphicsSystem.cs`

**Task 1.4: Eliminate per-frame List allocation in RemoveExpired**
- Use fixed-size `int[64]` stack array or `NativeList<int>(Allocator.Temp)`
- File: `NoteTracker.cs`

**Task 1.5: Eliminate List allocation in NoteTracker.Add()**
- Pre-allocate `NoteBatchAssignment[8]` array (most notes have ≤3 render groups)
- Count assignments first, then allocate exact-size array
- File: `NoteTracker.cs`

**Task 1.6: [DONE] Fix HighwayCameraRendering.OnDisable null-ref**
- Move `GameManager.HighwayElementGraphicsSystemRef` check before `_graphicsSystem = null`
- File: `HighwayCameraRendering.cs`
- **Fixed:** Moved GameManager ref check inside the `if (_graphicsSystem != null)` block, before `_graphicsSystem = null`. Verified with build + automation test.

**Task 1.7: Implement per-instrument scale**
- Add `Vector3 scale` field to `NoteSpawnData`
- Compute correct scale in each instrument's `CreateNoteSpawnData`
- Use `spawn.scale` in `UploadToGPU` instead of `new Vector3(1f, scale, 1f)`
- Files: `NoteData.cs`, `NoteTracker.cs`, `DrumsPlayer.cs`, `FiveFretGuitarPlayer.cs`, `FiveLaneKeysPlayer.cs`, `ProKeysPlayer.cs`

**Task 1.8: Implement drums SP-activator pulse**
- In `TrackPlayer.GameplayUpdate()`, after `UpdatePositions()`, iterate active notes
- For drums SP-activator notes, recompute color from beat pulse
- File: `TrackPlayer.cs`, `NoteTracker.cs`

**Task 1.9: Implement SP activation change hook**
- Track `_wasStarPowerActive` in `TrackPlayer`
- On SP toggle, update colors for in-flight SP notes
- File: `TrackPlayer.cs`, `NoteTracker.cs`

**Task 1.10: Implement UpdateBatchAssignments**
- Detect SP state changes and reassign notes to correct mesh batches
- File: `NoteTracker.cs`

**Task 1.11: Remove GameManager.HighwayElementGraphicsSystemRef**
- Remove static reference from GameManager
- Fix root cause of BRG GC (ensure HCR lifecycle is correct)
- Files: `GameManager.cs`, `HighwayCameraRendering.cs`

**Task 1.12: Fix ThemeMeshCache double-instantiation**
- Pass already-instantiated GameObjects to ThemeMeshCache
- Modify `ExtractFromTheme` to accept pre-instantiated GameObject
- Files: `TrackPlayer.cs`, `ThemeMeshCache.cs`

**Task 1.13: Fix Debug.LogError → Debug.Log in ThemeMeshCache**
- Change all informational LogError to Log
- Remove persistent debug logging flags
- File: `ThemeMeshCache.cs`

### Phase 2: High-priority fixes

**Task 2.1: Add GraphicsBuffer growth mechanism or increase initial size**
- Increase `InitialBufferSize` to 16MB minimum
- Add warning log when HeapAllocator usage exceeds 80%
- File: `HighwayElementGraphicsSystem.cs`

**Task 2.2: Add profiler markers**
- Add `Profiler.BeginSample`/`Profiler.EndSample` to all major methods
- Files: `HighwayElementGraphicsSystem.cs`, `NoteTracker.cs`, `SparseUploader.cs`

**Task 2.3: Remove unused System.Linq import**
- File: `HighwayElementGraphicsSystem.cs`

**Task 2.4: Convert ElementBatch to struct (or document class trade-off)**
- File: `HighwayElementGraphicsSystem.cs`

**Task 2.5: Optimize PackedMatrix.FromInverse for affine transforms**
- Implement direct float3x4 inverse without computing w-row
- File: `PackedMatrix.cs`

**Task 2.6: Address emission property limitation**
- Decision: add per-instance emission OR accept limitation
- If per-instance: add `_EmissionMultiplier` to DOTS metadata + shader
- File: `HighwayElementGraphicsSystem.cs`, shader files

**Task 2.7: Remove or use randomFloat/randomVector**
- Decision: remove from NoteData OR add to DOTS metadata
- File: `NoteData.cs`, `HighwayElementGraphicsSystem.cs`

**Task 2.8: Verify GraphicsSettings stripping doesn't remove DOTS variants**
- Test full build with BRG rendering
- Adjust `m_BrgStripping` if needed
- File: `ProjectSettings/GraphicsSettings.asset`

**Task 2.9: Add _disposed guards to NoteTracker methods**
- File: `NoteTracker.cs`

**Task 2.10: Clear NativeArray slots after swap-remove**
- File: `NoteTracker.cs`

**Task 2.11: Fix UploadInstance offset validation**
- Change `<= 0` to `< ZeroMatrixSize` or remove check
- File: `HighwayElementGraphicsSystem.cs`

**Task 2.12: Address Note_FullHOPO.shadergraph**
- Verify unused OR add _BaseColor property
- File: `Note_FullHOPO.shadergraph`

**Task 2.13: Remove unused diagnostic fields**
- Remove `_hasLoggedNullCamera` and `_hasLoggedGameplayUpdate`
- File: `TrackPlayer.cs`

**Task 2.14: Add attribution comment to HeapAllocator**
- File: `HeapAllocator.cs`

### Phase 3: Medium-priority improvements

**Task 3.1: Clean up dead compute shader code in SparseUploader**
- Wrap in `#if` or remove entirely
- File: `SparseUploader.cs`

**Task 3.2: Remove or use NoteData.Size / NoteSpawnData.Size**
- Add size assertions or remove unused properties
- File: `NoteData.cs`

**Task 3.3: Document BatchKey.GetHashCode overflow**
- File: `HighwayElementGraphicsSystem.cs`

**Task 3.4: Evaluate PackedMatrix for meshLocalOffset**
- File: `HighwayElementGraphicsSystem.cs`

**Task 3.5: Document GPU stale data invariant**
- Add comment in NoteTracker.Remove() and UploadToGPU()
- File: `NoteTracker.cs`

**Task 3.6: Increase DefaultBatchCapacity**
- Change from 256 to 512 or make dynamic
- File: `HighwayElementGraphicsSystem.cs`

**Task 3.7: Add unit tests**
- PackedMatrix round-trip test
- HeapAllocator allocate/release/coalesce test
- SparseUploader direct upload test
- File: New test files in `Assets/Tests/`

**Task 3.8: Wrap RuntimeAutomation in compile symbol**
- File: `RuntimeAutomation.cs`

---

## ARCHITECTURAL OBSERVATIONS

### What works well
1. **BRG + shared GraphicsBuffer pattern** — Correct adaptation of EntitiesGraphicsSystem
2. **ThemeMeshCache** — Clean extraction and caching of mesh/material data
3. **PackedMatrix** — Correct float3x4 packing for DOTS instancing
4. **Batch lifecycle** — Lazy creation, GC for empty batches
5. **Three-batch-per-render-group** — Correctly reproduces the 3-category theme material system
6. **Shader Graph _BaseColor rename** — Correct for DOTS instancing compatibility
7. **Material instancing enabled** — All note materials have `m_EnableInstancingVariants: 1`

### Architectural concerns
1. **Tracker owns batch state, graphics system doesn't** — The culling callback must ask trackers for visible instances. This creates the O(n×m) iteration problem (C3). The graphics system should own batch active counts directly.
2. **No job system integration** — Everything runs on the main thread. EntitiesGraphicsSystem uses Burst-compiled jobs for culling, batch creation, and data upload. For Phase 1 this is acceptable, but should be on the roadmap.
3. **Single GraphicsBuffer for everything** — Correct pattern, but 2MB initial size is too small. No growth mechanism.
4. **SparseUploader without compute shader** — The name is misleading when it does direct SetData. Consider renaming to `DirectUploader` or fixing the compute shader.

---

## COMPARISON WITH ENTITIESGRAPHICSSYSTEM

| Aspect | EntitiesGraphicsSystem | YARG Implementation | Gap |
|--------|----------------------|-------------------|-----|
| Buffer growth | Dynamic (32MB → 1GB) | Fixed 2MB | **Gap** |
| Upload method | Compute shader (batched) | Direct SetData (per-call) | **Gap (critical)** |
| Culling | Burst jobs, parallel | Synchronous, main thread | Acceptable for Phase 1 |
| Visible instances | Pre-computed in jobs | O(n×m) iteration in callback | **Gap (critical)** |
| Allocator | HeapAllocator (same) | HeapAllocator (copy) | OK |
| Profiler markers | Extensive | None | **Gap** |
| GC allocations | Zero in hot paths | Multiple (List, Dict, arrays) | **Gap (critical)** |
| Disposal guards | Comprehensive | Partial | **Gap** |
| Per-instance properties | Dynamic (ComponentType-driven) | Fixed (OWT, WTO, _BaseColor) | Acceptable |

---

## VERIFICATION CHECKLIST

After fixes, verify:
- [ ] Zero GC allocations per frame in UploadToGPU, RemoveExpired, OnPerformCulling
- [ ] Single SetData call per frame (or compute shader dispatch)
- [ ] Notes render at correct size for all instruments (drums scale, FiveLaneKeys 5/6)
- [ ] Drums SP-activator pulse works (emission changes per frame)
- [ ] SP activation/deactivation updates in-flight note colors
- [ ] SP mesh variant switching works (notes use correct mesh when SP toggles)
- [ ] No null-ref in HighwayCameraRendering.OnDisable
- [ ] GraphicsBuffer doesn't exhaust (test with dense chart, multiple players)
- [ ] Profiler shows reasonable times for UploadToGPU, OnPerformCulling
- [ ] Full build includes DOTS instancing shader variants
- [ ] Steam Deck frame time improvement measurable
