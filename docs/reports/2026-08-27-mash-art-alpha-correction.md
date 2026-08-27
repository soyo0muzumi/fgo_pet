# Mash Casual Art Alpha Correction

Date: 2026-08-27

## Result

Approved for Phase 0 rendering validation.

The regenerated bundle contains 29 assets and passes `art process-mash-casual` QA. Existing transparency is preserved byte-for-byte instead of being passed through edge-connected background removal.

## Evidence

- Bundle: `D:\fgo_unpack\fgo_assets\pet\mash\casual`
- QA status: `PASS`
- Schema version at this checkpoint: `1` (Task 2 upgrades it to the composition-aware schema)
- Runtime pixels with alpha below raw: `0`
- Runtime pixels with changed alpha: `0`
- Raw-visible pixels changed to fully transparent: `0`
- `full_body` raw SHA-256: `e38e71bbf86480b37b11ca3be761252a1f7f9fb3d67588983c1d1b651ee2d1f9`
- `full_body` runtime SHA-256: `e38e71bbf86480b37b11ca3be761252a1f7f9fb3d67588983c1d1b651ee2d1f9`

## Visual review

The regenerated contact sheet was inspected at original resolution. The full-body silhouette and all 28 expression crops retain their hair, glasses, face, collar, shoulders, and dark garment pixels. No recurrence of the earlier cut-out damage was observed.

Task 2 must regenerate the bundle after adding composition metadata and repeat the composite alignment review before the art is used for the final renderer decision.
