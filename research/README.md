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
