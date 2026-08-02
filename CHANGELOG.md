# Changelog

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
