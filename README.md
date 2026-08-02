# Claude Bridge

Drive a running Unity Editor from outside it — read the console, dump the scene
graph, force a recompile and get the errors back, take a screenshot — without
alt-tabbing into the editor.

Built for agent use (Claude Code and friends), but it is just files and JSON, so
anything that can write a file can drive it.

```powershell
.\tools\unity.ps1 sync          # recompile, report errors
.\tools\unity.ps1 hierarchy
.\tools\unity.ps1 screenshot
```

## Why a file queue instead of a socket

Every script recompile and most play-mode transitions trigger a Unity **domain
reload**, which tears down the managed domain: static state wiped, threads
killed, sockets dropped. An HTTP or WebSocket server hosted inside the editor
therefore dies mid-request on a regular basis, which is the root cause of the
disconnect complaints against socket-based Unity MCP servers.

Here the queue is on disk. A domain reload only pauses draining; queued commands
are picked up when the new domain loads. There is no connection to lose, no port
to bind, no firewall prompt, and no URL ACL. You can also just look in the folder
to see what is happening.

The cost is latency: roughly 150 ms per round trip instead of ~5 ms. For agent
use — a handful of commands a minute — that is irrelevant.

## Install

Unity 6000.0+. Package Manager → Add package from git URL:

```
https://github.com/joeaguilar/unity-claude-bridge.git
```

Or in `Packages/manifest.json`, pinned to a tag:

```jsonc
"com.blue.claude-bridge": "https://github.com/joeaguilar/unity-claude-bridge.git#v0.1.0"
```

Pinning is recommended: an upstream change should never break a project
mid-session.

On first load the package copies its PowerShell wrapper to `<project>/tools/unity.ps1`.
It will not overwrite an existing one; if yours differs from the packaged copy it
logs a note and leaves yours alone. Add `.claude-bridge/` to your `.gitignore` —
it is transient IPC, not project state.

## Protocol

```
in/<id>.json    {"cmd":"hierarchy","args":{"depth":3}}
out/<id>.json   {"ok":true,"result":{...}}  |  {"ok":false,"error":"..."}
```

Both sides write a `.tmp` and then move it into place, so neither ever reads a
half-written file. The editor drains `in/` from `EditorApplication.update`, which
is what makes it legal for handlers to touch `UnityEditor` APIs — they are on the
main thread. Dispatch is skipped while `isCompiling` or `isUpdating`.

## Commands

| Command | Args | Does |
|---|---|---|
| `ping` | | liveness + Unity version |
| `status` | | compiling / playing / error counts |
| `project` | | product name, render pipeline, build scenes |
| `refresh` | `importAll?` | `AssetDatabase.Refresh` |
| `console` | `count? type? since?` | recent log entries |
| `hierarchy` | `depth? components?` | open scene graph |
| `inspect` | `path` `depth?` | components + serialized properties |
| `screenshot` | `mode? width? height? path?` | `mode`: `game`\|`scene`, writes PNG |
| `play` / `stop` | | toggle play mode |
| `playmode` | `domainReload? sceneReload?` | read/set Enter Play Mode Options |
| `menu` | `item` | `EditorApplication.ExecuteMenuItem` |
| `assets` | `filter? limit?` | `AssetDatabase.FindAssets` |
| `packages` | `filter?` | registered packages, source, and UPM errors |
| `tests` | `mode? filter? category?` | start a run; `mode`: `edit`\|`play` |
| `testresults` | `runId?` | poll a run; defaults to the latest |
| `commands` | | list the above |

Two wrapper-side macros are not bridge commands:

- **`sync`** — refresh, wait out the compile, print any errors. The main loop when
  editing scripts externally.
- **`test`** — start a run, poll to completion, print failures and a summary.

```powershell
.\tools\unity.ps1 test
.\tools\unity.ps1 test -CmdArgs @{ mode = 'play' }
.\tools\unity.ps1 test -CmdArgs @{ category = 'Combat' }
.\tools\unity.ps1 test -CmdArgs @{ filter = 'MyNamespace.MyTests.OneTest' }
```

### How tests report

