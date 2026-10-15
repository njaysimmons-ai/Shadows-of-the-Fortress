using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace LivingNPCs
{
    /// <summary>What a sim remembers about adventuring with the current character.</summary>
    public class SharedHistory
    {
        public int Fights, SimDeaths, Wipes, PlayerDeaths, Levels;
        public string LastWipeZone = "", LastZone = "";
    }

    /// <summary>
    /// Per-character, saved between sessions: each sim's history with you, and how every pair of sims
    /// feels about each other. Moods are kept too, but only for the session.
    /// </summary>
    public class SimBonds
    {
        private readonly string folder;
        private readonly Dictionary<string, SharedHistory> history = new Dictionary<string, SharedHistory>();
        private readonly Dictionary<string, float> affinity = new Dictionary<string, float>();
        private readonly Dictionary<string, float> mood = new Dictionary<string, float>();
        private string loadedFor;
        private bool dirty;

        public SimBonds(string folder)
        {
            this.folder = folder;
            Directory.CreateDirectory(folder);
        }

        public void UseCharacter(string characterName)
        {
            if (string.IsNullOrEmpty(characterName) || characterName == loadedFor) return;
            Save();
            history.Clear();
            affinity.Clear();
            mood.Clear();
            loadedFor = characterName;
            string path = PathFor(characterName);
            if (!File.Exists(path)) return;
            foreach (string line in File.ReadAllLines(path))
            {
                string[] p = line.Split('\t');
                if (p.Length >= 9 && p[0] == "S")
                {
                    history[p[1]] = new SharedHistory
                    {
                        Fights = Int(p[2]), SimDeaths = Int(p[3]), Wipes = Int(p[4]), PlayerDeaths = Int(p[5]),
                        Levels = Int(p[6]), LastWipeZone = p[7], LastZone = p[8],
                    };
                }
                else if (p.Length >= 3 && p[0] == "R" &&
                         float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float a))
                {
                    affinity[p[1]] = a;
                }
            }
        }

        private static int Int(string s) => int.TryParse(s, out int v) ? v : 0;

        public SharedHistory History(string sim)
        {
            if (!history.TryGetValue(sim, out SharedHistory h))
            {
                h = new SharedHistory();
                history[sim] = h;
            }
            dirty = true;
            return h;
        }

        public bool Knows(string sim) => history.ContainsKey(sim);

        // ---------------------------------------------------------------- relationships

        private static string PairKey(string a, string b) =>
            string.CompareOrdinal(a, b) < 0 ? a + "|" + b : b + "|" + a;

        public float Affinity(string a, string b, string personaA, string personaB)
        {
            string key = PairKey(a, b);
            if (!affinity.TryGetValue(key, out float value))
            {
                // Strangers start somewhere between mild dislike and mild liking, nudged by temperament.
                value = (StableHash(key) % 1000) / 1000f * 0.6f - 0.3f;
                value += Chemistry(personaA, personaB);
                affinity[key] = value;
                dirty = true;
            }
            return value;
        }

        public void Nudge(string a, string b, float delta)
        {
            string key = PairKey(a, b);
            float value = affinity.TryGetValue(key, out float v) ? v : 0f;
            affinity[key] = Math.Max(-1f, Math.Min(1f, value + delta));
            dirty = true;
        }

        private static float Chemistry(string a, string b)
        {
            if (a == null || b == null) return 0f;
            float c = 0f;
            if (a == "wholesome" || b == "wholesome") c += 0.15f;
            if (a == "grump" || b == "grump") c -= 0.1f;
            if (a == b) c += 0.15f;
            if ((a == "tryhard" && b == "newbie") || (a == "newbie" && b == "tryhard")) c -= 0.25f;
            if ((a == "grump" && b == "hype") || (a == "hype" && b == "grump")) c -= 0.2f;
            if ((a == "oldschool" && b == "newbie") || (a == "newbie" && b == "oldschool")) c += 0.1f;
            return c;
        }

        public static string Bond(float affinity) =>
            affinity > 0.25f ? "friend" : affinity < -0.2f ? "rival" : "neutral";

        // ---------------------------------------------------------------- mood

        public void ShiftMood(string sim, float delta)
        {
            mood[sim] = Math.Max(-1f, Math.Min(1f, MoodValue(sim) + delta));
        }

        public float MoodValue(string sim) => mood.TryGetValue(sim, out float m) ? m : 0f;

        public string MoodOf(string sim)
        {
            float m = MoodValue(sim);
            return m > 0.35f ? "high" : m < -0.3f ? "low" : "neutral";
        }

        /// <summary>Moods drift back toward neutral over several minutes.</summary>
        public void DecayMoods(float seconds)
        {
            float step = 0.12f * seconds / 60f;
            var keys = new List<string>(mood.Keys);
            foreach (string k in keys)
            {
                float m = mood[k];
                mood[k] = Math.Abs(m) <= step ? 0f : m - Math.Sign(m) * step;
            }
        }

        // ---------------------------------------------------------------- persistence

        public void Save()
        {
            if (!dirty || loadedFor == null) return;
            var lines = new List<string>();
            foreach (var pair in history)
            {
                SharedHistory h = pair.Value;
                lines.Add(string.Join("\t", "S", pair.Key, h.Fights.ToString(), h.SimDeaths.ToString(), h.Wipes.ToString(),
                    h.PlayerDeaths.ToString(), h.Levels.ToString(), Clean(h.LastWipeZone), Clean(h.LastZone)));
            }
            foreach (var pair in affinity)
            {
                lines.Add("R\t" + pair.Key + "\t" + pair.Value.ToString("0.###", CultureInfo.InvariantCulture));
            }
            File.WriteAllLines(PathFor(loadedFor), lines);
            dirty = false;
        }

        private static string Clean(string s) => (s ?? "").Replace('\t', ' ');

        private string PathFor(string characterName)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                characterName = characterName.Replace(c, '_');
            }
            return Path.Combine(folder, characterName + ".sims.tsv");
        }

        internal static int StableHash(string text)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (char c in text) hash = (hash ^ c) * 16777619;
                return (int)(hash & 0x7fffffff);
            }
        }
    }
}
