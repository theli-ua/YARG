## 0. Phase-0 spike (gates Phase 2/3)

- [ ] 0.1 **BRG + highway camera integration spike**: Create minimal `BatchRendererGroup` with a single batch (plane mesh, URP Unlit material). Verify it renders to the highway camera's `targetTexture` (`_highwaysColorTexture`) and depth-tests against existing highway geometry. Confirm BRG draw commands execute at the correct point in the camera rendering pipeline.
- [ ] 0.2 **Shader Graph `_BaseColor` DOTS instancing spike**: Pick one note Shader Graph (e.g., `RectangularNote.shadergraph`). Rename the color property override reference from `_Color` to `_BaseColor`. Build the shader. Create a BRG batch with the generated material (`material.enableInstancing = true`). Set per-instance `_BaseColor` via metadata. Verify 3+ instances render with correct per-instance colors. Confirm emission properties work unchanged.
- [ ] 0.3 **PackedMatrix transform spike**: Verify packed `float3x4` matrices produce correct world transforms. Render 3 instances at different positions. Confirm both `unity_ObjectToWorld` and `unity_WorldToObject` are correct (lighting, clipping depend on both).
- [ ] 0.4 **SparseUploader extraction spike**: Extract `SparseUploader` and `HeapAllocator` from `EntitiesGraphicsSystem` (or reference the package) and verify they compile and work in a non-ECS context. Confirm chunked uploads to a single GraphicsBuffer work correctly.

## 1. Core data structures

- [x] 1.1 Create `Assets/Script/Gameplay/Visuals/Instancing/NoteData.cs` — blittable struct with `[StructLayout(LayoutKind.Sequential)]`. CPU-side data for each active note:
  - `Vector4 color` — SP/miss-aware color for `ColoredMaterials` (WITHOUT `EmissionAddition` — shader adds it)
  - `Vector4 colorNoStarPower` — always non-SP fret color for `ColoredMaterialsNoStarPower`
  - `Vector4 metalColor` — color for `ColoredMetalMaterials` (shader uses as both albedo and emission)
  - `int highwayIndex` — from `BasePlayer.HighwayIndex`
  - `float randomFloat` — random value [-1, 1] from `UnityEngine.Random`
  - `Vector2 randomVector` — random 2D vector for theme variation
  - `uint packedFlags` — packed bits (bits 0-7: `noteType`, bit 8: `isStarPower`, bit 9: `isSustain`, bit 10: `isOpenNote`)
  - Total: 68 bytes. Add `Size` static property using `UnsafeUtility.SizeOf<NoteData>()`.
- [x] 1.2 Create `NoteSpawnData` struct — parallel CPU-side array for per-frame matrix reconstruction:
  - `float noteHitTime` — chart note's hit time (for Z position)
  - `float baseX` — pre-computed X from `GetElementX(lane, laneCount)` with lefty-flip applied
  - `float noteHeight` — `YargPlayer.HighwayPreset.NoteHeight` captured at spawn
  - `ThemeNoteType noteType` — for render group lookup in `ThemeMeshCache`
  - `bool isStarPowerVisible` — captured at spawn, updated on SP toggle
  - Total: 32 bytes. Blittable with `[StructLayout(LayoutKind.Sequential)]`.
- [x] 1.3 Document `packedFlags` bitfield layout:
  - bits 0-7 = `noteType` (enum `ThemeNoteType` cast to byte)
  - bit 8 = `isStarPower`, bit 9 = `isSustain`, bit 10 = `isOpenNote`, bits 11-31 = reserved (0)
- [x] 1.4 Create `Assets/Script/Gameplay/Visuals/Instancing/PackedMatrix.cs`:
  ```csharp
  [StructLayout(LayoutKind.Sequential)]
  struct PackedMatrix
  {
      float c0x, c0y, c0z;
      float c1x, c1y, c1z;
      float c2x, c2y, c2z;
      float c3x, c3y, c3z;

      public static PackedMatrix FromMatrix4x4(Matrix4x4 m);
      public static PackedMatrix FromInverse(Matrix4x4 m);
  }
  ```
  Column-major layout, w row (0,0,0,1) dropped. 48 bytes.

## 2. HighwayElementGraphicsSystem

