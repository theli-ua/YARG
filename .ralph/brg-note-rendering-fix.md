## BRG Custom Instanced Note Rendering — Fix Notes Not Rendering

### Problem
BRG culling callback fires (8,277 calls), batches created (2), notes spawn (activeCount 4-9), benchmark completes without crash. **BUT Frame Debugger shows ZERO draw commands.**

### Root Cause Investigation
1. **Draw commands malformed** — silently dropped by Unity native renderer
2. **BRG not integrated with URP** — draw commands generated but never submitted
3. **Shader not reading DOTS data** — falls back to defaults (identity transform = pos 0,0,0)
4. **Shader property name mismatch** — metadata uses `unity_ObjectToWorld` but shader expects different name
5. **Batches=0 for 22% of frames** — unexplained, Dispose not called

### Iteration Plan
1. Research: Examine current culling callback, draw command generation, shader setup
2. Research: Check how BRG integrates with URP highway camera
3. Research: Verify shader reads DOTS instancing data correctly
4. Fix: Address root cause — likely draw command format, shader compatibility, or BRG-URP integration
5. Verify: Build, run benchmark, check Frame Debugger for draw commands
6. Iterate: Keep looping until notes render correctly
7. Update: Keep brg.md updated with each attempt and result

### Success Criteria
- Frame Debugger shows draw commands from BRG
- Notes visible in gameplay at correct positions with correct colors
- Benchmark completes without crash
- brg.md updated with findings

## Iteration 1 Results (2026-06-28)

### Changes Made
1. Fixed shader: Changed `CustomPropertyMetadata` → `MaterialPropertyMetadata` in NoteBRGUnlit.shader
2. Added BRG camera assignment: `_renderCamera.batchRendererGroup = _graphicsSystem.BatchRendererGroup`
3. Exposed BRG getter in HighwayElementGraphicsSystem

### Build Status
✅ C# build succeeds (0 errors, 11 warnings)

### Issue Found
`Camera.batchRendererGroup` property doesn't exist in Unity 6.0 / Core RP 17.3.0. Removed the assignment line - BRG is registered with render pipeline automatically.

### Benchmark Status
❌ Cannot run - Unity editor stuck in batch mode, doesn't enter play mode with `-automation` flag. Project loads successfully but automation never starts. This appears to be a pre-existing environment issue.

### Next Steps
- **BLOCKED**: Unity batch mode cannot enter play mode even with display available
- Tested: `-quit`, `-nographics`, `-benchmark`, `-automation`, `-executeMethod`, Xdummy, xdotool
- All fail because Unity batch mode requires user interaction to enter play mode
- Previous successful run (Player-prev.log) was likely run interactively
- BRG code changes are complete and compile successfully
- Verification requires interactive Unity session or CI environment