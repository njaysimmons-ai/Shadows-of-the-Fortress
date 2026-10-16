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
            Jobs[sim] = new Job { Mode = Mode.Guide, Target = where, TargetName = name, Until = Time.time + 6f * 60f };
            return true;
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

        private static bool FindPlace(string place, out Vector3 where, out string name)
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

        private static void MoveTo(SimPlayer sim, Vector3 dest, float speed)
        {
            NavMeshAgent nav = Nav(sim);
            if (nav == null) return;
            nav.speed = speed;
            if (nav.isOnNavMesh) nav.isStopped = false;
            Animator anim = sim.GetComponent<Character>()?.GetMyAnim();
            if (anim != null)
            {
                anim.SetBool("Walking", true);
                anim.SetBool("Patrol", false);
            }
            NPC npc = sim.GetComponent<NPC>();
            Vector3 safe = GameData.GetSafeNavMeshPoint(dest);
            if (npc != null && npc.NeedsNavUpdate(safe)) npc.HighPriorityNavUpdate(safe);
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
                    if (Vector3.Distance(me, job.Target) < 4f)
                    {
                        Halt(sim);
                        Speech.Face(sim, job.Target);
                        Jobs.Remove(sim);
                        Speech.Emote(sim.name, "points out " + job.TargetName + ".");
                        string line = Lines.Fill(Lines.Pick("action.guide.arrived", SimLife.PersonaOf(sim)) ?? "here you go, {place}!",
                            new Dictionary<string, string> { ["place"] = job.TargetName, ["player"] = WorldContext.PlayerName });
                        Scheduler.After(1.2f, () => Speech.Say(sim, line));
                        return true;
                    }
                    if (Vector3.Distance(me, player) > 14f)
                    {
                        // Don't lose the person you're guiding.
                        Halt(sim);
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
                        MoveTo(sim, job.Target, run * 0.85f);
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