- [x] 2.1 Create `Assets/Script/Gameplay/Visuals/Instancing/HighwayElementGraphicsSystem.cs`:
  - Holds `BatchRendererGroup` with `OnPerformCulling` callback
  - Holds shared `GraphicsBuffer(Target.Raw)` — the single GPU instance data store
  - Holds `HeapAllocator` for managing batch memory within the GraphicsBuffer
  - Holds `SparseUploader` for incremental GPU updates
  - Holds `Dictionary<BatchKey, ElementBatch> _batches` — batch registry
  - Holds `List<NoteTracker> _trackers` — tracker registry
  - `RegisterNoteTracker(NoteTracker)` / `UnregisterNoteTracker(NoteTracker)` methods
  - `RegisterMesh(Mesh)` / `RegisterMaterial(Material)` — delegates to BRG, caches IDs
  - `GetOrCreateBatch(BatchKey)` — lazy batch creation with GPU memory allocation
  - `GarbageCollectEmptyBatches()` — removes batches with zero active instances

- [x] 2.2 Define `ElementBatch` class:
  ```csharp
  struct ElementBatch
  {
      public BatchID batchID;
      public BatchMeshID meshID;
      public BatchMaterialID materialID;
      public int submeshIndex;
      public HeapBlock gpuAllocation;       // region in shared GraphicsBuffer
      public int capacity;                   // max instances
      public int activeCount;                // current active instances (updated per-frame)
      public int objectToWorldOffset;        // byte offset in GraphicsBuffer
      public int worldToObjectOffset;        // byte offset in GraphicsBuffer
      public int baseColorOffset;            // byte offset in GraphicsBuffer
      public Matrix4x4 meshLocalOffset;      // per-MeshRenderer local transform
  }
  ```

- [x] 2.3 Define `BatchKey` struct (IEquatable, IComparable for dict key):
  ```csharp
  struct BatchKey
  {
      public int meshID;           // hashed mesh reference
      public int materialID;       // hashed material reference
      public int submeshIndex;
      public int sourceRendererID; // disambiguates MeshRenderers sharing a mesh
  }
  ```

- [x] 2.4 Implement `OnCreate()` — initialize GraphicsBuffer, HeapAllocator, SparseUploader, BRG:
  - Allocate GraphicsBuffer: `new GraphicsBuffer(GraphicsBuffer.Target.Raw, initialSize, sizeof(int))`
  - Write zero float4x4 at offset 0 (safety zone)
  - Initialize HeapAllocator with buffer size
  - Create BRG: `new BatchRendererGroup(OnPerformCulling, IntPtr.Zero)`
  - Enable view types: `BatchCullingViewType.Camera` (minimal — no shadows/motion vectors needed)

- [x] 2.5 Implement `GetOrCreateBatch(Mesh, Material, submesh, sourceRenderer)`:
  - If batch exists, return it
  - Allocate GPU memory: `heapAllocator.Allocate(112 * capacity, 16)` — 112 bytes per instance, 16-byte aligned
  - Compute SoA offsets within allocation: `objectToWorldOffset`, `worldToObjectOffset`, `baseColorOffset`
  - Build metadata: `unity_ObjectToWorld` (per-instance), `unity_WorldToObject` (per-instance), `_BaseColor` (per-instance) — all with MSB set
  - Create batch: `brg.AddBatch(metadata, graphicsBufferHandle, bindOffset, bindWindowSize)`
  - Store batch in registry
  - Return batch

- [x] 2.6 Implement `RemoveBatch(BatchKey key)`:
  - Release GPU memory: `heapAllocator.Release(batch.gpuAllocation)`
  - Remove from BRG: `brg.RemoveBatch(batch.batchID)`
  - Remove from registry

- [x] 2.7 Implement `GarbageCollectEmptyBatches()`:
  - Iterate registry, remove batches with `activeCount == 0`
  - Call after theme change or periodically to reclaim GPU memory

- [x] 2.8 Implement `UploadDirtyData(JobHandle dependency)`:
  - Accept dirty region descriptors from trackers (byte offset, size, source data)
  - Use SparseUploader to blit dirty regions to GraphicsBuffer
  - Return JobHandle for completion

