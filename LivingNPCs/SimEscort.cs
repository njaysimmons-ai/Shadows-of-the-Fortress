using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;

namespace LivingNPCs
{
    /// <summary>
    /// Moves a sim on the player's behalf: guiding you to an NPC or spot, following you, or waiting where
    /// they are. While a sim is being steered, its normal out-of-combat routine (solo or grouped) is
    /// skipped; the moment something attacks it, the game's own combat logic takes over again.
    /// </summary>
    public static class SimEscort
    {
        public enum Mode { Guide, Follow, Wait }

        private class Job
        {
            public Mode Mode;
            public Vector3 Target;
            public string TargetName;
            public float Until;
            public bool ToldToCatchUp;
            public Action<SimPlayer> OnArrive;   // replaces the usual "points out X" ending

            // Stuck detection: if they barely move for a while, try another route, then give up gracefully.
            public Vector3 LastProgressAt;
            public float LastProgressTime;
            public int Repaths;
            public float NextRepath;
        }

        private static readonly Dictionary<SimPlayer, Job> Jobs = new Dictionary<SimPlayer, Job>();
        private static readonly AccessTools.FieldRef<SimPlayer, NavMeshAgent> Nav = AccessTools.FieldRefAccess<SimPlayer, NavMeshAgent>("MyNav");

        public static bool IsBusy(SimPlayer sim) => sim != null && Jobs.ContainsKey(sim);

        public static void Stop(SimPlayer sim)
        {
            if (sim == null || !Jobs.Remove(sim)) return;
            Halt(sim);
        }

        public static void Follow(SimPlayer sim) =>
            Jobs[sim] = new Job { Mode = Mode.Follow, Until = Time.time + 15f * 60f };

        public static void Wait(SimPlayer sim)
        {
            Jobs[sim] = new Job { Mode = Mode.Wait, Until = Time.time + 15f * 60f };
            Halt(sim);
        }

        /// <summary>Starts guiding you to a place in this zone. Returns false if the place wasn't found.</summary>
        public static bool Guide(SimPlayer sim, string place)
        {
            if (!FindPlace(place, out Vector3 where, out string name)) return false;
            StartGuide(sim, where, name, null);
            return true;
        }

        /// <summary>Guides you to the exit that leads into the given zone. Returns false if there's no such exit here.</summary>
        public static bool GuideToExit(SimPlayer sim, string destinationZone, Action<SimPlayer> onArrive)
        {
            Zoneline best = null;
            float bestDistance = float.MaxValue;
            Vector3 from = sim.transform.position;
            foreach (Zoneline z in UnityEngine.Object.FindObjectsOfType<Zoneline>())
            {
                if (z == null || !string.Equals(z.DestinationZone, destinationZone, StringComparison.OrdinalIgnoreCase)) continue;
                float d = Vector3.Distance(from, z.transform.position);
                if (d < bestDistance) { bestDistance = d; best = z; }
            }
            if (best == null) return false;
            StartGuide(sim, best.transform.position, "the way to " + GetCommonTerms.GetZoneTerm(destinationZone), onArrive);
            return true;
        }

        private static void StartGuide(SimPlayer sim, Vector3 where, string name, Action<SimPlayer> onArrive)
        {
            if (Plugin.GuideStyle.Value == "WalkAndMist" || Plugin.GuideStyle.Value == "Mist") MistTrail.BeginAt(where, name, quiet: true);
            Jobs[sim] = new Job
            {
                Mode = Mode.Guide, Target = where, TargetName = name, OnArrive = onArrive, Until = Time.time + 8f * 60f,
                LastProgressAt = sim.transform.position, LastProgressTime = Time.time,
            };
        }

        // ---------------------------------------------------------------- places in this zone

