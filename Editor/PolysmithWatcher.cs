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
    /// Follows the running Polysmith round trips, one Polysmith per prefab: each time a Polysmith writes its "_lod.obj"
    /// files, they are applied to its prefab (<see cref="PolysmithRoundTrip.ApplyResults"/>); when it closes, its round
    /// trip ends and its temp folder is deleted. The round trips live in SessionState, so they survive a script reload.
    /// </summary>
    [InitializeOnLoad]
    static class PolysmithWatcher
    {
        static PolysmithWatcher()
        {
            if (Load().Trips.Count > 0)
                EditorApplication.update += Poll;
        }

        /// <summary>Whether a round trip for the prefab at <paramref name="prefabPath"/> is open.</summary>
        public static bool IsOpen(string prefabPath)
        {
            foreach (var trip in Load().Trips)
                if (trip.Prefab == prefabPath)
                    return true;
            return false;
        }

        public static void Start(string prefabPath, string folder, Process polysmith)
        {
            var trips = Load();
            if (trips.Trips.Count == 0)
                EditorApplication.update += Poll;
            trips.Trips.Add(new RoundTrip
            {
                Prefab = prefabPath,
                Folder = folder,
                Process = polysmith.Id,
                Started = Ticks(polysmith.StartTime)
            });
            Save(trips);
        }

        static void Poll()
        {
            double now = EditorApplication.timeSinceStartup;
            // Assets are not changed during Play or while Unity compiles or imports.
            if (now < _nextPoll || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling ||
                EditorApplication.isUpdating)
                return;
            _nextPoll = now + PollSeconds;

            var trips = Load();
            for (int index = trips.Trips.Count - 1; index >= 0; index--)
            {
                var trip = trips.Trips[index];
                bool running = PolysmithRunning(trip);
                string stamp = Stamp(trip.Folder);
                if (_seen.TryGetValue(trip.Folder, out var seen) == false || seen.Stamp != stamp)
                    _seen[trip.Folder] = seen = (stamp, now);

                // An export writes its files one after another: apply once they have stopped changing.
                bool settled = running == false || now - seen.Since >= SettleSeconds;
                if (settled && stamp != trip.Applied)
                {
                    // Recorded first, so an apply that throws is not retried until Polysmith writes again.
                    trip.Applied = stamp;
                    Save(trips);
                    Apply(trip);
                }

                if (running == false)
                {
                    trips.Trips.RemoveAt(index);
                    Save(trips);
                    Finish(trip);
                }
            }

            if (trips.Trips.Count == 0)
                EditorApplication.update -= Poll;
        }

        static void Apply(RoundTrip trip)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(trip.Prefab);
            var warnings = new List<string>();
            var reduced = PolysmithRoundTrip.ApplyResults(prefab, trip.Folder, warnings);
            foreach (string warning in warnings)
                Debug.LogWarning("Polysmith: " + warning);
            Debug.Log($"Polysmith: '{prefab.name}' updated, {reduced.Count} reduced mesh(es).");
        }

        static void Finish(RoundTrip trip)
        {
            _seen.Remove(trip.Folder);
            Directory.Delete(trip.Folder, true);
            Debug.Log($"Polysmith closed: the round trip for '{trip.Prefab}' is finished.");
        }

        // The start time tells the round trip's Polysmith apart from a later process that got the same id.
        static bool PolysmithRunning(RoundTrip trip)
        {
            try
            {
                using (var process = Process.GetProcessById(trip.Process))
                    return process.HasExited == false && Ticks(process.StartTime) == trip.Started;
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

        static RoundTrips Load()
        {
            string json = SessionState.GetString(TripsKey, "");
            return json.Length > 0 ? JsonUtility.FromJson<RoundTrips>(json) : new RoundTrips();
        }

        static void Save(RoundTrips trips)
        {
            if (trips.Trips.Count == 0)
                SessionState.EraseString(TripsKey);
            else
                SessionState.SetString(TripsKey, JsonUtility.ToJson(trips));
        }

        /// <summary>One open round trip: its prefab, temp folder, Polysmith process, and the last results applied.</summary>
        [Serializable]
        sealed class RoundTrip
        {
            public string Prefab = "";
            public string Folder = "";
            public int Process;
            public string Started = "";
            public string Applied = "";
        }

        [Serializable]
        sealed class RoundTrips
        {
            public List<RoundTrip> Trips = new List<RoundTrip>();
        }

        const double PollSeconds = 0.5;
        const double SettleSeconds = 1;
        const string TripsKey = "Polysmith.RoundTrips";

        // Editor timing only; rebuilt from SessionState after a script reload.
        static double _nextPoll;
        // Per round trip folder: the last result stamp seen and since when.
        static readonly Dictionary<string, (string Stamp, double Since)> _seen =
            new Dictionary<string, (string Stamp, double Since)>();
    }
}