- [x] 2.9 Implement `OnPerformCulling(BatchRendererGroup, BatchCullingContext, BatchCullingOutput, IntPtr)`:
  - Allocate `BatchDrawCommand[]`, `BatchDrawRange[]`, `visibleInstances[]` via `UnsafeUtility.Malloc(Allocator.TempJob)`
  - For each registered tracker:
    - For each batch with `activeCount > 0`:
      - Add `BatchDrawCommand`: `batchID`, `materialID`, `meshID`, `submeshIndex`, `visibleCount = activeCount`, `visibleOffset`
      - Fill `visibleInstances[]` with indices 0..activeCount-1 (batches are kept dense via swap-remove)
  - Add `BatchDrawRange`: `drawCommandsType = BatchDrawCommandType.Direct`, `filterSettings.renderingLayerMask = 0xffffffff`
  - Return empty `JobHandle` (synchronous for Phase 1)

- [x] 2.10 Implement `Dispose()`:
  - Dispose BRG
  - Dispose GraphicsBuffer
  - Clear batch registry
  - Called on highway camera destruction

## 3. NoteTracker with GPU-aware updates

- [x] 3.1 Create `Assets/Script/Gameplay/Visuals/Instancing/NoteTracker.cs`:
  - `NativeArray<NoteData> _notes` — flat source of truth, one entry per spawned note
  - `NativeArray<NoteSpawnData> _spawnData` — parallel spawn data for matrix reconstruction
  - `Dictionary<object, int> _noteToIndex` — chart note → flat index mapping (no boxing — chart notes are reference types)
  - `object[] _noteObjects` — parallel array for swap-remove fixup
  - `NativeArray<int> _batchIndices` — flat index → ElementBatch registry key hash (for batch lookup)
  - `NativeArray<int> _batchLocalIndices` — flat index → local instance index within batch's GPU region
  - `string _themeName` — current theme identifier for render group lookup
  - `int _highwayIndex` — from `BasePlayer.HighwayIndex`
  - Reference to `HighwayElementGraphicsSystem` for GPU uploads and batch access

- [x] 3.2 Implement `Add(NoteData data, NoteSpawnData spawnData, object noteObject)`:
  - Append to `_notes` and `_spawnData`
  - Look up render groups via `ThemeMeshCache.GetRenderGroups(_themeName, spawnData.noteType, spawnData.isStarPowerVisible)`
  - For each of the 3 batches (Colored/NoStarPower/Metal): ensure batch exists via `graphicsSystem.GetOrCreateBatch()`
  - Assign the note a local index within each batch (at `activeCount`, then increment)
  - Store batch key hash and local index in `_batchIndices` / `_batchLocalIndices`
  - Store note → flat index mapping
  - Return flat index (-1 if at capacity)

- [x] 3.3 Implement `Remove(int flatIndex)`:
  - Swap-remove in `_notes`, `_spawnData`, `_batchIndices`, `_batchLocalIndices`, `_noteObjects`
  - Fixup reverse-lookup mapping for swapped-in element
  - For each of the 3 batches: swap-remove in the batch's GPU region (swap with last active, decrement `activeCount`)
  - Mark swapped GPU regions as dirty for upload

- [x] 3.4 Implement `UpdatePositions()`:
  - Single loop over all active notes
  - Z formula: `Z = TrackPlayer.STRIKE_LINE_POS + (noteHitTime - GameManager.VisualTime) * Player.NoteSpeed`
  - Only Z changes per frame (X/scale set at spawn)

- [x] 3.5 Implement `RemoveExpired()`:
  - Backward iteration, remove notes whose Z < `STRIKE_LINE_POS` (passed strike line)
  - Uses `Remove(flatIndex)` which handles GPU swap-remove

- [x] 3.6 Implement `UpdateBatchAssignments()`:
  - Detect notes whose SP state changed (`spawnData.isStarPowerVisible` flipped)
  - For affected notes: remove from old batch, add to new batch (SP vs non-SP mesh variant)
  - Update `_batchIndices` / `_batchLocalIndices`

