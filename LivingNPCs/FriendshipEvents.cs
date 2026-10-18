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

        // ---------------------------------------------------------------- guilds

        public static string PlayerGuild => GameData.PlayerControl != null ? GameData.PlayerControl.MyGuild : null;

        public static bool IsGuildmate(string simName)
        {
            string guild = PlayerGuild;
            if (string.IsNullOrEmpty(guild)) return false;
            SimPlayerTracking t = Tracking(simName);
            return t != null && t.GuildID == guild;
        }

        public static string GuildName()
        {
            string guild = PlayerGuild;
            if (string.IsNullOrEmpty(guild)) return null;
            try { return GameData.GuildManager?.GetGuildDataByID(guild)?.GuildName; }
            catch { return null; }
        }

        /// <summary>The guild's shared memory of you: news guildmates pass around.</summary>
        public static string GuildKey => "guild:" + PlayerGuild;

        /// <summary>
        /// Records something about you as guild gossip, retold from a guildmate's point of view:
        /// "we wiped together in Rottenfoot" from Kessa becomes "you and Kessa wiped together in Rottenfoot".
        /// </summary>
        public static void GuildNews(string simName, string memory)
        {
            if (!On || string.IsNullOrEmpty(PlayerGuild) || string.IsNullOrEmpty(memory)) return;
            string news = memory.Trim();
            if (simName != null)
            {
                news = System.Text.RegularExpressions.Regex.Replace(news, @"^we\b", "you and " + simName);
                news = System.Text.RegularExpressions.Regex.Replace(news, @"^i\b", simName);
                news = System.Text.RegularExpressions.Regex.Replace(news, @"\bme\b", simName);
                news = System.Text.RegularExpressions.Regex.Replace(news, @"\bmy\b", simName + "'s");
            }
            Plugin.FriendsNow().Remember(GuildKey, news, "event");
        }

        public static void GuildChatSaid(string text)
        {
            if (!On || string.IsNullOrEmpty(PlayerGuild) || string.IsNullOrWhiteSpace(text)) return;
            string said = text.Trim();
            if (said.Length > 160) said = said.Substring(0, 160);
            Plugin.FriendsNow().Remember(GuildKey, "you said in guild chat: \"" + said + "\"", "chat");
        }

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
            // Only things you did together read well after "remember when".
            var shared = f.Memories.FindAll(m => m.Kind == "event" || m.Kind == "milestone");
            if (shared.Count == 0) return null;
            // Recent memories come up more often than old ones.
            int count = shared.Count;
            int index = count - 1 - (int)(Math.Pow(Plugin.Rng.NextDouble(), 2) * count);
            string memory = shared[Math.Max(0, index)].Text;
            string line = Lines.Pick("friend.recall", SimLife.PersonaOf(sim));
            return Lines.Fill(line, new Dictionary<string, string> { ["memory"] = memory });
        }
    }

    /// <summary>Guild chat you type becomes something your guildmates remember.</summary>
    [HarmonyPatch(typeof(GuildManager), nameof(GuildManager.ParseGuildChatInput))]
    internal static class GuildChatMemory
    {
        private static void Prefix(string _input, string _fromPlayer)
        {
            try
            {
                if (_fromPlayer == "Player") FriendshipEvents.GuildChatSaid(_input);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Guild chat memory failed: " + e);
            }
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

        internal static void ShowMemories(string name)
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
            bool guildmate = key.StartsWith("sim:") && FriendshipEvents.IsGuildmate(name);
            UpdateSocialLog.LogAdd(name + " (" + Friendships.TierNames[Friendships.TierOf(f.Points)] + (guildmate ? ", guildmate" : "") + ") remembers:", "#7FD1AE");
            SimPlayerTracking who = FriendshipEvents.Tracking(name);
            if (who != null)
            {
                string persona = (who.MyAvatar != null ? SimVoices.PersonaOf(who.MyAvatar) : null) ?? WorldIndex.PersonaOf(who.SimName);
                if (persona != null) UpdateSocialLog.LogAdd("  Personality: " + persona + " (" + SimVoices.AboutOf(persona) + ")", "#7FD1AE");
                UpdateSocialLog.LogAdd("  The real person: " + RealLife.Of(who.SimName, persona).Replace("Outside the game you're a real person: ", ""), "#7FD1AE");
            }
            if (!string.IsNullOrEmpty(f.Summary)) UpdateSocialLog.LogAdd("  In short: " + f.Summary, "#7FD1AE");
            string feel = Feelings.Describe(f);
            if (feel.Length > 0) UpdateSocialLog.LogAdd("  Feelings: " + feel.Replace("them", "you").Replace("they", "you"), "#7FD1AE");
            foreach (string kind in new[] { "fact", "plan", "milestone", "event" })
            {
                var items = f.Memories.FindAll(m => m.Kind == kind);
                if (items.Count == 0) continue;
                string label = kind == "fact" ? "About you" : kind == "plan" ? "Plans" : kind == "milestone" ? "Milestones" : "Together";
                foreach (MemoryItem m in items.Skip(Math.Max(0, items.Count - (kind == "event" ? 6 : 8))))
                {
                    UpdateSocialLog.LogAdd("  " + label + " (" + Friendships.Ago(m.When) + "): " + m.Text, "#7FD1AE");
                }
            }
        }
    }
}
