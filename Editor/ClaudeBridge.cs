using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Blue.ClaudeBridge
{
    /// <summary>
    /// File-queue command bridge between an external agent and the Unity Editor.
    ///
    /// Protocol:
    ///   request   .claude-bridge/in/&lt;id&gt;.json   {"cmd":"hierarchy","args":{...}}
    ///   response  .claude-bridge/out/&lt;id&gt;.json  {"ok":true,"result":...} | {"ok":false,"error":"..."}
    ///
    /// Both sides write to a .tmp sibling and then move into place, so a reader never
    /// observes a partially written file.
    ///
    /// Everything runs from EditorApplication.update, which ticks on the main thread --
    /// this is what makes it legal to touch UnityEditor/UnityEngine APIs in handlers.
    /// The queue lives on disk, so a domain reload only pauses draining; pending
    /// commands are picked up once the new domain finishes loading.
    /// </summary>
    [InitializeOnLoad]
    public static class ClaudeBridge
    {
        const double PollInterval = 0.15;

        static readonly string Root;
        static readonly string InDir;
        static readonly string OutDir;
        static double _nextPoll;

        public static string RootDir { get { return Root; } }

        static ClaudeBridge()
        {
            Root = Path.Combine(Directory.GetParent(Application.dataPath).FullName, ".claude-bridge");
            InDir = Path.Combine(Root, "in");
            OutDir = Path.Combine(Root, "out");

            try
            {
                Directory.CreateDirectory(InDir);
                Directory.CreateDirectory(OutDir);
            }
            catch (Exception e)
            {
                Debug.LogError("[ClaudeBridge] could not create queue dirs: " + e.Message);
                return;
            }

            ConsoleCapture.Install();
            EnsureWrapperInstalled();

            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;

            // Heartbeat file so an external caller can tell the bridge is armed in
            // the current domain without issuing a command.
            TouchHeartbeat();
        }

        /// <summary>
        /// Copy the PowerShell wrapper out of the package into &lt;project&gt;/tools/ so a
        /// bare UPM install is self-sufficient.
        ///
        /// The wrapper ships under Tools~/. The trailing tilde keeps Unity's importer
        /// out of it, so it generates no .meta files and never appears in the Project
        /// window, but it is still on disk and readable.
        ///
        /// An existing wrapper is never overwritten -- it may have local edits. If it
        /// differs from the packaged copy we say so once and leave it alone.
        /// </summary>
        static void EnsureWrapperInstalled()
        {
            try
            {
                var pkg = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(ClaudeBridge).Assembly);
                if (pkg == null) return; // embedded in Assets/ rather than installed as a package

                string src = Path.Combine(pkg.resolvedPath, "Tools~", "unity.ps1");
                if (!File.Exists(src)) return;

                string projectRoot = Directory.GetParent(Application.dataPath).FullName;
                string dstDir = Path.Combine(projectRoot, "tools");
                string dst = Path.Combine(dstDir, "unity.ps1");

                if (File.Exists(dst))
                {
                    if (File.ReadAllText(dst) != File.ReadAllText(src))
                        Debug.Log("[ClaudeBridge] tools/unity.ps1 differs from the packaged copy (v"
                                  + pkg.version + "). Delete it to take the packaged version.");
                    return;
                }

                Directory.CreateDirectory(dstDir);
                File.Copy(src, dst);
                Debug.Log("[ClaudeBridge] installed wrapper at tools/unity.ps1 (v" + pkg.version + ")");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ClaudeBridge] could not install tools/unity.ps1: " + e.Message);
            }
        }

        static void TouchHeartbeat()
        {
            try
            {
                var payload = new JObject
                {
                    ["armedAtEditorTime"] = EditorApplication.timeSinceStartup,
                    ["unityVersion"] = Application.unityVersion,
                    ["project"] = Application.productName,
                };
                File.WriteAllText(Path.Combine(Root, "bridge-alive.json"), payload.ToString(Formatting.Indented));
            }
            catch { /* heartbeat is best-effort */ }
        }

        static void Tick()
        {
            if (EditorApplication.timeSinceStartup < _nextPoll) return;
            _nextPoll = EditorApplication.timeSinceStartup + PollInterval;

            // Don't dispatch mid-compile or mid-import; the domain is about to be
            // torn down and handler results would be unreliable. Commands just wait.
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;

            string[] files;
            try { files = Directory.GetFiles(InDir, "*.json"); }
            catch { return; }

            for (int i = 0; i < files.Length; i++)
                Handle(files[i]);
        }

        static void Handle(string path)
        {
            string id = Path.GetFileNameWithoutExtension(path);

            string body;
            try
            {
                body = File.ReadAllText(path);
                File.Delete(path);
            }
            catch
            {
                // Locked or still landing -- leave it for the next tick.
                return;
            }

            object result = null;
            string error = null;

            try
            {
                var req = JObject.Parse(body);
                var cmd = req["cmd"] != null ? req["cmd"].ToString() : null;
                if (string.IsNullOrEmpty(cmd))
                    throw new Exception("request has no 'cmd' field");

                var args = req["args"] as JObject ?? new JObject();
                result = Commands.Dispatch(cmd, args);
            }
            catch (Exception e)
            {
                error = e.Message + "\n" + e.StackTrace;
            }

            Respond(id, result, error);
        }

        static void Respond(string id, object result, string error)
        {
            try
            {
                var payload = new JObject();
                payload["ok"] = error == null;
                payload["error"] = error == null ? (JToken)JValue.CreateNull() : new JValue(error);
                payload["result"] = result == null ? (JToken)JValue.CreateNull() : JToken.FromObject(result);

                string tmp = Path.Combine(OutDir, id + ".tmp");
                string fin = Path.Combine(OutDir, id + ".json");

                File.WriteAllText(tmp, payload.ToString(Formatting.Indented));
                if (File.Exists(fin)) File.Delete(fin);
                File.Move(tmp, fin);
            }
            catch (Exception e)
            {
                Debug.LogError("[ClaudeBridge] failed to write response for " + id + ": " + e.Message);
            }
        }
    }
}
