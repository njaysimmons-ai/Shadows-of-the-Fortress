using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace LivingNPCs
{
    /// <summary>What happens when friendships grow, and the /friends and /memories commands.</summary>
    public static class FriendshipEvents
    {
        private static string greetedFor;

        internal static bool On => Plugin.EnableFriendships.Value;

        internal static SimPlayerTracking Tracking(string simName) =>
            GameData.SimMngr?.Sims?.Find(s => string.Equals(s.SimName, simName, StringComparison.OrdinalIgnoreCase));

        public static void OnTierReached(string key, int tier)
        {
            string name = Friendships.NameOf(key);
            string tierName = Friendships.TierNames[tier];
            if (Plugin.FriendshipAnnounce.Value)
            {
                UpdateSocialLog.LogAdd("You and " + name + " are now " + (tier == Friendships.TierNames.Length - 1 ? "Best Friends" : tierName + "s") + ".", "#7FD1AE");
            }
            if (!key.StartsWith("sim:")) return;

            SimPlayerTracking tracking = Tracking(name);
            if (tracking != null && Plugin.FriendshipAffectsGame.Value) tracking.OpinionOfPlayer += 2f;

            SimPlayer sim = tracking?.MyAvatar;
            string line = Lines.Pick("friend.tierup." + Friendships.TierIds[tier], SimLife.PersonaOf(sim));
            line = Lines.Fill(line, new Dictionary<string, string> { ["player"] = WorldContext.PlayerName });
            if (line == null) return;
            Scheduler.After(UnityEngine.Random.Range(2f, 4f), () =>
            {
                if (sim != null && SimLife.Alive(sim) && Vector3.Distance(sim.transform.position, SimLife.PlayerPos) < 25f)
                {
                    if (sim.InGroup) Speech.Party(sim, line);
                    else Speech.Say(sim, line);
                }
                else
                {
                    Whisper(name, sim, line);
                }
            });
        }

        internal static void Whisper(string name, SimPlayer sim, string text)
        {
            string typed = sim != null && GameData.SimMngr != null ? GameData.SimMngr.PersonalizeString(text, sim) : text;
            UpdateSocialLog.LogAdd(new ChatLogLine("[WHISPER FROM] " + name + ": " + typed, ChatLogLine.LogType.Whisper, "#EF0BAC"));
        }

        /// <summary>Once per play session, good friends who are online say hi.</summary>
        internal static void GreetOnLogin()
        {
            if (!On || greetedFor == WorldContext.PlayerName || GameData.SimMngr?.Sims == null) return;
            greetedFor = WorldContext.PlayerName;
            Friendships friends = Plugin.FriendsNow();
            var close = friends.All
                .Where(p => p.Key.StartsWith("sim:") && Friendships.TierOf(p.Value.Points) >= 3)
                .Select(p => Tracking(Friendships.NameOf(p.Key)))
                .Where(t => t != null && t.online > 0)
                .OrderBy(_ => Plugin.Rng.Next())
                .Take(2)
                .ToList();
            float delay = UnityEngine.Random.Range(12f, 25f);
            foreach (SimPlayerTracking t in close)
            {
                Friendship f = friends.Get(Friendships.Sim(t.SimName));
                string section = (DateTime.UtcNow - f.LastSeen).TotalDays >= 3 ? "friend.login.longtime" : "friend.login";
                string line = Lines.Fill(Lines.Pick(section, SimLife.PersonaOf(t.MyAvatar)),
                    new Dictionary<string, string> { ["player"] = WorldContext.PlayerName });
                if (line == null) continue;
                Scheduler.After(delay, () => Whisper(t.SimName, t.MyAvatar, line));
                delay += UnityEngine.Random.Range(5f, 15f);
            }
        }

        /// <summary>A "remember when..." line drawn from this character's memories of you, or null.</summary>
        internal static string Recall(string key, SimPlayer sim)
        {
            if (!On) return null;
            Friendships friends = Plugin.FriendsNow();
            if (!friends.Knows(key)) return null;
            Friendship f = friends.Get(key);
            if (f.Memories.Count == 0) return null;
            // Recent memories come up more often than old ones.
            int count = f.Memories.Count;
            int index = count - 1 - (int)(Math.Pow(Plugin.Rng.NextDouble(), 2) * count);
            string memory = f.Memories[Math.Max(0, index)].Value;
            string line = Lines.Pick("friend.recall", SimLife.PersonaOf(sim));
            return Lines.Fill(line, new Dictionary<string, string> { ["memory"] = memory });
        }
    }

    /// <summary>/friends lists your closest bonds; /memories Name shows what someone remembers about you.</summary>
    [HarmonyPatch(typeof(TypeText), "CheckCommands")]
    internal static class FriendCommands
    {
        private static readonly System.Reflection.FieldInfo TypedField = AccessTools.Field(typeof(TypeText), "typed");

        private static bool Prefix(TypeText __instance)
        {
            object typed = TypedField?.GetValue(__instance);
            string text = (typed?.GetType().GetProperty("text")?.GetValue(typed) as string ?? "").Trim();
            if (text == "/friends") { ListFriends(); return false; }
            if (text == "/memories" || text.StartsWith("/memories "))
            {
                string name = text.Length > 9 ? text.Substring(10).Trim() : "";
                if (name.Length == 0 && GameData.PlayerControl.CurrentTarget != null) name = GameData.PlayerControl.CurrentTarget.name;
                ShowMemories(name);
                return false;
            }
            return true;
        }

        private static void ListFriends()
        {
            var list = Plugin.FriendsNow().All.Where(p => p.Value.Points >= Friendships.TierPoints[1])
                .OrderByDescending(p => p.Value.Points).Take(12).ToList();
            if (list.Count == 0)
            {
                UpdateSocialLog.LogAdd("No friendships yet. Adventure, talk and emote with sims and NPCs to build them.", "yellow");
                return;
            }
            UpdateSocialLog.LogAdd("Your closest bonds:", "#7FD1AE");
            foreach (var p in list)
            {
                Friendship f = p.Value;
                UpdateSocialLog.LogAdd("  " + Friendships.NameOf(p.Key) + " - " + Friendships.TierNames[Friendships.TierOf(f.Points)] +
                                       " (" + (int)f.Points + ") - met " + Friendships.Ago(f.FirstMet) + ", " + f.Memories.Count + " memories", "#7FD1AE");
            }
        }

        private static void ShowMemories(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                UpdateSocialLog.LogAdd("Usage: /memories Name (or target someone and type /memories)", "yellow");
                return;
            }
            Friendships friends = Plugin.FriendsNow();
            string key = friends.Knows(Friendships.Sim(name)) ? Friendships.Sim(name) : Friendships.Npc(name);
            if (!friends.Knows(key))
            {
                UpdateSocialLog.LogAdd(name + " doesn't have any memories of you yet.", "yellow");
                return;
            }
            Friendship f = friends.Get(key);
            UpdateSocialLog.LogAdd(name + " (" + Friendships.TierNames[Friendships.TierOf(f.Points)] + ") remembers:", "#7FD1AE");
            foreach (var m in f.Memories.Skip(Math.Max(0, f.Memories.Count - 10)))
            {
                UpdateSocialLog.LogAdd("  " + Friendships.Ago(m.Key) + ": " + m.Value, "#7FD1AE");
            }
        }
    }
}
