---
name: unity-bridge
description: Drive a running Unity Editor from outside it via the Claude Bridge file queue - recompile and read compile errors, dump the scene graph, inspect GameObjects, screenshot the game or scene view, toggle play mode, run menu items. Trigger whenever working in a Unity project that has com.blue.claude-bridge installed and the task touches the live editor - after editing any .cs file, or on asks like "did that compile", "what's in the scene", "show me what it looks like", "run it", "check the console", "why is this pink". Do NOT trigger for Unity projects without the bridge package installed, or for headless -batchmode builds and CI, which do not need it.
---

# Unity Bridge

Talk to a running Unity Editor through a file queue. Handlers execute on Unity's
main thread; the queue is on disk, so it survives domain reloads.

## Check it is alive first

```powershell
.\tools\unity.ps1 ping
```

A timeout means the editor is closed or the bridge failed to compile — it does
**not** mean the command was lost. Queued commands sit on disk and fire when the
editor next ticks. See Troubleshooting.

## The main loop: edit, sync, read errors

After changing any `.cs` file, always run:

```powershell
.\tools\unity.ps1 sync
```

`sync` refreshes assets, waits out the recompile, and prints errors with file,
line, and column. This replaces guessing whether code compiles — **run it after
every script edit** rather than assuming, and never report a change as working
until `sync` comes back clean.

Unity does not auto-import while unfocused, so editing a file on disk does
nothing until `sync` (or `refresh`) forces it.

## Commands

```powershell
.\tools\unity.ps1 ping
.\tools\unity.ps1 status                                          # compiling/playing/error counts
.\tools\unity.ps1 project                                         # render pipeline, build scenes
.\tools\unity.ps1 commands                                        # authoritative list
.\tools\unity.ps1 console    -CmdArgs @{ type='Error'; count=20 }
.\tools\unity.ps1 hierarchy  -CmdArgs @{ depth=3 }
.\tools\unity.ps1 inspect    -CmdArgs @{ path='Main Camera' }     # path is Parent/Child/GrandChild
.\tools\unity.ps1 screenshot -CmdArgs @{ mode='game'; width=1600; height=900 }
.\tools\unity.ps1 assets     -CmdArgs @{ filter='t:Material' }
.\tools\unity.ps1 menu       -CmdArgs @{ item='Assets/Refresh' }
.\tools\unity.ps1 play
.\tools\unity.ps1 stop
.\tools\unity.ps1 playmode   -CmdArgs @{ domainReload=$false }
```

Add `-Raw` for the unparsed envelope, `-TimeoutSec N` for slow operations.

## Verifying visuals

Headless Unity cannot render, but the bridge can — `screenshot` writes a PNG that
you then read back as an image. Use it to actually confirm a visual change rather
than inferring one from code.

```powershell
.\tools\unity.ps1 screenshot -CmdArgs @{ mode='game'; path='.claude-bridge/shots/check.png' }
```

Then read that file. `mode='scene'` captures the Scene view camera instead.

It renders one camera to a RenderTexture, so a Screen Space **Overlay** canvas
will not appear. Switch the canvas to Screen Space **Camera** to capture UI.

## Play mode requires window focus

`play`/`stop` are accepted and run `EnterPlaymode()`, but Unity stays in edit mode
when the editor window is in the background — silently, with no error. Focus it
first, matching on window title because several editors may be running:

```powershell
$ed = Get-Process Unity | Where-Object { $_.MainWindowTitle -like 'MyProject*' }
(New-Object -ComObject WScript.Shell).AppActivate($ed.Id)
```

The response returns *before* the state changes (the flip is deferred so the
reply survives the domain reload). Always poll `status` to confirm `isPlaying`
rather than assuming it worked.

## Gotchas

- **Console history starts at domain load.** Entries logged before the current
  domain are invisible. After a recompile, the buffer is fresh — use `sync`'s
  `since` handling rather than expecting old messages.
- **`assets` returns `mainAssetType`** — the file's type, not the matched thing.
  `FindAssets` matches embedded sub-assets and returns the containing file's
  GUID, so `t:Material` returning `.fbx` and `.ttf` paths is correct, not a bug.
- **Unfocused editors tick slowly.** Unity throttles `EditorApplication.update`
  in the background; expect everything to be slower when Unity is not foreground.
- **Domain reload may be disabled** on the project (check `playmode`). If so,
  `static` fields do not reset on play. A bug that only reproduces on the second
  play is this, nearly every time.
- **Never edit the bridge's own code while relying on it.** A compile error in
  `Packages/com.blue.claude-bridge` kills the channel. It has its own assembly so
  gameplay errors cannot, but its own errors can.

## Adding a command

Cheaper than working around a missing one. Add a `case` to `Commands.Dispatch`
and a static method in `Commands.cs`. Handlers are on the main thread and must
return only plain types — dictionaries, lists, strings, numbers, bools. A
`UnityEngine.Object` recurses into engine internals and blows up the serializer.
Then `sync` to compile it.

## Troubleshooting

**`ping` times out.**
1. Is the editor open on *this* project? `Get-Process Unity | Select MainWindowTitle`
2. Is the bridge armed in the current domain? Check `.claude-bridge/bridge-alive.json`.
3. If absent, the bridge did not compile. Read
   `%LOCALAPPDATA%\Unity\Editor\Editor.log` — it always works, no bridge needed.

**Commands all fire at once after a delay.** Normal. They queued on disk while
the editor was closed or reloading.

**Everything is stale after editing files.** You skipped `sync`. Unity did not
import them.
