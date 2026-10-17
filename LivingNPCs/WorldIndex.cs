using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

namespace LivingNPCs
{
    /// <summary>
    /// What the mod has learned about the world: which zone each NPC lives in and which zones connect,
    /// recorded as you visit each zone (the game only knows NPCs in the zone you're in). Combined with the
    /// game's own zone neighbour list, this lets sims plan a route to an NPC or zone elsewhere.
    /// Saved to memory/world-index.tsv; shared by all your characters.
    /// </summary>
    public static class WorldIndex
    {
        private static readonly Dictionary<string, string> NpcZone = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> Edges = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static string path;
        private static bool dirty;

        public static void Load(string folder)
        {
            path = Path.Combine(folder, "world-index.tsv");
            if (!File.Exists(path)) return;
            foreach (string line in File.ReadAllLines(path))
            {
                string[] p = line.Split('\t');
                if (p.Length < 3) continue;
                if (p[0] == "N") NpcZone[p[1]] = p[2];
                else if (p[0] == "E") Edges.Add(p[1] + ">" + p[2]);
            }
        }

        public static void Save()
        {
            if (!dirty || path == null) return;
            var lines = new List<string>();
            foreach (var pair in NpcZone) lines.Add("N\t" + pair.Key + "\t" + pair.Value);
            foreach (string e in Edges)
            {
                string[] p = e.Split('>');
                lines.Add("E\t" + p[0] + "\t" + p[1]);
            }
            File.WriteAllLines(path, lines);
            dirty = false;
        }

        /// <summary>Records the NPCs and exits of the zone you're in.</summary>
        public static void Scan()
        {
            string zone = WorldContext.Zone;
            if (string.IsNullOrEmpty(zone) || zone == "LoadScene" || zone == "Menu") return;
            foreach (NPCDialogManager m in UnityEngine.Object.FindObjectsOfType<NPCDialogManager>())
            {
                NPC npc = m.GetComponent<NPC>();
                if (npc == null || npc.SimPlayer || string.IsNullOrEmpty(npc.NPCName)) continue;
                if (!NpcZone.TryGetValue(npc.NPCName, out string known) || known != zone)
                {
                    NpcZone[npc.NPCName] = zone;
                    dirty = true;
                }
            }
            foreach (Zoneline z in UnityEngine.Object.FindObjectsOfType<Zoneline>())
            {
                if (z != null && !string.IsNullOrEmpty(z.DestinationZone) && Edges.Add(zone + ">" + z.DestinationZone)) dirty = true;
            }
            Save();
        }

        private static string Simplify(string s) => Regex.Replace((s ?? "").ToLowerInvariant(), "[^a-z0-9]", "");

        /// <summary>Finds a known NPC by (part of) their name. Returns false if none matches.</summary>
        public static bool FindNpc(string query, out string name, out string zone)
        {
            name = zone = null;
            string wanted = Simplify(Regex.Replace(query ?? "", @"^\s*(the|a|an)\s+", "", RegexOptions.IgnoreCase));
            if (wanted.Length < 3) return false;
            foreach (var pair in NpcZone)
            {
                string n = Simplify(pair.Key);
                if (n == wanted || n.Contains(wanted) || (wanted.Length >= 5 && wanted.Contains(n)))
                {
                    name = pair.Key;
                    zone = pair.Value;
                    return true;
                }
            }
            return false;
        }

        private static IEnumerable<string> Neighbours(string zone)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string e in Edges)
            {
                string[] p = e.Split('>');
                if (string.Equals(p[0], zone, StringComparison.OrdinalIgnoreCase) && seen.Add(p[1])) yield return p[1];
            }
            ZoneAtlasEntry info = ZoneAtlas.FindZoneInfo(zone);
            if (info?.NeighboringZones != null)
            {
                foreach (string n in info.NeighboringZones) if (!string.IsNullOrEmpty(n) && seen.Add(n)) yield return n;
            }
            // The atlas lists neighbours one way; treat connections as two-way.
            if (ZoneAtlas.Atlas != null)
            {
                foreach (ZoneAtlasEntry e in ZoneAtlas.Atlas)
                {
                    if (e?.NeighboringZones != null && e.NeighboringZones.Contains(zone) && seen.Add(e.ZoneName)) yield return e.ZoneName;
                }
            }
        }

        /// <summary>The next zone to head into on the shortest route from one zone to another, or null.</summary>
        public static string NextHop(string from, string to)
        {
            if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return null;
            var previous = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [from] = null };
            var queue = new Queue<string>();
            queue.Enqueue(from);
            while (queue.Count > 0)
            {
                string zone = queue.Dequeue();
                foreach (string next in Neighbours(zone))
                {
                    if (previous.ContainsKey(next)) continue;
                    previous[next] = zone;
                    if (string.Equals(next, to, StringComparison.OrdinalIgnoreCase))
                    {
                        string step = next;
                        while (!string.Equals(previous[step], from, StringComparison.OrdinalIgnoreCase)) step = previous[step];
                        return step;
                    }
                    queue.Enqueue(next);
                }
            }
            return null;
        }
    }
}
