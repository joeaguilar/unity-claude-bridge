# Changelog

## [0.3.0] - 2026-08-02

### Added
- `packages` — registered packages with version, source, resolved path and any
  UPM errors. Package Manager reports a package as "invalid" without saying why
  anywhere in the editor log; the reason lives in `PackageInfo.errors`, and this
  surfaces it.
- `dev/embed.ps1`, `dev/publish.ps1`, `dev/check-metas.ps1` — the development
  loop. A git-installed package resolves into `Library/PackageCache`, which Unity
  marks immutable, so edits there are discarded and no `.meta` files are
  generated. Changing this package means embedding it in a host project and
  copying back, and these automate that with a meta-completeness gate on publish.

### Notes for anyone scripting Unity from PowerShell
Three traps, all handled in `dev/`:
- PowerShell 5.1 `-Encoding utf8` writes a **BOM**. Unity rejects a BOM in
  `manifest.json` (`Non-whitespace before {[`, char 65279) and then resolves *no
  packages at all*, which surfaces as packages showing "invalid" rather than as
  an encoding error.
- Removing a dependency from `manifest.json` is not enough: `packages-lock.json`
  independently pins the resolved git package, so UPM keeps loading the cached
  copy and an embedded folder of the same name becomes a duplicate.
- Unity does not reliably re-resolve when `manifest.json` changes underneath a
  running editor. Restart it.

## [0.2.0] - 2026-08-02

### Added
- `tests` / `testresults` — run EditMode or PlayMode tests in the live editor via
  `TestRunnerApi`, filtered by test name or category. Results stream to
  `.claude-bridge/tests/<runId>.json`. The wrapper gains a `test` macro that
  starts a run, polls to completion, and prints failures with their messages.
- MIT `LICENSE`.

### Notes
- Results go to disk rather than being returned from the starting command for two
  reasons: `TestRunnerApi` is asynchronous, so blocking the pump would deadlock
  the very loop that delivers the result; and a PlayMode run reloads the domain
  mid-flight, which wipes static state. The run id lives in `SessionState` and
  callbacks re-register on every domain load.
- `TestRunnerApi` is a `ScriptableObject` and its callback registry survives
  domain reloads, so re-registering each load stacks a duplicate subscription and
  every test reports twice. Callbacks are now dropped in
  `AssemblyReloadEvents.beforeAssemblyReload`, and `TestFinished` dedupes by test
  full name as a backstop.
- Run totals come from `RunFinished`'s authoritative counts, not from the
  incremental tally, which a mid-run domain reload can desynchronize.
- Adds a `com.unity.test-framework` dependency.

## [0.1.1] - 2026-08-02

### Fixed
- Ship `.meta` files. Without them a git-installed package does not compile at
  all: Unity generates metas for embedded packages but never for immutable ones
  in `Library/PackageCache`, and logs `has no meta file, but it's in an immutable
  folder. The asset will be ignored.` for every source file. **0.1.0 is broken —
  use 0.1.1 or later.**

## [0.1.0] - 2026-08-02

Initial release. Extracted from the ChoomDoom project, where it was verified
end to end against a live Unity 6000.4.2f1 / URP editor.

### Added
- File-queue bridge at `<project>/.claude-bridge/`, drained from
  `EditorApplication.update` so handlers run on the main thread. Atomic
  `.tmp`-then-move writes on both sides.
- Commands: `ping`, `status`, `project`, `refresh`, `console`, `hierarchy`,
  `inspect`, `screenshot`, `play`, `stop`, `playmode`, `menu`, `assets`,
  `commands`.
- `tools/unity.ps1` wrapper, auto-installed from `Tools~/` on first load, with a
  `sync` macro that refreshes, waits out the compile, and reports errors.
  ASCII-only, for PowerShell 5.1.
- Claude Code skill under `Skill~/unity-bridge/`.

### Notes
- `play`/`stop` use `EnterPlaymode()`/`ExitPlaymode()` rather than assigning
  `EditorApplication.isPlaying`, which silently no-ops when the editor is
  unfocused. The documented API still requires window focus to take effect.
- Screenshots render a camera to a RenderTexture rather than using
  `ScreenCapture.CaptureScreenshot`, which is async and play-mode only.
