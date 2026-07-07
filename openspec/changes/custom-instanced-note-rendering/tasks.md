## Status Legend
- `[x]` DONE — implemented and verified
- `[~]` PARTIAL — implemented but has known gaps (see notes)
- `[ ]` TODO — not yet implemented

---

## 0. Phase-0 spike (gate for Phase 2/3) — DONE

- [x] 0.1 **BRG + highway camera integration spike**: Created minimal `BatchRendererGroup` with a single batch. Verified it renders to the highway camera's `targetTexture` and depth-tests against existing highway geometry.
- [x] 0.2 **Shader Graph `_BaseColor` DOTS instancing spike**: Renamed color property override reference from `_Color` to `_BaseColor` in `RectangularNote.shadergraph`. Verified 3+ instances render with correct per-instance colors via BRG metadata.
- [x] 0.3 **PackedMatrix transform spike**: Verified packed `float3x4` matrices produce correct world transforms.
- [x] 0.4 **SparseUploader extraction spike**: Extracted `SparseUploader` and `HeapAllocator` from `EntitiesGraphicsSystem`. Verified they compile and work in a non-ECS context.

---

## 1. Core data structures — DONE

- [x] 1.1 Create `Assets/Script/Gameplay/Visuals/Instancing/NoteData.cs` — `NoteData` struct (68 bytes): `color`, `colorNoStarPower`, `metalColor` (Vector4 ×3), `highwayIndex` (int), `randomFloat` (float), `randomVector` (Vector2), `packedFlags` (uint). Blittable, `[StructLayout(LayoutKind.Sequential)]`. Includes `PackFlags`/`GetNoteType`/etc helpers.
- [x] 1.2 Create `NoteSpawnData` struct in same file — `noteHitTime` (float), `baseX` (float), `noteHeight` (float), `noteType` (ThemeNoteType), `isStarPowerVisible` (bool). Blittable, `[StructLayout(LayoutKind.Sequential)]`.
- [x] 1.3 `packedFlags` bitfield documented in XML comments: bits 0-7 = `noteType`, bit 8 = `isStarPower`, bit 9 = `isSustain`, bit 10 = `isOpenNote`, bits 11-31 = reserved.
- [x] 1.4 Create `Assets/Script/Gameplay/Visuals/Instancing/PackedMatrix.cs` — 48-byte packed float3x4. `FromMatrix4x4` drops w row. `FromInverse` computes `m.inverse` then drops w row.

---

## 2. HighwayElementGraphicsSystem — DONE

