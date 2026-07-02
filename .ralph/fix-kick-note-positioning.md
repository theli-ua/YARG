## Fix Kick Note Positioning Bug

### Problem
Yellow kick notes (open notes) are shifted right on the right highway but centered correctly on the left highway.

### Root Cause Analysis
- `CreateNoteSpawnData` in `FiveFretGuitarPlayer.cs` sets `baseX = ComputeElementX(lane, LANE_COUNT)` for open/wildcard notes
- Lane is set to `(LANE_COUNT - 1) / 2` (center lane = lane 2 for 5-fret)
- The kick note mesh in the theme prefab may be positioned differently than expected
- The `meshLocalOffset` is calculated from the theme prefab's root transform to the mesh's local transform

### Fix Strategy
1. Identify if the note is an open/kick note
2. Adjust the `baseX` calculation to account for the mesh's center point
3. Build and test with automation screenshots
4. Verify the fix visually

### Iteration Checklist
- [ ] Analyze the kick note mesh positioning in the theme prefab
- [ ] Modify `CreateNoteSpawnData` to adjust `baseX` for open notes
- [ ] Build the project
- [ ] Run automation and capture screenshots
- [ ] Verify kick notes are centered on both highways
- [ ] Commit the fix