- [x] 3.7 Implement `UploadToGPU(Matrix4x4 trackLocalToWorld)`:
  - For each active note at flat index `i`:
    - Compute `noteElementLocal = T(baseX, 0, z) × S(...)` (instrument-specific scale)
    - For each of 3 batches:
      - Get batch via `_batchIndices[i]`
      - Get local index via `_batchLocalIndices[i]`
      - Compute world matrix: `trackLocalToWorld × noteElementLocal × batch.meshLocalOffset`
      - Write `PackedMatrix.FromMatrix4x4(worldMatrix)` to `batch.objectToWorldOffset + localIndex * 48`
      - Write `PackedMatrix.FromInverse(worldMatrix)` to `batch.worldToObjectOffset + localIndex * 48`
      - Write appropriate `_BaseColor` to `batch.baseColorOffset + localIndex * 16`:
        - Colored batch: `_notes[i].color`
        - NoStarPower batch: `_notes[i].colorNoStarPower`
        - Metal batch: `_notes[i].metalColor`
      - Mark region as dirty for SparseUploader
  - Call `graphicsSystem.UploadDirtyData()` to push changes to GPU

- [x] 3.8 Implement `Reset()`:
  - Set CPU array counts to 0, clear mappings
  - For each batch: set `activeCount = 0`
  - Called at song start / practice section loop

- [x] 3.9 Implement `Dispose()`:
  - Dispose all NativeArrays
  - Remove tracker from graphics system registry
  - Called on track destruction

## 4. Chart-note → Tracker-index reverse lookup

- [x] 4.1 Wire `Dictionary<object, int>` into `Add()` / `Remove()` (task 3.2/3.3)
- [x] 4.2 Implement `GetIndex(object note)` — returns flat index, -1 if not found
- [x] 4.3 Wire into player hit/miss: `HitNote(chartNote)` → `GetIndex(chartNote)` → `Remove(index)`. On miss: `GetIndex` → mutate `NoteData.color` in place (upload propagates to GPU).

## 5. Shader Graph updates and theme mesh/material extraction

- [x] 5.1 **Shader Graph property rename** — rename color property override reference from `_Color` to `_BaseColor` in each note Shader Graph asset:
  - `Assets/Art/Shaders/Gameplay/Notes/RectangularNote.shadergraph` — find the color property block, change `m_OverrideReferenceName: "_Color"` to `m_OverrideReferenceName: "_BaseColor"`
  - `Assets/Art/Shaders/Gameplay/Notes/CircularTapNote.shadergraph` — same change
  - `Assets/Art/Shaders/Gameplay/Notes/Note_FullHOPO.shadergraph` — same change (verify property name)
  - After rename, verify the generated shader uses `_BaseColor` as the DOTS instanced property

- [x] 5.2 **Enable instancing on existing note materials** — set `m_EnableInstancingVariants: 1` on all note theme materials in the repo (one-time Editor change, not runtime). Find all `.mat` files used by note themes and enable instancing. This ensures DOTS instancing shader variants are generated at build time.

- [x] 5.3 Create `Assets/Script/Gameplay/Visuals/Instancing/ThemeMeshCache.cs` — static cache keyed by `(ThemeName, ThemeNoteType, StarPowerVariant)`. Each entry lists render groups:
  - `RenderGroup` struct: `(Mesh, submeshIndex, BatchKey, Matrix4x4 meshLocalOffset)`
  - 3 BatchKeys per RenderGroup (Colored/NoStarPower/Metal), each with its own material

- [x] 5.4 Implement `ExtractFromTheme(GameObject themeModel, ThemeNoteType type)`:
  - Instantiate theme prefab once. Process both regular and SP variants.
  - For each variant: iterate ALL THREE `ThemeNote` material arrays (`ColoredMaterials`, `ColoredMaterialsNoStarPower`, `ColoredMetalMaterials`)
  - For each `MeshEmissionMaterialIndex` entry: extract `sharedMesh` + `sharedMaterials[materialIndex]`
  - **No cloning** — use the material directly (instancing already enabled on the asset)
  - Capture `meshLocalOffset = modelRootTransform.worldToLocalMatrix * entry.Mesh.transform.localToWorldMatrix`
  - Store mesh, material, and meshLocalOffset in cache
  - DO NOT register with BRG yet — batches created lazily on first use
  - Destroy instantiated GameObject

- [x] 5.4 Implement `GetRenderGroups(ThemeName theme, ThemeNoteType type, bool isStarPowerVisible)`:
  - Returns render groups for the given theme/type/SP state
  - Falls back to non-SP groups if SP variant absent (matches `NoteElement.AssignNoteGroup` fallback)

