using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Blue.ClaudeBridge
{
    /// <summary>
    /// Ring buffer of editor log messages.
    ///
    /// Unity exposes no public API for reading the Console window's existing entries
    /// (the usual workaround reflects into the internal UnityEditor.LogEntries, which
    /// breaks across versions). Capturing from Application.logMessageReceived instead
    /// is stable, at the cost of only seeing messages logged after this domain loaded.
    ///
    /// The buffer is static state, so a domain reload clears it. That is usually what
    /// you want: after a recompile the interesting messages are the new ones.
    /// </summary>
    public static class ConsoleCapture
    {
        public const int Capacity = 1000;

        static readonly Queue<Entry> Buffer = new Queue<Entry>(Capacity);
        static bool _installed;
        static int _seq;

        struct Entry
        {
            public int Seq;
            public string Type;
            public string Message;
            public string Stack;
            public double Time;
        }

        public static void Install()
        {
            if (_installed) return;
            _installed = true;

            Application.logMessageReceived -= OnLog;
            Application.logMessageReceived += OnLog;
        }

        static void OnLog(string message, string stack, LogType type)
        {
            var e = new Entry
            {
                Seq = ++_seq,
                Type = type.ToString(),
                Message = message,
                Stack = stack,
                Time = EditorApplication.timeSinceStartup,
            };

            Buffer.Enqueue(e);
            while (Buffer.Count > Capacity) Buffer.Dequeue();
        }

        /// <param name="count">Max entries to return, newest last.</param>
        /// <param name="typeFilter">null/empty for all, otherwise Error|Assert|Warning|Log|Exception.</param>
        /// <param name="sinceSeq">Only entries with Seq greater than this.</param>
        public static List<Dictionary<string, object>> Recent(int count, string typeFilter, int sinceSeq)
        {
            var all = Buffer.ToArray();
            var picked = new List<Dictionary<string, object>>();

            for (int i = 0; i < all.Length; i++)
            {
                var e = all[i];
                if (e.Seq <= sinceSeq) continue;
                if (!string.IsNullOrEmpty(typeFilter) &&
                    !string.Equals(e.Type, typeFilter, StringComparison.OrdinalIgnoreCase)) continue;

                picked.Add(new Dictionary<string, object>
                {
                    { "seq", e.Seq },
                    { "type", e.Type },
                    { "message", e.Message },
                    { "stack", e.Stack },
                    { "time", Math.Round(e.Time, 2) },
                });
            }

            if (count > 0 && picked.Count > count)
                picked = picked.GetRange(picked.Count - count, count);

            return picked;
        }

        public static int LatestSeq { get { return _seq; } }

        public static int CountOf(LogType type)
        {
            int n = 0;
            var all = Buffer.ToArray();
            string want = type.ToString();
            for (int i = 0; i < all.Length; i++)
                if (all[i].Type == want) n++;
            return n;
        }
    }
}