- [x] 2.1 Create `Assets/Script/Gameplay/Visuals/Instancing/HighwayElementGraphicsSystem.cs` with `BatchRendererGroup`, shared `GraphicsBuffer`, `HeapAllocator`, `SparseUploader`, `Dictionary<BatchKey, ElementBatch>`, `List<INoteTracker>`.
- [x] 2.2 `ElementBatch` class (not struct — mutations via `UploadInstance` persist): `batchID`, `meshID`, `materialID`, `submeshIndex`, `gpuAllocation`, `capacity`, `activeCount`, `objectToWorldOffset`, `worldToObjectOffset`, `baseColorOffset`, `meshLocalOffset`.
- [x] 2.3 `BatchKey` struct (IEquatable): `meshID`, `materialID`, `submeshIndex`, `sourceRendererID`.
- [x] 2.4 `OnCreate()`: allocates GraphicsBuffer (2MB Raw), writes 64-byte zero prefix, initializes HeapAllocator, SparseUploader, BRG with `OnPerformCulling` callback, sets global bounds, enables `BatchCullingViewType.Camera`.
- [x] 2.5 `GetOrCreateBatch(mesh, material, submesh, sourceRendererID, capacity, meshLocalOffset)`: registers mesh/material with BRG, allocates GPU via HeapAllocator, computes SoA offsets, builds metadata (OWT/WTO/_BaseColor with MSB), calls `BRG.AddBatch`.
- [x] 2.6 `RemoveBatch(BatchKey)`: releases GPU memory, `BRG.RemoveBatch`, removes from registry.
- [x] 2.7 `GarbageCollectEmptyBatches()`: removes batches with `activeCount == 0`.
- [x] 2.8 `UploadDirtyData(JobHandle)`: calls `SparseUploader.Commit()`.
- [x] 2.9 `OnPerformCulling`: iterates `_batches.Values` directly (not trackers), generates `BatchDrawCommand` per active batch with `visibleCount = batch.activeCount`, fills `visibleInstances = [0..activeCount-1]`, single `BatchDrawRange`. Uses `UnsafeUtility.Malloc(Allocator.TempJob)` for arrays.
- [x] 2.10 `Dispose()`: disposes BRG, GraphicsBuffer, HeapAllocator, SparseUploader, clears registries.
- [x] 2.11 `BeginUploadFrame()`: resets ALL batches' `activeCount = 0` once per frame (idempotent via `Time.frameCount`). Called by first tracker's `UploadToGPU`.
- [x] 2.12 `UploadInstance(batch, instanceIndex, objectToWorld, baseColor)`: validates offsets/capacity, writes packed OWT + WTO + color via SparseUploader, bumps `activeCount` to cover `instanceIndex`.
- [x] 2.13 `BatchIndexUpperBound` property and `ElementBatch.batchIndex` field — sequential int assigned at creation (currently set but unused after shared-batch fix; retained for debugging).

---

## 3. NoteTracker with GPU-aware updates — DONE (with known gaps)

- [x] 3.1 `NoteTracker` class: `NativeArray<NoteData> _notes`, `NativeArray<NoteSpawnData> _spawnData`, `Dictionary<object, int> _noteToIndex`, `object[] _noteObjects`, `NoteBatchAssignment[][] _batchAssignments`. Constructor takes `(capacity, themeName, highwayIndex, graphicsSystem, trackPlayer, gameManager)`.
- [x] 3.2 `Add(NoteData, NoteSpawnData, object)`: appends to arrays, looks up render groups via `ThemeMeshCache.GetRenderGroups`, creates batch assignments for all 3 categories, returns flat index. Does NOT touch `batch.activeCount`.
- [x] 3.3 `Remove(flatIndex)`: swap-removes from all arrays, fixes reverse-lookup. Does NOT touch `batch.activeCount` (owned by per-frame uploads).
- [x] 3.4 `UpdatePositions()`: no-op (Z computed in UploadToGPU).
- [x] 3.5 `RemoveExpired()`: backward iteration, swap-remove notes with `z < -4`. No managed allocation (no `List<int>`).
- [x] 3.6 `UpdateBatchAssignments()`: no-op placeholder (SP mesh switching deferred).
- [x] 3.7 `UploadToGPU(trackLocalToWorld)`: calls `BeginUploadFrame`, iterates active notes, computes `worldMatrix = trackLocalToWorld × T(baseX,0,z) × S(1,scale,1) × batch.meshLocalOffset`, writes to `batch.activeCount` slot (shared-batch append), flushes via `UploadDirtyData`.
- [x] 3.8 `Reset()`: clears arrays and mappings.
- [x] 3.9 `Dispose()`: disposes NativeArrays, unregisters from graphics system.
- [x] 3.10 `TryRemoveByNote(object)`: reverse-lookup + `Remove`.
- [x] 3.11 `GetIndex(object)` / `GetData(object)` / `SetColor(object, color)`: reverse-lookup helpers for hit/miss.

---

## 4. Chart-note → Tracker-index reverse lookup — DONE

- [x] 4.1 `Dictionary<object, int>` wired into `Add()` / `Remove()`.
- [x] 4.2 `GetIndex(object)` returns flat index or -1.
- [x] 4.3 Hit → `TryRemoveByNote(chartNote)`. Miss → `TryRemoveByNote(chartNote)` (uniform removal, no color mutation needed since note is removed).