        /// <summary>Named NPCs (with roles), rest/camp spots and exits in the current zone, for the AI prompt.</summary>
        public static string PlacesHere()
        {
            var names = new List<string>();
            foreach (NPCDialogManager m in UnityEngine.Object.FindObjectsOfType<NPCDialogManager>())
            {
                NPC npc = m.GetComponent<NPC>();
                if (npc == null || npc.SimPlayer || string.IsNullOrEmpty(npc.NPCName)) continue;
                NpcRole role = WorldContext.RoleOf(npc);
                string label = role == NpcRole.Townsfolk ? npc.NPCName : npc.NPCName + " (" + role.ToString().ToLowerInvariant() + ")";
                if (!names.Contains(label)) names.Add(label);
                if (names.Count >= 30) break;
            }
            bool rest = false, camp = false;
            foreach (PointOfInterest p in POI.POIs)
            {
                if (p == null) continue;
                if (p.Use == PointOfInterest.POIType.rest) rest = true;
                if (p.Use == PointOfInterest.POIType.camp) camp = true;
            }
            if (rest) names.Add("a rest spot");
            if (camp) names.Add("a hunting camp");
            foreach (Zoneline z in UnityEngine.Object.FindObjectsOfType<Zoneline>())
            {
                if (z != null && !string.IsNullOrEmpty(z.DestinationZone))
                {
                    string exit = "the exit to " + GetCommonTerms.GetZoneTerm(z.DestinationZone);
                    if (!names.Contains(exit)) names.Add(exit);
                }
            }
            return names.Count == 0 ? "nothing in particular" : string.Join(", ", names);
        }

        private static string Simplify(string s) => Regex.Replace((s ?? "").ToLowerInvariant(), "[^a-z0-9 ]", "").Trim();

        internal static bool FindPlace(string place, out Vector3 where, out string name)
        {
            bool ok = FindPlaceCore(place, out Vector3 w, out string n);
            where = w;
            name = n;
            return ok;
        }

        private static Vector3 bestWhere;
        private static string bestName;

        private static bool FindPlaceCore(string place, out Vector3 where, out string name)
        {
            where = Vector3.zero;
            name = place;
            bestWhere = Vector3.zero;
            bestName = place;
            string wanted = Simplify(Regex.Replace(place ?? "", @"^\s*(the|a|an)\s+", "", RegexOptions.IgnoreCase));
            if (wanted.Length < 3) return false;
            Vector3 me = GameData.PlayerControl.transform.position;
            float best = float.MaxValue;
            bool found = false;

            void Consider(Vector3 pos, string label)
            {
                float d = Vector3.Distance(pos, me);
                if (d < best) { best = d; bestWhere = pos; bestName = label; found = true; }
            }

            // A named NPC, or the nearest NPC with that role ("vendor", "banker", "guard").
            foreach (NPCDialogManager m in UnityEngine.Object.FindObjectsOfType<NPCDialogManager>())
            {
                NPC npc = m.GetComponent<NPC>();
                if (npc == null || npc.SimPlayer || string.IsNullOrEmpty(npc.NPCName)) continue;
                string n = Simplify(npc.NPCName);
                if (n == wanted || n.Contains(wanted) || (wanted.Length >= 5 && wanted.Contains(n)))
                {
                    where = npc.transform.position;
                    name = npc.NPCName;
                    return true;
                }
                string role = WorldContext.RoleOf(npc).ToString().ToLowerInvariant();
                if (wanted.Contains(role) || (role == "vendor" && Regex.IsMatch(wanted, @"\b(shop|store|merchant|seller|trader)\b")) ||
                    (role == "banker" && wanted.Contains("bank")))
                {
                    Consider(npc.transform.position, npc.NPCName);
                }
            }
            if (found) { where = bestWhere; name = bestName; return true; }

            // A kind of spot: rest, camp, dance.
            foreach (PointOfInterest p in POI.POIs)
            {
                if (p == null) continue;
                bool match = (p.Use == PointOfInterest.POIType.rest && Regex.IsMatch(wanted, @"\b(rest|sit|med|safe)\b")) ||
                             (p.Use == PointOfInterest.POIType.camp && Regex.IsMatch(wanted, @"\b(camp|hunt|grind|xp)\b")) ||
                             (p.Use == PointOfInterest.POIType.dance && wanted.Contains("dance")) ||
                             (!string.IsNullOrEmpty(p.AreaName) && Simplify(p.AreaName).Contains(wanted));
                if (match) Consider(p.transform.position, string.IsNullOrEmpty(p.AreaName) ? place : p.AreaName);
            }
            if (found) { where = bestWhere; name = bestName; return true; }

            // An exit: "the exit to the brake", "way to port azure".
            foreach (Zoneline z in UnityEngine.Object.FindObjectsOfType<Zoneline>())
            {
                if (z == null || string.IsNullOrEmpty(z.DestinationZone)) continue;
                string dest = Simplify(GetCommonTerms.GetZoneTerm(z.DestinationZone));
                string scene = Simplify(z.DestinationZone);
                if (wanted.Contains(dest) || dest.Contains(wanted) || wanted.Contains(scene) || (wanted.Contains("exit") && found == false))
                {
                    Consider(z.transform.position, "the way to " + GetCommonTerms.GetZoneTerm(z.DestinationZone));
                }
            }
            where = bestWhere;
            name = bestName;
            return found;
        }

