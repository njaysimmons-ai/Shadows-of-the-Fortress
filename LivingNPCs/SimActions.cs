using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace LivingNPCs
{
    /// <summary>
    /// Lets AI-driven sims do what they say. The model ends a reply with an action tag such as [JOIN] or
    /// [MEET:Rockshade Hold]; the tag is removed from chat and carried out with the game's own mechanics:
    /// joining your group, travelling to a zone and waiting at the entrance, coming to find you, leaving,
    /// or an emote.
    /// </summary>
    public static class SimActions
    {
        private static readonly Regex Tag = new Regex(@"\[\s*(JOIN|LEAVE|COME|MEET|WAVE|DANCE|SIT|CHEER|BOW|LAUGH)\s*(?::\s*([^\]]*))?\]", RegexOptions.IgnoreCase);
        private static readonly Regex AnyTag = new Regex(@"\[[^\]]*\]");

        private class Meeting
        {
            public string Sim;
            public string Zone;
            public float Until;
            public bool Arrived;
        }

        private static readonly List<Meeting> Meetings = new List<Meeting>();
        private static readonly Dictionary<string, float> PendingJoins = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        private static float nextTick;

        // ---------------------------------------------------------------- parsing

        /// <summary>Splits a reply into the chat text and the action it asks for (or null).</summary>
        public static string Extract(string reply, out string action, out string argument)
        {
            action = null;
            argument = null;
            if (reply == null) return null;
            Match m = Tag.Match(reply);
            if (m.Success)
            {
                action = m.Groups[1].Value.ToUpperInvariant();
                argument = m.Groups[2].Success ? m.Groups[2].Value.Trim() : null;
            }
            return AnyTag.Replace(reply, "").Replace("  ", " ").Trim();
        }

        /// <summary>The zones the AI may name, in their display names, for the prompt.</summary>
        public static string ZoneList()
        {
            var names = new List<string>();
            if (ZoneAtlas.Atlas != null)
            {
                foreach (ZoneAtlasEntry e in ZoneAtlas.Atlas)
                {
                    if (e != null && !string.IsNullOrEmpty(e.ZoneName) && e.ZoneName != "Tutorial") names.Add(GetCommonTerms.GetZoneTerm(e.ZoneName));
                }
            }
            names.Sort();
            return string.Join(", ", names);
        }

        private static string Simplify(string s) => Regex.Replace((s ?? "").ToLowerInvariant(), "[^a-z0-9]", "");

        /// <summary>Finds the scene for a zone name the AI wrote ("Rockshade", "the brake", "Port Azure").</summary>
        public static string FindZone(string name)
        {
            string wanted = Simplify(Regex.Replace(name ?? "", @"^\s*the\s+", "", RegexOptions.IgnoreCase));
            if (wanted.Length < 3 || ZoneAtlas.Atlas == null) return null;
            string best = null;
            foreach (ZoneAtlasEntry e in ZoneAtlas.Atlas)
            {
                if (e == null || string.IsNullOrEmpty(e.ZoneName) || e.ZoneName == "Tutorial") continue;
                string scene = Simplify(e.ZoneName);
                string display = Simplify(GetCommonTerms.GetZoneTerm(e.ZoneName));
                if (wanted == scene || wanted == display) return e.ZoneName;
                if (display.Contains(wanted) || wanted.Contains(display) || scene.Contains(wanted) || wanted.Contains(scene)) best = best ?? e.ZoneName;
            }
            return best;
        }

        // ---------------------------------------------------------------- doing it

        /// <summary>Carries out an action a sim just agreed to. Runs on the game thread.</summary>
        public static void Perform(string simName, SimPlayer sim, string action, string argument)
        {
            if (!Plugin.AiActions.Value || action == null) return;
            SimPlayerTracking tracking = FriendshipEvents.Tracking(simName);
            if (tracking == null) return;
            sim = sim != null ? sim : tracking.MyAvatar;
            Plugin.Log.LogInfo("Sim action: " + simName + " " + action + (argument != null ? " " + argument : ""));
            try
            {
                switch (action)
                {
                    case "JOIN":
                    case "COME":
                        if (sim != null && tracking.CurScene == WorldContext.Zone && SimLife.Alive(sim)) Join(sim);
                        else ComeToPlayer(tracking);
                        break;
                    case "LEAVE":
                        if (sim != null && sim.InGroup)
                        {
                            Character c = sim.GetComponent<Character>();
                            if (c != null) GameData.SimPlayerGrouping.ForceDismissFromGroup(c);
                        }
                        break;
                    case "MEET":
                        Meet(tracking, sim, argument);
                        break;
                    default:
                        if (sim != null && SimLife.Idle(sim))
                        {
                            string line = Lines.Pick("emote." + action.ToLowerInvariant() + ".react", SimLife.PersonaOf(sim), SimLife.MoodOf(sim));
                            if (line != null) SimLife.ActOut(sim, Lines.Fill(line, new Dictionary<string, string> { ["player"] = WorldContext.PlayerName }), SimLife.PlayerPos);
                        }
                        break;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Sim action " + action + " failed: " + e);
            }
        }

        private static void Join(SimPlayer sim)
        {
            if (sim.InGroup) return;
            Character c = sim.GetComponent<Character>();
            if (c == null || GameData.SimPlayerGrouping == null) return;
            GameData.SimPlayerGrouping.InviteToGroup(c);
            if (!sim.InGroup)
            {
                bool full = Array.TrueForAll(GameData.GroupMembers, m => m != null);
                string why = full ? "oh wait, your group's full" : "hm, can't join, we're too far apart in level i think";
                Scheduler.After(1.5f, () => Speech.Say(sim, why));
            }
        }

        /// <summary>A sim elsewhere heads to your zone (the game brings them in at the entrance) and joins on arrival.</summary>
        private static void ComeToPlayer(SimPlayerTracking tracking)
        {
            tracking.seekingPlayer = true;
            tracking.TarScene = WorldContext.Zone;
            tracking.StayInZone = false;
            PendingJoins[tracking.SimName] = Time.time + 30f * 60f;
        }

        private static void Meet(SimPlayerTracking tracking, SimPlayer sim, string zoneName)
        {
            string zone = FindZone(zoneName);
            if (zone == null)
            {
                Plugin.Log.LogInfo(tracking.SimName + " wanted to meet at '" + zoneName + "', which isn't a zone the mod knows.");
                return;
            }
            // A groupmate travels with you anyway; they'll just be there when you arrive.
            if (sim != null && sim.InGroup) return;

            Meetings.RemoveAll(m => string.Equals(m.Sim, tracking.SimName, StringComparison.OrdinalIgnoreCase));
            Meetings.Add(new Meeting { Sim = tracking.SimName, Zone = zone, Until = Time.time + 45f * 60f });
            tracking.StayInZone = true;
            tracking.InvitedPlayerToZone = zone;

            if (tracking.CurScene == zone)
            {
                if (zone == WorldContext.Zone) PendingJoins[tracking.SimName] = Time.time + 30f * 60f;
                return;
            }

            if (sim != null && tracking.CurScene == WorldContext.Zone)
            {
                // Visible: they set off, then leave the zone like the game's own sims do.
                string where = GetCommonTerms.GetZoneTerm(zone);
                Scheduler.After(2f, () =>
                {
                    if (sim == null) return;
                    Speech.Emote(sim.name, "heads off toward " + where + ".");
                    Scheduler.After(4f, () =>
                    {
                        if (sim == null || sim.InGroup) return;
                        sim.SaveSim();
                        GameData.SimMngr.SimChangeScene(tracking, zone, Vector3.zero, false);
                        UnityEngine.Object.Destroy(sim.gameObject);
                    });
                });
            }
            else
            {
                GameData.SimMngr.SimChangeScene(tracking, zone, Vector3.zero, false);
            }
        }

        // ---------------------------------------------------------------- keeping promises over time

        /// <summary>Called by SimLife every half second.</summary>
        public static void Tick()
        {
            if (Time.time < nextTick || GameData.SimMngr == null) return;
            nextTick = Time.time + 2f;
            string here = WorldContext.Zone;

            for (int i = Meetings.Count - 1; i >= 0; i--)
            {
                Meeting m = Meetings[i];
                SimPlayerTracking t = FriendshipEvents.Tracking(m.Sim);
                if (t == null || Time.time > m.Until || t.Grouped)
                {
                    if (t != null) t.StayInZone = false;
                    Meetings.RemoveAt(i);
                    continue;
                }
                // Keep them waiting where they said they'd be, instead of wandering off to another zone.
                if (t.CurScene == m.Zone) t.TimeInZone = 0f;

                if (!m.Arrived && here == m.Zone && t.CurScene == m.Zone)
                {
                    m.Arrived = true;
                    string line = Lines.Fill(Lines.Pick("action.meet.arrived", SimLife.PersonaOf(t.MyAvatar)) ?? "i'm here at the entrance, {player}! come find me",
                        new Dictionary<string, string> { ["player"] = WorldContext.PlayerName, ["zone"] = GetCommonTerms.GetZoneTerm(m.Zone) });
                    if (line != null) Scheduler.After(3f, () => FriendshipEvents.Whisper(t.SimName, t.MyAvatar, line));
                    PendingJoins[t.SimName] = Time.time + 10f * 60f;
                }
            }

            var done = new List<string>();
            foreach (var pair in PendingJoins)
            {
                SimPlayerTracking t = FriendshipEvents.Tracking(pair.Key);
                if (t == null || Time.time > pair.Value || t.Grouped)
                {
                    done.Add(pair.Key);
                    continue;
                }
                if (t.seekingPlayer && t.CurScene != here) t.TarScene = here; // you moved; follow
                SimPlayer avatar = t.MyAvatar;
                if (avatar != null && t.CurScene == here && SimLife.Alive(avatar) && avatar.gameObject.activeInHierarchy)
                {
                    Join(avatar);
                    done.Add(pair.Key);
                }
            }
            foreach (string k in done) PendingJoins.Remove(k);
        }

        public static string Describe()
        {
            var sb = new StringBuilder();
            foreach (Meeting m in Meetings) sb.Append(m.Sim).Append(" is meeting you in ").Append(GetCommonTerms.GetZoneTerm(m.Zone)).Append(". ");
            foreach (var p in PendingJoins) sb.Append(p.Key).Append(" is on the way to join you. ");
            return sb.ToString().Trim();
        }
    }
}