---

## 5. Shader Graph updates and theme mesh/material extraction — DONE

- [x] 5.1 Renamed color property override reference from `_Color` to `_BaseColor` in `RectangularNote.shadergraph`, `CircularTapNote.shadergraph`, `Note_FullHOPO.shadergraph`.
- [x] 5.2 Set `m_EnableInstancingVariants: 1` on all note theme materials in the repo.
- [x] 5.3 `ThemeMeshCache.cs`: static cache keyed by `(ThemeName, ThemeNoteType, StarPowerVariant)`. `RenderGroup` struct: `(Mesh, SubmeshIndex, Material, MeshLocalOffset, SourceRendererID)`. `ThemeRenderData`: 3 arrays (Colored/NoStarPower/Metal).
- [x] 5.4 `ExtractFromTheme(themeName, themeModel, noteType)`: instantiates prefab once, iterates all 3 material categories, extracts `sharedMesh` + `sharedMaterials[materialIndex]` + `meshLocalOffset`, stores in cache, destroys GameObject.
- [x] 5.5 `GetRenderGroups(themeName, noteType, isStarPowerVisible)`: returns render groups, falls back to non-SP if SP variant absent, falls back to Wildcard if type absent.
- [x] 5.6 `ExtractTheme(themeName, models, starPowerModels)` called from `TrackPlayer.InitializeNoteTracker` after theme prefabs resolved.
- [x] 5.7 `ClearTheme(themeName)` / `ClearAll()` for theme changes.

---

## 6. TrackPlayer integration — DONE (with known gaps)

- [x] 6.1 `NoteTracker` field on `TrackPlayer`. Initialized in `InitializeNoteTracker` with `NotePool.ObjectCap`. Registered with `HighwayCameraRendering.RegisterNoteTracker`. Unregistered on cleanup.
- [x] 6.2 `SpawnNote()` modified: calls `NoteTracker.Add(noteData, spawnData, note)` always. GameObject head spawned only if `dualRenderMode` is true.
- [x] 6.3 `CreateNoteData(TNote)` virtual method — override in each instrument player. Resolves all three colors from `ColorProfile`.
- [x] 6.4 `CreateNoteSpawnData(TNote)` virtual method — override in each instrument player. Computes `baseX`, `noteHeight`, `noteType`, `isStarPowerVisible`.
- [x] 6.5 `highwayIndex` from `BasePlayer.HighwayIndex`. `randomFloat`/`randomVector` from `UnityEngine.Random`.
- [x] 6.6 Hit/miss: `OnNoteHit` → `NoteTracker.TryRemoveByNote(note)` for non-sustain. `OnNoteMissed` → `NoteTracker.TryRemoveByNote(note)`.
- [x] 6.7 `NoteTracker.UpdatePositions()` in `GameplayUpdate`.
- [x] 6.8 `NoteTracker.RemoveExpired()` in `GameplayUpdate`.
- [x] 6.9 `NoteTracker.UpdateBatchAssignments()` in `GameplayUpdate` (no-op placeholder).
- [x] 6.10 `NoteTracker.UploadToGPU(transform.localToWorldMatrix)` in `GameplayUpdate`.
- [x] 6.11 `NoteTracker.Reset()` in `TrackPlayer.ResetVisuals()`.
- [x] 6.12 `NoteTracker.Dispose()` in track cleanup.
- [x] 6.13 Per-instrument `CreateNoteData` / `CreateNoteSpawnData` overrides: `FiveFretGuitarPlayer`, `DrumsPlayer`, `FiveLaneKeysPlayer`, `ProKeysPlayer`.
- [~] 6.14 **SP activation change hook** — NOT IMPLEMENTED. `_wasStarPowerActive` field exists on `TrackPlayer` (line 189) but is not used to update in-flight note colors. See Task 10.1.
- [~] 6.15 **Drums SP-activator pulse** — NOT IMPLEMENTED. See Task 10.2.
- [~] 6.16 **Per-instrument non-uniform scale** — NOT IMPLEMENTED. `UploadToGPU` always uses `S(1, noteHeight, 1)`. Drums `NoteScaleFactor` and FiveLaneKeys `5/6` scale missing. See Task 10.3.