        // ---------------------------------------------------------------- movement

        /// <summary>
        /// The closest walkable point to a target. NPCs often stand behind counters or on platforms, so search
        /// outward instead of giving up (the game's own helper only looks 2 metres away).
        /// </summary>
        internal static Vector3 Reachable(Vector3 target)
        {
            foreach (float radius in new[] { 2f, 4f, 8f, 15f })
            {
                if (NavMesh.SamplePosition(target, out NavMeshHit hit, radius, NavMesh.AllAreas)) return hit.position;
            }
            return target;
        }

        private static void MoveTo(SimPlayer sim, Vector3 dest, float speed, bool force = false)
        {
            NavMeshAgent nav = Nav(sim);
            if (nav == null || !nav.isOnNavMesh) return;
            nav.speed = speed;
            nav.isStopped = false;
            Animator anim = sim.GetComponent<Character>()?.GetMyAnim();
            if (anim != null)
            {
                anim.SetBool("Walking", true);
                anim.SetBool("Patrol", false);
            }
            Vector3 point = Reachable(dest);
            if (force || Vector3.Distance(nav.destination, point) > 0.5f) nav.SetDestination(point);
        }

        /// <summary>True once the sim is as close as walking can get it.</summary>
        private static bool Arrived(SimPlayer sim, Job job)
        {
            if (Vector3.Distance(sim.transform.position, job.Target) < 5f) return true;
            NavMeshAgent nav = Nav(sim);
            return nav != null && nav.isOnNavMesh && !nav.pathPending && nav.hasPath &&
                   Vector3.Distance(nav.destination, Reachable(job.Target)) < 1f &&
                   nav.remainingDistance <= nav.stoppingDistance + 1.5f;
        }

        private static void Halt(SimPlayer sim)
        {
            NavMeshAgent nav = Nav(sim);
            if (nav != null)
            {
                nav.speed = 0f;
                nav.velocity = Vector3.zero;
                if (nav.isOnNavMesh) nav.isStopped = true;
            }
            Animator anim = sim.GetComponent<Character>()?.GetMyAnim();
            if (anim != null)
            {
                anim.SetBool("Walking", false);
                anim.SetBool("Patrol", false);
            }
        }