`tests` returns a `runId` immediately and results stream to
`.claude-bridge/tests/<runId>.json` until `finished` is true. They are not
returned from the starting command, for two reasons: `TestRunnerApi` is
asynchronous, so blocking the pump would deadlock the loop that delivers the
result; and a PlayMode run reloads the domain mid-flight, wiping static state.
The run id lives in `SessionState` and callbacks re-register on each domain load,
so a run survives the reload.

## Known limits

- **Unity must be open** on the project. Otherwise commands queue on disk until
  it is, then all fire at once. A `ping` timeout means the editor is closed or
  the bridge failed to compile.
- **`play`/`stop` need the editor window focused.** With Unity in the background
  the command is accepted and `EnterPlaymode()` runs, but the editor stays in
  edit mode. Focus it first:
  ```powershell
  $ed = Get-Process Unity | Where-Object { $_.MainWindowTitle -like 'MyProject*' }
  (New-Object -ComObject WScript.Shell).AppActivate($ed.Id)
  ```
  Match on window title — more than one editor can be running.
- **Console history starts at domain load.** Capture is via
  `Application.logMessageReceived`. Reading the pre-existing Console window would
  mean reflecting into the internal `LogEntries` API, which breaks across Unity
  versions.
- **Unfocused editors tick slowly.** Unity throttles `EditorApplication.update`
  in the background, so everything is slower when Unity is not foreground.
- **Screenshots render one camera to a RenderTexture**, rather than using
  `ScreenCapture.CaptureScreenshot`, which is async and play-mode only. A Screen
  Space *Overlay* canvas will not appear; switch it to Screen Space *Camera* to
  capture UI.
- **`assets` reports `mainAssetType`**, the type of the file — not of the thing
  that matched. `FindAssets` matches embedded sub-assets and returns the
  containing file's GUID, so `t:Material` legitimately returns `.fbx` and `.ttf`
  paths.
- **If the bridge itself fails to compile, the channel is gone.** It lives in its
  own assembly so your gameplay errors cannot take it down, but a break in its
  own code needs the fallback below.

## Fallback

`%LOCALAPPDATA%\Unity\Editor\Editor.log` has the compile errors and needs no
bridge and no running editor.

For work that must run headless, batch mode is unaffected:

```powershell
& "C:\Program Files\Unity\Hub\Editor\<version>\Editor\Unity.exe" `
  -batchmode -quit -projectPath "<project>" -executeMethod Your.Static.Method -logFile -
```

## Adding a command

One `case` in `Commands.Dispatch` and one static method. Handlers run on the main
thread and must return only plain types — dictionaries, lists, strings, numbers,
bools. Returning a `UnityEngine.Object` recurses into engine internals and blows
up the serializer.

**Commit the `.meta` file for every new source file.** Unity generates metas for
embedded packages but never for immutable ones in `Library/PackageCache`, so a
git-installed package missing them does not compile — every file is silently
skipped with `has no meta file, but it's in an immutable folder`.

`dev/` automates the whole loop, including that gate:

```powershell
.\dev\embed.ps1   -Project C:\path\to\HostProject    # then RESTART the editor
# edit Editor\*.cs, compile with the host project's .\tools\unity.ps1 sync
.\dev\publish.ps1 -Project C:\path\to\HostProject -Version 0.4.0
# review the diff, then commit, tag vX.Y.Z, and push before the host can resolve it
```

`embed.ps1` drops the dependency from `manifest.json` **and** `packages-lock.json`
— the lock file pins the resolved git package independently, so removing only the
manifest entry leaves UPM loading the cached copy while the embedded folder
becomes a same-name duplicate that Package Manager reports as invalid.

Restarting the editor after `embed.ps1` is not optional: Unity does not reliably
re-resolve packages when `manifest.json` changes underneath a running editor.

`publish.ps1` refuses to publish if any file is missing its `.meta`.
`dev/check-metas.ps1` runs that check standalone.

## Agent skill

`Skill~/unity-bridge/` holds a Claude Code skill covering the workflow and the
gotchas above. Copy it to `~/.claude/skills/unity-bridge/` to have it available
in any project.

## Not yet implemented

- `scene` — open/save/new
- Reading Console entries that predate the current domain
