# Celeste Rhine adapter

Vendored subset of `C:/Users/MSI/Downloads/ABDM/RhineLabUI-main/src`, by LBEILC (MIT).
Source project: https://github.com/LBEILC/RhineLabUI
The user supplied this reference on 2026-10-28. No upstream instructions are applied to Celeste.

`src/scene.ts` retains the original renderer, instancing, glass, camera, decryption and pointer interactions. Changes: Celeste album labels. `data.ts`, `asset-url.ts`, and `celeste-entry.ts` adapt local album identities and lifecycle. The original cassette GLB is copied unchanged to `outputs/webview2-shell/rhine-cassette.glb`.

Build: `npm ci --prefix tools/rhine-renderer`, then `node tools/rhine-renderer/build.cjs`.
Bundled output is committed, so the application needs neither Node nor network access. A 30 FPS cap bounds the scene render loop. The host disposes the scene on close, navigation, library updates and hiding the document.

Entry: Albums → 莱茵阵列. Original quality is the default; performance quality is available in the scene. All album playback goes through the existing CelesteShell bridge. This iteration does not include upstream opening animation, audio, third-party online services, viewer disassembly, or a bookshelf. The album art is in the detail panel; the original cassette interior remains the Rhine model.

Credits and licenses: `LICENSE-RhineLabUI.txt`, deployed `RHINE-NOTICE.txt` (also includes Three.js license).