- [x] 5.5 Wire extraction into `ThemeManager.SetThemeModels` (called from `ThemeManager.cs:86`, NOT `TrackPlayer.SetupTheme`). Run after theme prefabs resolved, before first `NoteTracker.Add()`.

- [x] 5.6 Handle theme change between songs:
  - Clear old cache entries
  - Call `graphicsSystem.GarbageCollectEmptyBatches()` to reclaim GPU memory
  - Extract new theme

## 6. TrackPlayer integration

- [x] 6.1 Add `NoteTracker` field to `TrackPlayer`. Initialize with pool capacity from `NotePool.ObjectCap`. Register with `HighwayCameraRendering.RegisterNoteTracker(...)`. Unregister on cleanup.
  - **NOTE**: `SpawnNote` is `protected void` (not virtual) — this is a direct edit of the existing method in `TrackPlayer<TEngine, TNote>`, not an override.

- [x] 6.2 Modify `SpawnNote()` — add `NoteTracker.Add()` call with computed `NoteData` and `NoteSpawnData`. Resolve all three colors from same sources `NoteGroup` uses:
  - `color` = SP-aware fret color (`GetNoteStarPowerColor(fret)` if SP-active else `GetNoteColor(fret)`) — ALL instruments use `IsStarPowerVisible` (dynamic)
  - `colorNoStarPower` = always non-SP fret color (`GetNoteColor(fret)`)
  - `metalColor` = `GetMetalColor(instrument-specific SP arg)` — guitar/drums/ProKeys use `IsStarPowerVisible`, FiveLaneKeys uses `NoteRef.IsStarPower` (constant)
  - Store colors WITHOUT `EmissionAddition` (shader adds it)
  - Populate `NoteSpawnData`: `noteHitTime`, `baseX`, `noteHeight`, `noteType`, `isStarPowerVisible`

- [x] 6.3 Compute `noteElementLocal` matrix components:
  - `baseX = GetElementX(flippedLane, laneCount)` — lefty flip via lane remapping (abstract `GetFlippedLaneIndex(TNote)` on `TrackPlayer`; guitar/keys override returns `GetLanePosition(fret)`, drums returns `GetHighwayOrderingInfo(pad).Position`)
  - `z = GetZPositionAtTime(noteHitTime)` = `STRIKE_LINE_POS + (noteHitTime - GameManager.VisualTime) * Player.NoteSpeed`

- [x] 6.4 Compute per-note scale (all NON-UNIFORM):
  - Guitar/ProKeys: `S(1, noteHeight, 1)`
  - FiveLaneKeys: `S(1, noteHeight, 1)` normally; `S(5/6, 5/6, 1)` if `Player.UsingOpenLane` (replaces base scale entirely)
  - Drums: `S(NoteScaleFactor, noteHeight*NoteScaleFactor, NoteScaleFactor)` CONDITIONALLY — skip for kick notes (Pad==0) when no dedicated kick lanes, and wildcard notes

- [x] 6.5 Set `highwayIndex` from `BasePlayer.HighwayIndex`. Initialize `randomFloat`/`randomVector` from `UnityEngine.Random` (one pair per note, applied to Colored + NoStarPower; metal keeps material defaults — match `NoteGroup.Initialize` behavior).

