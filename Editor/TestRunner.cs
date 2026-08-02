using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace Blue.ClaudeBridge
{
    /// <summary>
    /// Runs EditMode/PlayMode tests in the live editor via TestRunnerApi.
    ///
    /// Two things make this awkward, and both are why results go to disk rather
    /// than being returned from the command that starts the run:
    ///
    /// 1. TestRunnerApi is asynchronous. Blocking the bridge pump waiting for it
    ///    would deadlock, since the pump is what would deliver the result.
    /// 2. A PlayMode run triggers a domain reload, which wipes static state
    ///    mid-run. Callbacks are re-registered on every domain load (this class is
    ///    driven from ClaudeBridge's [InitializeOnLoad]) and the run id lives in
    ///    SessionState, which survives reloads. Results accumulate in a file.
    ///
    /// So `tests` starts a run and returns a runId; poll `testresults` (or read
    /// .claude-bridge/tests/&lt;runId&gt;.json) until "finished" is true.
    /// </summary>
    public static class TestRunner
    {
        const string SessionKeyRunId = "ClaudeBridge.TestRunId";
        const string SessionKeyMode = "ClaudeBridge.TestMode";

        static TestRunnerApi _api;
        static Callbacks _callbacks;

        public static string ResultsDir
        {
            get { return Path.Combine(ClaudeBridge.RootDir, "tests"); }
        }

        /// <summary>Re-registers callbacks. Safe to call on every domain load.</summary>
        public static void Install()
        {
            if (_api != null) return;

            _api = ScriptableObject.CreateInstance<TestRunnerApi>();
            _callbacks = new Callbacks();
            _api.RegisterCallbacks(_callbacks);

            // TestRunnerApi is a ScriptableObject and its callback registry outlives a
            // domain reload. Registering again on the next load therefore stacks a
            // second subscription, and every test reports twice. Drop ours on the way
            // out. (TestFinished also dedupes, in case this never fires.)
            AssemblyReloadEvents.beforeAssemblyReload -= Uninstall;
            AssemblyReloadEvents.beforeAssemblyReload += Uninstall;
        }

        static void Uninstall()
        {
            if (_api == null || _callbacks == null) return;
            try { _api.UnregisterCallbacks(_callbacks); }
            catch { /* shutting down anyway */ }
        }

        public static object Start(string mode, string testFilter, string category)
        {
            Install();

            string current = SessionState.GetString(SessionKeyRunId, "");
            if (!string.IsNullOrEmpty(current))
            {
                var state = ReadResults(current);
                bool finished = state != null && state["finished"] != null && (bool)state["finished"];
                if (!finished)
                    throw new Exception("a test run is already in progress (runId " + current +
                                        "). Poll 'testresults' until finished.");
            }

            TestMode testMode;
            if (string.Equals(mode, "play", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mode, "playmode", StringComparison.OrdinalIgnoreCase))
                testMode = TestMode.PlayMode;
            else
                testMode = TestMode.EditMode;

            var filter = new Filter { testMode = testMode };
            if (!string.IsNullOrEmpty(testFilter)) filter.testNames = new[] { testFilter };
            if (!string.IsNullOrEmpty(category)) filter.categoryNames = new[] { category };

            string runId = Guid.NewGuid().ToString("N").Substring(0, 12);
            SessionState.SetString(SessionKeyRunId, runId);
            SessionState.SetString(SessionKeyMode, testMode.ToString());

            Directory.CreateDirectory(ResultsDir);
            WriteResults(runId, new JObject
            {
                ["runId"] = runId,
                ["mode"] = testMode.ToString(),
                ["filter"] = testFilter,
                ["category"] = category,
                ["started"] = true,
                ["finished"] = false,
                ["passed"] = 0,
                ["failed"] = 0,
                ["skipped"] = 0,
                ["inconclusive"] = 0,
                ["tests"] = new JArray(),
            });

            _api.Execute(new ExecutionSettings(filter));

            return new Dictionary<string, object>
            {
                { "runId", runId },
                { "mode", testMode.ToString() },
                { "started", true },
                { "resultsFile", Path.Combine(ResultsDir, runId + ".json") },
                { "note", "poll 'testresults' until finished=true" },
            };
        }

        public static object Results(string runId)
        {
            if (string.IsNullOrEmpty(runId))
                runId = SessionState.GetString(SessionKeyRunId, "");

            if (string.IsNullOrEmpty(runId))
                throw new Exception("no test run has been started in this editor session");

            var json = ReadResults(runId);
            if (json == null)
                throw new Exception("no results file for runId '" + runId + "'");

            return json.ToObject<Dictionary<string, object>>();
        }

        // ------------------------------------------------------------ results file

        static string PathFor(string runId)
        {
            return Path.Combine(ResultsDir, runId + ".json");
        }

        static JObject ReadResults(string runId)
        {
            try
            {
                string p = PathFor(runId);
                if (!File.Exists(p)) return null;
                return JObject.Parse(File.ReadAllText(p));
            }
            catch { return null; }
        }

        static void WriteResults(string runId, JObject payload)
        {
            try
            {
                Directory.CreateDirectory(ResultsDir);
                string tmp = PathFor(runId) + ".tmp";
                string fin = PathFor(runId);
                File.WriteAllText(tmp, payload.ToString(Formatting.Indented));
                if (File.Exists(fin)) File.Delete(fin);
                File.Move(tmp, fin);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ClaudeBridge] could not write test results: " + e.Message);
            }
        }

        // ------------------------------------------------------------ callbacks

        class Callbacks : ICallbacks
        {
            public void RunStarted(ITestAdaptor testsToRun) { }

            public void TestStarted(ITestAdaptor test) { }

            public void TestFinished(ITestResultAdaptor result)
            {
                // Suites report aggregate rows; only leaf tests are interesting.
                if (result.Test.IsSuite) return;

                string runId = SessionState.GetString(SessionKeyRunId, "");
                if (string.IsNullOrEmpty(runId)) return;

                var json = ReadResults(runId);
                if (json == null) return;

                var tests = (JArray)json["tests"];

                // Defensive: a duplicate subscription would otherwise record the same
                // test twice. FullName is unique within a run, including for
                // parameterized cases, so this is safe.
                string fullName = result.Test.FullName;
                foreach (var existing in tests)
                    if ((string)existing["name"] == fullName) return;

                tests.Add(new JObject
                {
                    ["name"] = result.Test.FullName,
                    ["status"] = result.TestStatus.ToString(),
                    ["durationSec"] = Math.Round(result.Duration, 3),
                    ["message"] = string.IsNullOrEmpty(result.Message) ? null : result.Message,
                    ["stackTrace"] = string.IsNullOrEmpty(result.StackTrace) ? null : result.StackTrace,
                });

                string key = result.TestStatus == TestStatus.Passed ? "passed"
                           : result.TestStatus == TestStatus.Failed ? "failed"
                           : result.TestStatus == TestStatus.Skipped ? "skipped"
                           : "inconclusive";
                json[key] = (int)json[key] + 1;

                WriteResults(runId, json);
            }

            public void RunFinished(ITestResultAdaptor result)
            {
                string runId = SessionState.GetString(SessionKeyRunId, "");
                if (string.IsNullOrEmpty(runId)) return;

                var json = ReadResults(runId);
                if (json == null) return;

                json["finished"] = true;
                json["durationSec"] = Math.Round(result.Duration, 3);
                // Trust the run-level totals over our incremental tally: a domain
                // reload mid-run can cost us individual TestFinished callbacks.
                json["passed"] = result.PassCount;
                json["failed"] = result.FailCount;
                json["skipped"] = result.SkipCount;
                json["inconclusive"] = result.InconclusiveCount;

                WriteResults(runId, json);

                SessionState.SetString(SessionKeyRunId, "");
            }
        }
    }
}
