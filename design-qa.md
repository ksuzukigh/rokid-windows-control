# Design QA: lower navigation highlight

## Target

- macOS implementation: a 44×44 black-backed cyan highlight at 1:1 Rokid display scale
- Rokid device screen: 480×640
- Windows standard mirror: approximately 320×427 at 150% Windows display scaling
- Test state: Home selected in the lower navigation row

## Evidence

- The original Windows capture showed a fixed-size ring that was too large and centered above the Home icon.
- An ADB screenshot measured the visible Home icon center at Rokid coordinate `(240, 330)`.
- The input target remains `(240, 320)`; display and input coordinates must therefore be separate.
- The corrected layout produces an approximately 29-pixel ring on the 320-pixel-wide Windows mirror and centers it on the visible Home icon.
- A visual overlay using the corrected layout was captured over the live scrcpy device image. The size and center alignment passed visual inspection.

## Remaining gate

The newly built application DLL cannot be launched on this PC because Smart App Control requires a trusted signature. The security setting was not changed. A screenshot from the corrected production binary remains pending until a trusted signed build is available.

final result: blocked