---

## 7. HighwayCameraRendering integration — DONE

- [x] 7.1 `HighwayElementGraphicsSystem` field on `HighwayCameraRendering`. Instantiated in `OnEnable`.
- [x] 7.2 `RegisterNoteTracker` / `UnregisterNoteTracker` methods.
- [x] 7.3 `dualRenderMode` bool (production default: false).
- [x] 7.4 `Dispose` in `OnDisable` (with `GameManager.HighwayElementGraphicsSystemRef` cleanup before nulling).
- [x] 7.5 `GraphicsSystem` property exposed for `TrackPlayer` theme extraction access.

---

## 8. Decommission GameObject note system — PARTIAL

- [x] 8.1 `dualRenderMode = false` by default (production).
- [ ] 8.2 Document that GameObject notes remain for sustain lines and beatlines (deferred).
- [x] 8.3 Clean up unused theme model GameObject references after full migration.
- [x] 8.4 Remove `TrackElement.LeftyFlipMultiplier` and `TrackElement.LeftyFlip` (dead code — zero call sites, lane-remapping approach makes them misleading).

---

## 9. Code cleanup and hardening — TODO

These tasks address issues identified during code review. They are ordered by priority.

### 9.1 Remove `GameManager.HighwayElementGraphicsSystemRef` static reference
**File:** `Assets/Script/Gameplay/GameManager.cs` (line 42), `Assets/Script/Gameplay/Visuals/HighwayCameraRendering.cs` (lines 130, 442-447)
**Problem:** A static reference to `HighwayElementGraphicsSystem` on `GameManager` is set in `HighwayCameraRendering.OnEnable` and cleared in `OnDisable`. This is an unnecessary global — `HighwayCameraRendering` already owns the instance. The static ref risks leaking if `OnDisable` ordering is wrong.
**Action:**
1. Delete `internal static HighwayElementGraphicsSystem HighwayElementGraphicsSystemRef { get; set; }` from `GameManager.cs`
2. Delete the `GameManager.HighwayElementGraphicsSystemRef = _graphicsSystem;` line in `HighwayCameraRendering.OnEnable`
3. Delete the `if (GameManager.HighwayElementGraphicsSystemRef == _graphicsSystem) ...` block in `HighwayCameraRendering.OnDisable`
4. Search for any other references to `HighwayElementGraphicsSystemRef` and remove them

