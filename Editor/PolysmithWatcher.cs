using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Polysmith
{
    /// <summary>
    /// Follows a running Polysmith round trip: each time Polysmith writes the "_lod.obj" files, they are applied to the
    /// prefab (<see cref="PolysmithRoundTrip.ApplyResults"/>); when Polysmith closes, the round trip ends and its temp
    /// folder is deleted. The round trip lives in SessionState, so it survives a script reload. One at a time.
    /// </summary>
    [InitializeOnLoad]
    static class PolysmithWatcher
    {
        static PolysmithWatcher()
        {
            if (IsRunning)
                EditorApplication.update += Poll;
        }

        public static bool IsRunning => SessionState.GetString(PrefabKey, "").Length > 0;

        public static void Start(string prefabPath, string folder, Process polysmith)
        {
            SessionState.SetString(PrefabKey, prefabPath);
            SessionState.SetString(FolderKey, folder);
            SessionState.SetInt(ProcessKey, polysmith.Id);
            SessionState.SetString(StartedKey, Ticks(polysmith.StartTime));
            SessionState.SetString(AppliedKey, "");
            EditorApplication.update += Poll;
        }

        static void Poll()
        {
            double now = EditorApplication.timeSinceStartup;
            // Assets are not changed during Play or while Unity compiles or imports.
            if (now < _nextPoll || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling ||
                EditorApplication.isUpdating)
                return;
            _nextPoll = now + PollSeconds;

            bool running = PolysmithRunning();
            string folder = SessionState.GetString(FolderKey, "");
            string stamp = Stamp(folder);
            if (stamp != _seenStamp)
            {
                _seenStamp = stamp;
                _seenSince = now;
            }

            // An export writes its files one after another: apply once they have stopped changing.
            bool settled = running == false || now - _seenSince >= SettleSeconds;
            if (settled && stamp != SessionState.GetString(AppliedKey, ""))
            {
                // Recorded first, so an apply that throws is not retried until Polysmith writes again.
                SessionState.SetString(AppliedKey, stamp);
                Apply(folder);
            }

            if (running == false)
                Finish(folder);
        }

        static void Apply(string folder)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(SessionState.GetString(PrefabKey, ""));
            var warnings = new List<string>();
            var reduced = PolysmithRoundTrip.ApplyResults(prefab, folder, warnings);
            foreach (string warning in warnings)
                Debug.LogWarning("Polysmith: " + warning);
            Debug.Log($"Polysmith: '{prefab.name}' updated, {reduced.Count} reduced mesh(es).");
        }

        static void Finish(string folder)
        {
            EditorApplication.update -= Poll;
            string prefabPath = SessionState.GetString(PrefabKey, "");
            foreach (string key in new[] { PrefabKey, FolderKey, StartedKey, AppliedKey })
                SessionState.EraseString(key);
            SessionState.EraseInt(ProcessKey);
            _seenStamp = null;
            Directory.Delete(folder, true);
            Debug.Log($"Polysmith closed: the round trip for '{prefabPath}' is finished.");
        }

        // The start time tells the round trip's Polysmith apart from a later process that got the same id.
        static bool PolysmithRunning()
        {
            try
            {
                using (var process = Process.GetProcessById(SessionState.GetInt(ProcessKey, 0)))
                    return process.HasExited == false && Ticks(process.StartTime) == SessionState.GetString(StartedKey, "");
            }
            // No process has that id any more (GetProcessById), or it ended while being read (StartTime).
            catch (Exception gone) when (gone is ArgumentException || gone is InvalidOperationException)
            {
                return false;
            }
        }

        // Every result file with its last write time: it changes whenever Polysmith writes one.
        static string Stamp(string folder)
        {
            var files = new List<string>();
            foreach (string file in Directory.GetFiles(folder))
                if (file.EndsWith("_lod.obj", StringComparison.OrdinalIgnoreCase))
                    files.Add(Path.GetFileName(file) + "@" + Ticks(File.GetLastWriteTimeUtc(file)));
            files.Sort(StringComparer.Ordinal);
            var stamp = new StringBuilder();
            foreach (string file in files)
                stamp.Append(file).Append('|');
            return stamp.ToString();
        }

        static string Ticks(DateTime time) => time.Ticks.ToString(CultureInfo.InvariantCulture);

        const double PollSeconds = 0.5;
        const double SettleSeconds = 1;
        const string PrefabKey = "Polysmith.RoundTrip.Prefab";
        const string FolderKey = "Polysmith.RoundTrip.Folder";
        const string ProcessKey = "Polysmith.RoundTrip.Process";
        const string StartedKey = "Polysmith.RoundTrip.Started";
        const string AppliedKey = "Polysmith.RoundTrip.Applied";

        // Editor timing only; rebuilt from SessionState after a script reload.
        static double _nextPoll;
        static string _seenStamp;
        static double _seenSince;
    }
}