        /// <summary>One frame of steering. Returns false to let the game's own logic run instead.</summary>
        private static bool Step(SimPlayer sim)
        {
            if (!Jobs.TryGetValue(sim, out Job job)) return false;
            NPC npc = sim.GetComponent<NPC>();
            if (!SimLife.Alive(sim) || Time.time > job.Until)
            {
                Jobs.Remove(sim);
                return false;
            }
            if (npc != null && npc.CurrentAggroTarget != null) return false; // fight first, then carry on

            Vector3 me = sim.transform.position;
            Vector3 player = GameData.PlayerControl.transform.position;
            float run = sim.MyStats != null && sim.MyStats.actualRunSpeed > 0 ? sim.MyStats.actualRunSpeed : 5f;

            switch (job.Mode)
            {
                case Mode.Follow:
                    if (Vector3.Distance(me, player) > 5f) MoveTo(sim, player, run);
                    else Halt(sim);
                    break;

                case Mode.Wait:
                    Halt(sim);
                    break;

                case Mode.Guide:
                    if (Arrived(sim, job) || job.Repaths >= 3)
                    {
                        Halt(sim);
                        Speech.Face(sim, job.Target);
                        Jobs.Remove(sim);
                        if (job.OnArrive != null)
                        {
                            job.OnArrive(sim);
                            return true;
                        }
                        Speech.Emote(sim.name, "points out " + job.TargetName + ".");
                        string section = job.Repaths >= 3 ? "action.guide.close" : "action.guide.arrived";
                        string fallback = job.Repaths >= 3 ? "it's right over there, {place}" : "here you go, {place}!";
                        string line = Lines.Fill(Lines.Pick(section, SimLife.PersonaOf(sim)) ?? fallback,
                            new Dictionary<string, string> { ["place"] = job.TargetName, ["player"] = WorldContext.PlayerName });
                        Scheduler.After(1.2f, () => Speech.Say(sim, line));
                        return true;
                    }
                    // Stuck? Barely moved in 6 seconds while not waiting for you: try a fresh route.
                    if (Vector3.Distance(me, job.LastProgressAt) > 1.5f)
                    {
                        job.LastProgressAt = me;
                        job.LastProgressTime = Time.time;
                    }
                    else if (!job.ToldToCatchUp && Time.time - job.LastProgressTime > 6f && Time.time > job.NextRepath)
                    {
                        job.Repaths++;
                        job.NextRepath = Time.time + 3f;
                        job.LastProgressTime = Time.time;
                        Vector3 nudge = job.Target + new Vector3(UnityEngine.Random.Range(-3f, 3f), 0f, UnityEngine.Random.Range(-3f, 3f));
                        MoveTo(sim, nudge, run * Plugin.GuidePaceFactor, force: true);
                        return true;
                    }
                    float waitAt = Plugin.GuideWaitDistance.Value;
                    if (waitAt > 0f && Vector3.Distance(me, player) > waitAt)
                    {
                        // Don't lose the person you're guiding.
                        Halt(sim);
                        job.LastProgressTime = Time.time;
                        job.LastProgressAt = me;
                        Speech.Face(sim, player);
                        if (!job.ToldToCatchUp)
                        {
                            job.ToldToCatchUp = true;
                            Speech.Emote(sim.name, "waits for you to catch up.");
                        }
                    }
                    else
                    {
                        job.ToldToCatchUp = false;
                        MoveTo(sim, job.Target, run * Plugin.GuidePaceFactor);
                    }
                    break;
            }
            return true;
        }

        public static void ForgetDestroyed()
        {
            var dead = new List<SimPlayer>();
            foreach (SimPlayer s in Jobs.Keys) if (s == null) dead.Add(s);
            foreach (SimPlayer s in dead) Jobs.Remove(s);
        }

        [HarmonyPatch(typeof(SimPlayer), "SoloPlayLogic")]
        private static class SoloPatch
        {
            private static bool Prefix(SimPlayer __instance)
            {
                try { return !Step(__instance); }
                catch (Exception e) { Plugin.Log.LogError("Sim escort failed: " + e); Jobs.Remove(__instance); return true; }
            }
        }

        [HarmonyPatch(typeof(SimPlayer), "GroupedWithPlayerLogic")]
        private static class GroupedPatch
        {
            private static bool Prefix(SimPlayer __instance)
            {
                try { return !Step(__instance); }
                catch (Exception e) { Plugin.Log.LogError("Sim escort failed: " + e); Jobs.Remove(__instance); return true; }
            }
        }
    }
}
