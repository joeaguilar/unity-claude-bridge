using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Blue.ClaudeBridge
{
    /// <summary>
    /// Command handlers. Every one of these runs on the main thread (dispatched from
    /// EditorApplication.update), so calling UnityEditor/UnityEngine APIs is legal here.
    ///
    /// Handlers must return only plain types -- dictionaries, lists, strings, numbers,
    /// bools -- because the result is serialized with JToken.FromObject. Returning a
    /// UnityEngine.Object would recurse into engine internals and blow up.
    /// </summary>
    public static class Commands
    {
        public static object Dispatch(string cmd, JObject a)
        {
            switch (cmd)
            {
                case "ping":       return Ping();
                case "status":     return Status();
                case "project":    return Project();
                case "refresh":    return Refresh(Arg(a, "importAll", false));
                case "console":    return Console(Arg(a, "count", 50), Arg(a, "type", (string)null), Arg(a, "since", 0));
                case "hierarchy":  return Hierarchy(Arg(a, "depth", 4), Arg(a, "components", true));
                case "inspect":    return Inspect(Req(a, "path"), Arg(a, "depth", 2));
                case "screenshot": return Screenshot(
                                       Arg(a, "mode", "game"),
                                       Arg(a, "width", 1280),
                                       Arg(a, "height", 720),
                                       Arg(a, "path", (string)null));
                case "play":       return SetPlaying(true);
                case "stop":       return SetPlaying(false);
                case "menu":       return Menu(Req(a, "item"));
                case "assets":     return Assets(Arg(a, "filter", ""), Arg(a, "limit", 100));
                case "playmode":   return PlayModeOptions(a);
                case "commands":   return ListCommands();
                default:
                    throw new Exception("unknown cmd '" + cmd + "'. Try {\"cmd\":\"commands\"}.");
            }
        }

        // ---------------------------------------------------------------- basics

        static object Ping()
        {
            return new Dictionary<string, object>
            {
                { "pong", true },
                { "unityVersion", Application.unityVersion },
                { "project", Application.productName },
                { "editorTime", Math.Round(EditorApplication.timeSinceStartup, 2) },
            };
        }

        static object Status()
        {
            return new Dictionary<string, object>
            {
                { "isCompiling", EditorApplication.isCompiling },
                { "isUpdating", EditorApplication.isUpdating },
                { "isPlaying", EditorApplication.isPlaying },
                { "isPaused", EditorApplication.isPaused },
                { "isPlayingOrWillChange", EditorApplication.isPlayingOrWillChangePlaymode },
                { "errors", ConsoleCapture.CountOf(LogType.Error) + ConsoleCapture.CountOf(LogType.Exception) },
                { "warnings", ConsoleCapture.CountOf(LogType.Warning) },
                { "latestLogSeq", ConsoleCapture.LatestSeq },
                { "activeScene", SceneManager.GetActiveScene().name },
            };
        }

        static object Project()
        {
            var scenes = new List<object>();
            foreach (var s in EditorBuildSettings.scenes)
                scenes.Add(new Dictionary<string, object> { { "path", s.path }, { "enabled", s.enabled } });

            var rp = GraphicsSettings.defaultRenderPipeline;

            return new Dictionary<string, object>
            {
                { "productName", Application.productName },
                { "companyName", Application.companyName },
                { "unityVersion", Application.unityVersion },
                { "dataPath", Application.dataPath },
                { "renderPipeline", rp == null ? "Built-in" : rp.GetType().Name },
                { "buildScenes", scenes },
                { "openScene", SceneManager.GetActiveScene().path },
                { "bridgeRoot", ClaudeBridge.RootDir },
            };
        }

        static object ListCommands()
        {
            return new List<object>
            {
                "ping                                       - liveness + version",
                "status                                     - compiling/playing/error counts",
                "project                                    - project + render pipeline info",
                "refresh   {importAll?}                     - AssetDatabase.Refresh, then poll status",
                "console   {count?,type?,since?}            - recent log entries",
                "hierarchy {depth?,components?}             - open scene graph",
                "inspect   {path,depth?}                    - components + serialized props of one GameObject",
                "screenshot{mode?,width?,height?,path?}     - mode: game|scene, writes PNG",
                "play / stop                                - toggle play mode",
                "menu      {item}                           - EditorApplication.ExecuteMenuItem",
                "assets    {filter?,limit?}                 - AssetDatabase.FindAssets",
                "playmode  {domainReload?,sceneReload?}     - read/set Enter Play Mode Options",
                "commands                                   - this list",
            };
        }

        // ---------------------------------------------------------------- assets

        static object Refresh(bool importAll)
        {
            AssetDatabase.Refresh(importAll
                ? ImportAssetOptions.ForceUpdate
                : ImportAssetOptions.Default);

            // Compilation is asynchronous. Blocking here would deadlock the pump that
            // dispatched us, so return immediately and let the caller poll `status`
            // until isCompiling flips false, then read `console` for errors.
            return new Dictionary<string, object>
            {
                { "triggered", true },
                { "isCompiling", EditorApplication.isCompiling },
                { "logSeqBefore", ConsoleCapture.LatestSeq },
                { "note", "poll 'status' until isCompiling=false, then 'console' with since=logSeqBefore" },
            };
        }

        static object Assets(string filter, int limit)
        {
            var guids = AssetDatabase.FindAssets(filter ?? "");
            var list = new List<object>();

            for (int i = 0; i < guids.Length && list.Count < limit; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (string.IsNullOrEmpty(path)) continue;

                var t = AssetDatabase.GetMainAssetTypeAtPath(path);
                list.Add(new Dictionary<string, object>
                {
                    { "path", path },
                    { "guid", guids[i] },
                    { "mainAssetType", t == null ? "?" : t.Name },
                });
            }

            return new Dictionary<string, object>
            {
                { "total", guids.Length },
                { "returned", list.Count },
                { "assets", list },
                // FindAssets matches sub-assets too, but returns the GUID of the file
                // that contains them. A "t:Material" search legitimately turns up .fbx
                // and .ttf files, whose mainAssetType is GameObject or Font -- the
                // material is embedded inside. mainAssetType describes the file, not
                // the thing that matched the filter.
                { "note", "mainAssetType is the file's main asset; filters can match embedded sub-assets" },
            };
        }

        // ---------------------------------------------------------------- console

        static object Console(int count, string type, int since)
        {
            var entries = ConsoleCapture.Recent(count, type, since);
            return new Dictionary<string, object>
            {
                { "latestSeq", ConsoleCapture.LatestSeq },
                { "returned", entries.Count },
                { "entries", entries },
            };
        }

        // ---------------------------------------------------------------- scene graph

        static object Hierarchy(int depth, bool withComponents)
        {
            var scenes = new List<object>();

            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;

                var roots = new List<object>();
                foreach (var go in scene.GetRootGameObjects())
                    roots.Add(NodeOf(go, depth, withComponents));

                scenes.Add(new Dictionary<string, object>
                {
                    { "name", scene.name },
                    { "path", scene.path },
                    { "isDirty", scene.isDirty },
                    { "rootCount", roots.Count },
                    { "roots", roots },
                });
            }

            return new Dictionary<string, object> { { "scenes", scenes } };
        }

        static object NodeOf(GameObject go, int depth, bool withComponents)
        {
            var node = new Dictionary<string, object>
            {
                { "name", go.name },
                { "active", go.activeSelf },
                { "tag", go.tag },
                { "layer", LayerMask.LayerToName(go.layer) },
            };

            if (withComponents)
            {
                var comps = new List<string>();
                foreach (var c in go.GetComponents<Component>())
                    comps.Add(c == null ? "<missing script>" : c.GetType().Name);
                node["components"] = comps;
            }

            int childCount = go.transform.childCount;
            node["childCount"] = childCount;

            if (depth > 0 && childCount > 0)
            {
                var kids = new List<object>();
                for (int i = 0; i < childCount; i++)
                    kids.Add(NodeOf(go.transform.GetChild(i).gameObject, depth - 1, withComponents));
                node["children"] = kids;
            }
            else if (childCount > 0)
            {
                node["children"] = "<truncated: raise depth>";
            }

            return node;
        }

        static object Inspect(string path, int depth)
        {
            var go = FindByPath(path);
            if (go == null) throw new Exception("no GameObject at path '" + path + "'");

            var comps = new List<object>();
            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null)
                {
                    comps.Add(new Dictionary<string, object> { { "type", "<missing script>" } });
                    continue;
                }

                var props = new Dictionary<string, object>();
                try
                {
                    var so = new SerializedObject(c);
                    var it = so.GetIterator();
                    bool enterChildren = true;
                    int guard = 0;

                    while (it.NextVisible(enterChildren) && guard++ < 400)
                    {
                        enterChildren = it.depth < depth;
                        if (it.depth > depth) continue;
                        if (it.propertyPath == "m_Script") continue;
                        props[it.propertyPath] = PropValue(it);
                    }
                }
                catch (Exception e)
                {
                    props["<error>"] = e.Message;
                }

                comps.Add(new Dictionary<string, object>
                {
                    { "type", c.GetType().Name },
                    { "properties", props },
                });
            }

            return new Dictionary<string, object>
            {
                { "path", path },
                { "name", go.name },
                { "active", go.activeSelf },
                { "components", comps },
            };
        }

        static object PropValue(SerializedProperty p)
        {
            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer:   return p.intValue;
                case SerializedPropertyType.Boolean:   return p.boolValue;
                case SerializedPropertyType.Float:     return p.floatValue;
                case SerializedPropertyType.String:    return p.stringValue;
                case SerializedPropertyType.Enum:      return p.enumValueIndex >= 0 && p.enumDisplayNames != null
                                                              && p.enumValueIndex < p.enumDisplayNames.Length
                                                              ? p.enumDisplayNames[p.enumValueIndex]
                                                              : p.enumValueIndex.ToString();
                case SerializedPropertyType.Vector2:   return V(p.vector2Value.x, p.vector2Value.y);
                case SerializedPropertyType.Vector3:   return V(p.vector3Value.x, p.vector3Value.y, p.vector3Value.z);
                case SerializedPropertyType.Vector4:   return V(p.vector4Value.x, p.vector4Value.y, p.vector4Value.z, p.vector4Value.w);
                case SerializedPropertyType.Quaternion:return V(p.quaternionValue.x, p.quaternionValue.y, p.quaternionValue.z, p.quaternionValue.w);
                case SerializedPropertyType.Color:     return "#" + ColorUtility.ToHtmlStringRGBA(p.colorValue);
                case SerializedPropertyType.ObjectReference:
                    return p.objectReferenceValue == null
                        ? null
                        : p.objectReferenceValue.name + " (" + p.objectReferenceValue.GetType().Name + ")";
                case SerializedPropertyType.Generic:
                    return p.isArray ? ("<array len=" + p.arraySize + ">") : "<generic>";
                default:
                    return "<" + p.propertyType + ">";
            }
        }

        static List<float> V(params float[] xs) { return new List<float>(xs); }

        static GameObject FindByPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var parts = path.Split('/');

            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                var scene = SceneManager.GetSceneAt(s);
                if (!scene.isLoaded) continue;

                foreach (var root in scene.GetRootGameObjects())
                {
                    if (root.name != parts[0]) continue;

                    var cur = root.transform;
                    bool ok = true;

                    for (int i = 1; i < parts.Length && ok; i++)
                    {
                        var next = cur.Find(parts[i]);
                        if (next == null) ok = false; else cur = next;
                    }

                    if (ok) return cur.gameObject;
                }
            }
            return null;
        }

        // ---------------------------------------------------------------- capture

        static object Screenshot(string mode, int width, int height, string path)
        {
            if (width <= 0 || height <= 0) throw new Exception("width/height must be positive");

            Camera cam = null;
            if (string.Equals(mode, "scene", StringComparison.OrdinalIgnoreCase))
            {
                var sv = SceneView.lastActiveSceneView;
                if (sv == null) throw new Exception("no active Scene view to capture");
                cam = sv.camera;
            }
            else
            {
                cam = Camera.main;
                if (cam == null)
                {
                    var all = UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsSortMode.None);
                    if (all.Length > 0) cam = all[0];
                }
                if (cam == null) throw new Exception("no camera found in the open scene(s)");
            }

            if (string.IsNullOrEmpty(path))
                path = Path.Combine(ClaudeBridge.RootDir, "shots", "shot.png");

            if (!Path.IsPathRooted(path))
                path = Path.Combine(Directory.GetParent(Application.dataPath).FullName, path);

            Directory.CreateDirectory(Path.GetDirectoryName(path));

            // Render the camera into an offscreen target and read it back synchronously.
            // ScreenCapture.CaptureScreenshot is asynchronous and only meaningful in play
            // mode, which makes it unusable for a request/response bridge.
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            rt.Create();

            var prevTarget = cam.targetTexture;
            var prevActive = RenderTexture.active;
            Texture2D tex = null;

            try
            {
                cam.targetTexture = rt;
                cam.Render();

                RenderTexture.active = rt;
                tex = new Texture2D(width, height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply(false);

                File.WriteAllBytes(path, tex.EncodeToPNG());
            }
            finally
            {
                cam.targetTexture = prevTarget;
                RenderTexture.active = prevActive;
                if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
            }

            return new Dictionary<string, object>
            {
                { "path", path },
                { "camera", cam.name },
                { "mode", mode },
                { "width", width },
                { "height", height },
                { "bytes", new FileInfo(path).Length },
            };
        }

        // ---------------------------------------------------------------- play mode

        static object SetPlaying(bool play)
        {
            if (EditorApplication.isPlaying == play)
                return new Dictionary<string, object>
                {
                    { "changed", false },
                    { "isPlaying", play },
                    { "note", "already in requested state" },
                };

            // Flipping play mode can trigger a domain reload, which would kill this domain
            // before the response is written. Defer it: delayCall fires after the current
            // update tick, by which point the caller already has its answer.
            //
            // EnterPlaymode/ExitPlaymode rather than assigning EditorApplication.isPlaying:
            // the setter is the legacy path and does not reliably take effect when the
            // editor window is unfocused, which is the normal case when driving it from
            // outside.
            EditorApplication.delayCall += () =>
            {
                if (play) EditorApplication.EnterPlaymode();
                else EditorApplication.ExitPlaymode();
            };

            return new Dictionary<string, object>
            {
                { "changed", true },
                { "requested", play ? "play" : "stop" },
                { "note", "scheduled; a domain reload follows, poll 'status' to confirm" },
            };
        }

        /// <summary>
        /// Read or set Project Settings > Editor > Enter Play Mode Settings.
        ///
        /// Done through the API rather than by editing ProjectSettings/EditorSettings.asset
        /// on disk, because a running editor holds that asset in memory and overwrites
        /// disk edits when it quits.
        ///
        /// Disabling domain reload makes play-mode entry near-instant, at the cost that
        /// static fields no longer reset between plays -- initialize statics explicitly
        /// instead of relying on the reload to clear them.
        /// </summary>
        static object PlayModeOptions(JObject a)
        {
            bool queried = a["domainReload"] == null && a["sceneReload"] == null;

            var before = EditorSettings.enterPlayModeOptions;
            bool beforeEnabled = EditorSettings.enterPlayModeOptionsEnabled;

            if (!queried)
            {
                // Options are expressed as "disable" flags; the args read as the
                // user-facing checkboxes, so they invert.
                bool domainReload = Arg(a, "domainReload",
                    (before & EnterPlayModeOptions.DisableDomainReload) == 0);
                bool sceneReload = Arg(a, "sceneReload",
                    (before & EnterPlayModeOptions.DisableSceneReload) == 0);

                var opts = EnterPlayModeOptions.None;
                if (!domainReload) opts |= EnterPlayModeOptions.DisableDomainReload;
                if (!sceneReload) opts |= EnterPlayModeOptions.DisableSceneReload;

                EditorSettings.enterPlayModeOptionsEnabled = true;
                EditorSettings.enterPlayModeOptions = opts;
                AssetDatabase.SaveAssets();
            }

            var now = EditorSettings.enterPlayModeOptions;

            return new Dictionary<string, object>
            {
                { "changed", !queried },
                { "optionsEnabled", EditorSettings.enterPlayModeOptionsEnabled },
                { "reloadDomain", (now & EnterPlayModeOptions.DisableDomainReload) == 0 },
                { "reloadScene", (now & EnterPlayModeOptions.DisableSceneReload) == 0 },
                { "wasEnabled", beforeEnabled },
                { "rawFlags", now.ToString() },
            };
        }

        static object Menu(string item)
        {
            bool ok = EditorApplication.ExecuteMenuItem(item);
            if (!ok) throw new Exception("menu item not found or refused: '" + item + "'");
            return new Dictionary<string, object> { { "executed", item } };
        }

        // ---------------------------------------------------------------- arg helpers

        static string Req(JObject a, string key)
        {
            var t = a[key];
            if (t == null || t.Type == JTokenType.Null)
                throw new Exception("missing required arg '" + key + "'");
            return t.ToString();
        }

        static string Arg(JObject a, string key, string fallback)
        {
            var t = a[key];
            return (t == null || t.Type == JTokenType.Null) ? fallback : t.ToString();
        }

        static int Arg(JObject a, string key, int fallback)
        {
            var t = a[key];
            if (t == null || t.Type == JTokenType.Null) return fallback;
            int v;
            return int.TryParse(t.ToString(), out v) ? v : fallback;
        }

        static bool Arg(JObject a, string key, bool fallback)
        {
            var t = a[key];
            if (t == null || t.Type == JTokenType.Null) return fallback;
            bool v;
            return bool.TryParse(t.ToString(), out v) ? v : fallback;
        }
    }
}
