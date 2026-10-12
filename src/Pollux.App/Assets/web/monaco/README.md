# Vendored Monaco bundle

Monaco Editor 0.57.0 (MIT), the AMD build from the npm package `monaco-editor`, folder `min/vs`. Version also in `README.md` (third-party table) and `SECURITY.md`. Why vendored whole: `docs/adr/0004-webview2-for-editor-and-terminal.md`. Licence texts: `LICENSE.txt`, `ThirdPartyNotices.txt`. Never edit files here by hand; they are minified.

## What loads at runtime

`notes.html` (Notes editor) and `preview.html` (Quick Look, Preview Pane) both:

1. link `vs/editor/editor.main.css`,
2. load `vs/loader.js` and `require.config({ paths: { vs: 'monaco/vs' } })`,
3. `require(['vs/editor/editor.main'])`.

`editor.main.js` pulls in the hashed chunks next to it in `vs/` (`editor-*.js`, `monaco.contribution-*.js`, one `<lang>-*.js` per language, `vs/language/*/monaco.contribution.js`).

Workers are all files in `vs/assets/`: `editor.worker`, `editorWebWorkerMain`, `json.worker`, `css.worker`, `html.worker`, `ts.worker`. `editor.main.js` sets its own `MonacoEnvironment.getWorker`, so a `getWorkerUrl` in the HTML pages has no effect. The worker copies directly in `vs/` (`json.worker-*.js` etc.) are wrapper modules that reference the `vs/assets/` files; keep both.

The app serves the folder from disk via `https://pollux.assets`. The pages' CSP allows `worker-src blob: https://pollux.assets`.

## Regenerate

1. Install the new version, e.g. `npm pack monaco-editor@<version>`, and unpack it.
2. Replace the contents of this folder with the package's `min/vs`, plus `LICENSE.txt` and `ThirdPartyNotices.txt` from the package root.
3. Delete the dead files again (`git show 1d2dd11` lists them): `vs/nls/lang/`, and any non-hashed AMD workers under `vs/language/*/` or `vs/editor/editor.worker.js`, if the new build brings them back. Check first that nothing in `vs/assets/` or the pages references them.
4. Update the version in `README.md` and `SECURITY.md`.

## Check after changing the bundle

- Notes window opens, the editor appears, `#` tag completion works (the page posts `ready`).
- The Notes window posts no `error` message. `preview.html` posts only `ready`, so it does not report worker failures.
- Worker check: open a `.json` file with a syntax error in Quick Look and confirm the diagnostics (squiggle) appear and folding works. Highlighting alone proves nothing, it comes from Monarch tokenizers, not workers. Check the WebView2 devtools console for worker errors.
- No request leaves `https://pollux.assets` (see `WebViewPolicy.cs`).

This file ships in the output (`Assets\**` in `Pollux.App.csproj`); it is harmless there.