### 9.2 Remove debug logging from `ThemeMeshCache`
**File:** `Assets/Script/Gameplay/Visuals/Instancing/ThemeMeshCache.cs`
**Problem:** Uses `Debug.LogError` for informational messages (lines in `ExtractFromTheme`, `GetRenderGroups`). `LogError` is wrong severity for info messages and pollutes the console. Persistent `_hasLoggedFirstCall` / `_hasLoggedCacheMiss` flags are debug-only state.
**Action:**
1. Change all `Debug.LogError` in `ThemeMeshCache.cs` to `Debug.Log` (or remove them — they're extraction diagnostics)
2. Remove `_hasLoggedFirstCall` and `_hasLoggedCacheMiss` fields and their usage
3. Remove the commented-out DOTS test shader swap code in `ExtractGroupsFromEntries`

### 9.3 Remove `ThemeMeshCache` double-instantiation
**File:** `Assets/Script/Gameplay/Visuals/Instancing/ThemeMeshCache.cs` (`ExtractFromTheme`), `Assets/Script/Gameplay/Player/TrackPlayer.cs` (`InitializeNoteTracker`)
**Problem:** `ExtractFromTheme` calls `GameObject.Instantiate(themeModel)` internally, but `TrackPlayer.InitializeNoteTracker` may also instantiate theme models for its own purposes. This double-instantiates prefabs.
**Action:**
1. Verify whether `TrackPlayer.InitializeNoteTracker` passes an already-instantiated GameObject or a prefab to `ThemeMeshCache.ExtractTheme`
2. If passing a prefab (current `ExtractFromTheme` signature takes `GameObject themeModel` then instantiates): verify the caller also instantiates separately and consolidate — either pass the prefab directly (let `ExtractFromTheme` instantiate) or pass an already-instantiated copy (change `ExtractFromTheme` to NOT instantiate)
3. The correct pattern: `ExtractFromTheme` should accept a prefab and instantiate once internally; the caller should NOT separately instantiate for extraction purposes

### 9.4 Add profiler markers
**Files:** `HighwayElementGraphicsSystem.cs`, `NoteTracker.cs`, `SparseUploader.cs`
**Problem:** No `Profiler.BeginSample`/`EndSample` markers. Cannot measure per-method cost in the profiler.
**Action:** Add `UnityEngine.Profiling.Profiler.BeginSample("HEGS.OnPerformCulling")` / `EndSample()` pairs to:
- `OnPerformCullingCallback` (label: `HEGS.OnPerformCulling`)
- `UploadToGPU` (label: `NoteTracker.UploadToGPU`)
- `RemoveExpired` (label: `NoteTracker.RemoveExpired`)
- `SparseUploader.Commit` (label: `SparseUploader.Commit`)
- `BeginUploadFrame` (label: `HEGS.BeginUploadFrame`)

### 9.5 Remove unused `System.Linq` import
**File:** `Assets/Script/Gameplay/Visuals/Instancing/HighwayElementGraphicsSystem.cs`
**Action:** Delete `using System.Linq;` if present.

### 9.6 Remove unused `NoteData.Size` / `NoteSpawnData.Size` or add assertions
**File:** `Assets/Script/Gameplay/Visuals/Instancing/NoteData.cs` (lines 43, 101)
**Problem:** `public static readonly int Size` is defined but never referenced.
**Action:** Either remove both `Size` properties, or add `Debug.Assert(NoteData.Size == 68)` and `Debug.Assert(NoteSpawnData.Size == 20)` in a static constructor or `OnCreate`.

### 9.7 Add disposal guards to `NoteTracker` methods
**File:** `Assets/Script/Gameplay/Visuals/Instancing/NoteTracker.cs`
**Problem:** `UploadToGPU`, `RemoveExpired`, `Add`, `Remove` don't check `_disposed` before accessing disposed NativeArrays.
**Action:** Add `if (_disposed) return;` guard at the top of each public/internal method that touches `_notes` or `_spawnData`.

### 9.8 Clear NativeArray slots after swap-remove
**File:** `Assets/Script/Gameplay/Visuals/Instancing/NoteTracker.cs` (`Remove`)
**Problem:** After swap-remove, the last slot still contains the swapped-in copy. Not a bug (activeCount prevents reading it), but clearing aids debugging.
**Action:** After the swap, set `_notes[last] = default` and `_spawnData[last] = default` (only if `flatIndex != last`).

### 9.9 Wrap `RuntimeAutomation.cs` in compile symbol
**File:** `Assets/Script/RuntimeAutomation.cs`
**Problem:** 244 lines of automation/benchmarking code active in production builds.
**Action:** Wrap the entire class body in `#if YARG_TEST_BUILD` / `#endif` (the `YARG_TEST_BUILD` symbol is already added to `csc.rsp` during automation builds). Verify the `-automation` CLI flag still works with the symbol defined.

### 9.10 Add attribution comment to `HeapAllocator`
**File:** `Assets/Script/Gameplay/Visuals/Instancing/HeapAllocator.cs`
**Action:** Add header comment: `// Adapted from com.unity.entities.graphics HeapAllocator.cs. SPDX-License-Identifier: BSD-3-Clause`

### 9.11 Remove `ElementBatch.batchIndex` / `BatchIndexUpperBound` / `_nextBatchIndex` (dead code)
**File:** `Assets/Script/Gameplay/Visuals/Instancing/HighwayElementGraphicsSystem.cs`
**Problem:** After the shared-batch fix (using `batch.activeCount` as write slot instead of per-tracker `_batchPositions`), the `batchIndex` field, `_nextBatchIndex` counter, and `BatchIndexUpperBound` property are set but never read.
**Action:** Delete `batchIndex` from `ElementBatch`, delete `_nextBatchIndex` field, delete `BatchIndexUpperBound` property, delete the `batchIndex = _nextBatchIndex++` line in `GetOrCreateBatch`.

---

## 10. Missing gameplay features — TODO

### 10.1 Implement SP activation change hook (color update for in-flight notes)
**Files:** `Assets/Script/Gameplay/Player/TrackPlayer.cs`, `Assets/Script/Gameplay/Visuals/Instancing/NoteTracker.cs`
**Problem:** When star power activates/deactivates mid-song, in-flight notes need their `color` (all instruments) and `metalColor` (guitar/drums/ProKeys only — FiveLaneKeys uses constant `IsStarPower`) updated. The `_wasStarPowerActive` field exists on `TrackPlayer` (line 189) but is only used for track-lowering, not for note color updates.
**Action:**
1. In `TrackPlayer.GameplayUpdate`, after `UpdatePositions()` and before `UploadToGPU`, check if `Engine.BaseStats.IsStarPowerActive != _wasStarPowerActive`
2. If changed: call a new `NoteTracker.UpdateStarPowerColors(bool isStarPowerActive)` method
3. In `UpdateStarPowerColors`: iterate all active notes. For each note where `spawnData.isStarPowerVisible` is true (i.e., the note itself is an SP note):
   - Recompute `color` = `isStarPowerActive ? colors.GetNoteStarPowerColor(fret) : colors.GetNoteColor(fret)` (ALL instruments)
   - Recompute `metalColor` = `colors.GetMetalColor(isStarPowerActive)` for guitar/drums/ProKeys ONLY. FiveLaneKeys keeps `metalColor` unchanged (uses constant `NoteRef.IsStarPower`).
   - Write updated `NoteData` back to `_notes[i]`
4. The updated colors will be uploaded to GPU in the subsequent `UploadToGPU` call
5. Note: this does NOT change `spawnData.isStarPowerVisible` — that's the note's inherent SP flag, not the player's active SP state. The SP *mesh variant* switching (Task 10.4) is separate.

**Per-instrument color sources:**
- FiveFretGuitar / FiveLaneKeys: `Player.ColorProfile.FiveFretGuitar`
- Drums (4-lane): `Player.ColorProfile.FourLaneDrums`
- Drums (5-lane): `Player.ColorProfile.FiveLaneDrums`
- ProKeys: `Player.ColorProfile.ProKeys`

**Key distinction:** `note.IsStarPower` (the chart note's inherent SP flag, constant) vs `Engine.BaseStats.IsStarPowerActive` (the player's current SP state, dynamic). `color` uses the dynamic state; `colorNoStarPower` always uses the non-SP fret color (never changes); `metalColor` uses the dynamic state for guitar/drums/ProKeys, constant for FiveLaneKeys.

### 10.2 Implement drums SP-activator pulse
**Files:** `Assets/Script/Gameplay/Player/DrumsPlayer.cs`, `Assets/Script/Gameplay/Visuals/Instancing/NoteTracker.cs`
**Problem:** Drums SP-activator notes (where `NoteRef.IsStarPowerActivator && Player.Engine.CanStarPowerActivate && !IsStarPowerActive`) should pulse their color each frame based on `GameManager.BeatEventHandler.Visual.StrongBeat.CurrentPercentage`. This is a drums-only behavior — FiveLaneKeys has no SP-activator pulse.
**Action:**
1. Add a virtual method `UpdateStarPowerActivatorPulse()` on `TrackPlayer` (default: no-op)
2. Override in `DrumsPlayer`:
   - Get `CurrentPercentage` from `GameManager.BeatEventHandler.Visual.StrongBeat.CurrentPercentage`
   - Iterate active notes in `NoteTracker` where `note.IsStarPowerActivator && Engine.CanStarPowerActivate && !Engine.BaseStats.IsStarPowerActive`
   - Recompute `color` from the pulse: `Color.Lerp(baseColor, pulseColor, CurrentPercentage)` where `baseColor = colors.GetNoteColor(orderingInfo.ColorIndex)` and `pulseColor = colors.GetNoteStarPowerColor(orderingInfo.ColorIndex)`
   - Write back to `NoteTracker._notes[i].color`
3. Call `UpdateStarPowerActivatorPulse()` in `TrackPlayer.GameplayUpdate` after `UpdatePositions()` and before `UploadToGPU`
4. NoteTracker needs a method to expose/iterate active notes for this purpose, OR `DrumsPlayer` iterates via `NoteTracker.GetIndex` for each SP-activator note in the active window. Prefer a batch method: `NoteTracker.UpdateColorsForNotes(Predicate<NoteData> filter, Action<ref NoteData> updater)` — but since `NoteData` is in a NativeArray (value type), use `NoteTracker.SetColorAt(int index, Vector4 color)` or iterate with index access.

### 10.3 Implement per-instrument non-uniform scale
**Files:** `Assets/Script/Gameplay/Visuals/Instancing/NoteData.cs`, `Assets/Script/Gameplay/Visuals/Instancing/NoteTracker.cs`, `Assets/Script/Gameplay/Player/{FiveFretGuitarPlayer,DrumsPlayer,FiveLaneKeysPlayer,ProKeysPlayer}.cs`
**Problem:** `UploadToGPU` always uses `S(1, noteHeight, 1)`. The design specifies non-uniform scaling for drums and FiveLaneKeys:
- **Guitar/ProKeys:** `S(1, noteHeight, 1)` — current behavior is correct
- **FiveLaneKeys:** `S(1, noteHeight, 1)` when `UsingOpenLane`; `S(5/6, noteHeight*5/6, 1)` when NOT `UsingOpenLane` (replaces base scale entirely)
- **Drums:** `S(NoteScaleFactor, noteHeight*NoteScaleFactor, NoteScaleFactor)` — conditionally skipped for kick notes (Pad==0) when no dedicated kick lanes, and wildcard notes (use `S(1, noteHeight, 1)` instead)
**Action:**
1. Replace `noteHeight` (float) field in `NoteSpawnData` with `scale` (Vector3): `public Vector3 scale;`
2. Update `NoteSpawnData.Size` comment (struct grows from 20 to 32 bytes — verify with `UnsafeUtility.SizeOf`)
3. In each instrument's `CreateNoteSpawnData`, compute the correct scale:
   - **FiveFretGuitarPlayer:** `scale = new Vector3(1f, noteHeight, 1f)`
   - **ProKeysPlayer:** `scale = new Vector3(1f, noteHeight, 1f)`
   - **FiveLaneKeysPlayer:** `scale = Player.UsingOpenLane ? new Vector3(1f, noteHeight, 1f) : new Vector3(5f/6f, noteHeight * 5f/6f, 1f)`
   - **DrumsPlayer:** Check `isKick` (Pad==Kick) and `isWildcard` (Pad==Wildcard). If `isKick && NumberOfDedicatedKickLanes == 0`: `scale = new Vector3(1f, noteHeight, 1f)`. If `isWildcard`: `scale = new Vector3(1f, noteHeight, 1f)`. Otherwise: `scale = new Vector3(NoteScaleFactor, noteHeight * NoteScaleFactor, NoteScaleFactor)`. Get `NoteScaleFactor` from `Player.HighwayPreset` or the drums-specific preset (verify the source — check existing `DrumsNoteElement` for the scale formula).
4. In `NoteTracker.UploadToGPU`, replace `new Vector3(1f, scale, 1f)` with `spawn.scale`
5. Remove the `float scale = spawn.noteHeight;` line — use `spawn.scale` directly

### 10.4 Implement SP mesh variant switching (UpdateBatchAssignments)
**Files:** `Assets/Script/Gameplay/Visuals/Instancing/NoteTracker.cs`, `Assets/Script/Gameplay/Player/TrackPlayer.cs`
**Problem:** `UpdateBatchAssignments()` is a no-op. When a note's SP state changes (e.g., SP activates and the note should switch to its SP mesh variant), the batch assignment is not updated. Currently, `spawnData.isStarPowerVisible` is captured at spawn and never changed, so SP mesh variants are static.
**Status:** This is a lower-priority feature. The SP *color* update (Task 10.1) provides the primary visual feedback. SP *mesh* switching (different geometry for SP notes) is secondary.
**Action (if needed):**
1. Detect notes whose effective SP visibility changed since spawn
2. Remove from old batch, add to new batch (SP vs non-SP mesh variant)
3. Update `_batchAssignments[i]` and `spawnData.isStarPowerVisible`
4. This requires re-querying `ThemeMeshCache.GetRenderGroups` with the new SP state
5. **Defer if the color update (10.1) is sufficient for visual parity.** Verify with screenshots whether SP notes need different meshes or just different colors.

---

## 11. Performance verification and profiling — TODO

- [ ] 11.1 Profile with BRG rendering active — capture `CreateSharedRendererScene`, `BaseElement.LateUpdate`, draw calls. Use Unity Profiler with deep profile.
- [ ] 11.2 Verify ≥80% reduction in `CreateSharedRendererScene` time vs baseline.
- [ ] 11.3 Verify ≥90% reduction in note-related MonoBehaviour invokes (should be ~0 for note heads; sustain lines and beatlines remain).
- [ ] 11.4 Verify draw call count: one per batch (3 per render group × render groups per theme × 1 if single-player, more if multi-player shares batches).
- [ ] 11.5 Verify GPU memory usage: single GraphicsBuffer with HeapAllocator (no per-batch buffers).
- [ ] 11.6 Verify SparseUploader compute shader dispatch (not direct SetData fallback) — check Player.log for "Compute shader unavailable" message (should NOT appear).
- [ ] 11.7 Test on Steam Deck — frame time improvement, no visual regressions.
- [ ] 11.8 Test with dense charts — no performance regression at high note density. Use a chart with 500+ simultaneous notes.
- [ ] 11.9 Test all instruments — guitar (5-fret), drums (4-lane, 5-lane), keys (5-lane), ProKeys. Verify per-instrument scale (after Task 10.3), color resolution, SP-activation color flip (after Task 10.1), drums SP-activator pulse (after Task 10.2).
- [ ] 11.10 Add debug toggle for `dualRenderMode` (gameplay settings or console command) for A/B visual comparison.
- [ ] 11.11 Visual A/B comparison in dual mode — position and color parity between GameObject and BRG notes.
- [ ] 11.12 Verify zero GC allocations per frame in `UploadToGPU`, `RemoveExpired`, `OnPerformCulling` (use Unity Profiler memory module).
- [ ] 11.13 Verify `GraphicsBuffer` doesn't exhaust — test with dense chart, multiple players. Check for `[HEGS] BATCH OVERFLOW` or heap allocation failure warnings in Player.log.