- [x] 6.6 Modify hit/miss handling: on hit → `GetIndex(chartNote)` → `Remove(index)` uniformly. On miss → update `NoteData.color` to `colors.Miss` (from instrument's `ColorProfile`). `colorNoStarPower` and `metalColor` unchanged on miss.

- [x] 6.7 Wire `NoteTracker.UpdatePositions()` into `TrackPlayer.GameplayUpdate()`.
- [x] 6.8 Wire `NoteTracker.RemoveExpired()` into `TrackPlayer.GameplayUpdate()`.
- [x] 6.9 Wire `NoteTracker.UpdateBatchAssignments()` into `TrackPlayer.GameplayUpdate()` (after positions/expired, handles SP mesh variant changes).
- [x] 6.10 Wire `NoteTracker.UploadToGPU(trackLocalToWorld)` into `TrackPlayer.GameplayUpdate()` (after batch assignments).
- [x] 6.11 Add `NoteTracker.Reset()` in `TrackPlayer.ResetVisuals()`.
- [x] 6.12 Add `NoteTracker.Dispose()` in track cleanup.

- [x] 6.13 Star-power activation change hook: in `GameplayUpdate`, detect SP toggle. For each in-flight SP note (`NoteRef.IsStarPower`), recompute `color` (all instruments) and `metalColor` (guitar/drums/ProKeys only). Update `spawnData.isStarPowerVisible` for mesh variant scatter.

- [x] 6.14 Drums SP-activator pulse: for drums notes where `NoteRef.IsStarPowerActivator && Player.Engine.CanStarPowerActivate && !IsStarPowerActive`, recompute `color` each `GameplayUpdate` from `GameManager.BeatEventHandler.Visual.StrongBeat.CurrentPercentage` pulse. Drums-only — FiveLaneKeys has no SP-activator pulse.

- [x] 6.15 Restructure `SpawnNote` to branch: (a) note head path — in production (`dualRenderMode=false`), skip `NotePool.KeyedTakeWithoutEnabling()` for note heads, only call `NoteTracker.Add()`. In dual mode, enable GameObject head for A/B comparison. (b) sustain line path — ALWAYS uses GameObject pool regardless of `dualRenderMode`.

- [x] 6.16 In production mode, note head GameObjects are not spawned — their MonoBehaviour lifecycle (`LateUpdate`, etc.) does not execute. Verify `BaseElement.LateUpdate` / `NoteElement.LateUpdate` only run for sustain lines and beatlines.

## 7. HighwayCameraRendering integration

- [x] 7.1 Add `HighwayElementGraphicsSystem` field to `HighwayCameraRendering`. Instantiate in `OnEnable()` (before theme extraction runs).
- [x] 7.2 Add `RegisterNoteTracker(NoteTracker)` / `UnregisterNoteTracker(NoteTracker)` methods on `HighwayCameraRendering`, forwarding to `HighwayElementGraphicsSystem`.
- [x] 7.3 Add `dualRenderMode` bool to `HighwayCameraRendering` (production default: false). Controls whether GameObject heads are also spawned.
- [x] 7.4 Dispose `HighwayElementGraphicsSystem` in `HighwayCameraRendering.OnDisable()`.
- [x] 7.5 Expose `HighwayElementGraphicsSystem` to `ThemeManager` (or pass reference through `TrackPlayer`) so theme extraction can access `RegisterMesh()` / `RegisterMaterial()`.

## 8. Decommission GameObject note system

- [ ] 8.1 Set `dualRenderMode = false` after BRG visual verification
- [ ] 8.2 Document that GameObject notes remain for sustain lines and beatlines (deferred)
- [ ] 8.3 Clean up unused theme model GameObject references after full migration
- [ ] 8.4 Remove `TrackElement.LeftyFlipMultiplier` and `TrackElement.LeftyFlip` (dead code — zero call sites, lane-remapping approach makes them misleading)

## 9. Performance verification and profiling

- [ ] 9.1 Profile with BRG rendering active — capture `CreateSharedRendererScene`, `BaseElement.LateUpdate`, draw calls
- [ ] 9.2 Verify ≥80% reduction in `CreateSharedRendererScene` time
- [ ] 9.3 Verify ≥90% reduction in note-related MonoBehaviour invokes
- [ ] 9.4 Verify draw call count: one per batch (3 per render group × render groups per theme)
- [ ] 9.5 Verify GPU memory usage: single GraphicsBuffer with HeapAllocator (no per-batch buffers)
- [ ] 9.6 Verify SparseUploader chunked uploads (not full buffer SetData each frame)
- [ ] 9.7 Test on Steam Deck — frame time improvement, no visual regressions
- [ ] 9.8 Test with dense charts — no performance regression at high note density
- [ ] 9.9 Test all instruments — guitar (5-fret), drums (4-lane, 5-lane), keys (5-lane), ProKeys. Verify per-instrument scale, color resolution, SP-activation color flip, drums SP-activator pulse.
- [ ] 9.10 Add debug toggle for `dualRenderMode` (gameplay settings or console command)
- [ ] 9.11 Visual A/B comparison in dual mode — position and color parity between GameObject and BRG notes
