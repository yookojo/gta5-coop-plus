# Renderer research (read-only)

Tools that only *read* the running game's memory, to check whether GTA V can draw two full views in one frame.

- `rprobe.ps1`: finds FiveM's patterns on the current build, reads the game viewport and walks the game skeleton.
- `dis.py <image.bin> <base.txt> <hexOffset> <bytes>`: disassembles a function from a memory dump (Capstone).
- `refs.py <image.bin> <base.txt> name=address ...`: counts code references to a global.

## Findings on 1.0.3889 (2026-11-25)
- FiveM's patterns for `spdViewport::sm_Current`, `g_viewportGame` and `rage::gameSkeleton` each match exactly once.
- The game skeleton (228 init functions, 40-byte entries, name hash at +32) lists PostFX, CRenderer, CPostScan, CScene, Water,
  CRenderPhaseCascadeShadowsInterface and ViewportSystemInit (+0x2ACCC).
- ViewportSystemInit is plain, unobfuscated code. It builds 3 viewport objects (0x600/0x600/0x5F0 bytes) and registers each
  in a viewport manager list at +0x1F6B300; the game viewport pointer is at +0x1F6B320.
- The single game viewport holds whichever camera drew last (it alternated between both players' cameras while CoopPlus ran).
- 57 code sites read "the game viewport" (single-camera assumptions: LOD, culling, streaming...). 171 read
  `spdViewport::sm_Current` (the viewport being drawn), so most drawing code follows whatever viewport is current.

## Next step for a proof of concept
Find the render phase list (data, so adding a phase is a data change rather than a code patch, which Arxan won't flag),
create a second CViewportGame for Player 2, and add one scene render phase bound to it. If it draws, then two views
in one frame are possible.

## Experiment 1 (2026-11-25): do render phases draw from their own viewport? YES
- `findrefs.ps1`: 29 render phase objects (22 classes), each keeps its viewport pointer at **+0x4C8**. The render
  phase list is a plain array of 28 phase pointers (the frame's pipeline order); 22 phases use the game viewport.
- `phasetest.ps1`: copied the game viewport while it held Player 2's camera (turned 180 degrees), turned split off,
  then pointed one phase at a time at the copy for ~1 s (data write only, restored after each). No crash.
- **Phase [4] (class +0x1A46DD0) = the main scene phase.** Redirected, the main screen showed the whole world from Player 2's
  camera, complete (visibility is computed per phase from its own viewport), lit, with the normal HUD on top
  (`phase4_from_player2_camera.png` vs `phase4_before.png`). Phases [18] and [21] redraw transparent parts (glass,
  car interior) from the other camera; [18] also changes lighting. The rest showed no visible change.

## What this means
Drawing a second view in the same frame is a matter of running a second copy of the scene chain ([4], the lighting/
transparent phases, post-processing) bound to Player 2's viewport and writing into its own render targets, then
putting that picture on the TV. Not a renderer rewrite.
Next: duplicate phase [4] as a new phase object with Player 2's viewport, give it its own G-buffer/colour targets,
and add it to the list; then the lighting and post-processing phases.

## Experiment 2 (2026-11-25): a cloned phase object runs and renders its own camera. YES
`clonetest.ps1`: copied phase [4]'s object (0x800 bytes) into new memory, set its viewport (+0x4C8) to Player 2's camera, and swapped it into
slot [13] for 1.5 s. The screen showed a complete, lit view from Player 2's camera (no missing geometry), so clones compute their own
visibility. Problems: the clone draws into the SAME global render targets (G-buffer etc.), so it overwrote Player 1's picture; shadows
(cascades follow the main camera) and glass were off. The atArray header for the phase list wasn't found by pointer search (the list may be
inline in a manager object).

## Plan to finish (not started)
1. Run Player 2's cloned chain (shadows, scene [4], lighting/transparent [17-21], post-fx) BEFORE Player 1's in the list.
2. Between the chains, copy the finished picture out on the render thread: a tiny custom phase whose BuildDrawList enqueues a generic draw
   command (FiveM: EnqueueGenericDrawCommand; pattern still to be found for GTA V 1.0.3889) that runs a callback in our ASI.
3. Exposure/TAA history are global and would still mix between the two chains: save/restore them in the same callbacks.
