using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityEngine;

namespace LivingNPCs
{
    /// <summary>
    /// Lets you actually talk to sims and NPCs. What you type goes to a local AI model along with who
    /// the character is: their persona, mood, history with you, the zone, and Erenshor's lore. The
    /// reply comes back through the game's own chat, typing quirks and all. The game's normal handling
    /// of what you type still happens; the AI only adds conversation.
    /// </summary>
    public class AIChat : MonoBehaviour
    {
        private enum Channel { Say, Party, Whisper }

        private const int HistoryLength = 8;

        private static readonly Dictionary<string, List<Ollama.Message>> History = new Dictionary<string, List<Ollama.Message>>();
        private static readonly Queue<Action> Waiting = new Queue<Action>();

        // Messages about grouping, trading and so on are left entirely to the game's own sims.
        private static readonly Regex GameCommandWords = new Regex(
            @"\b(inv|invite|join|lfg|lfm|group up|grp|trade|wts|wtb|disband|kick|follow|guild|raid)\b",
            RegexOptions.IgnoreCase);

        public static bool Ready { get; private set; }
        private static bool preparing;

        private void Start()
        {
            if (Plugin.AiEnabled.Value) Scheduler.After(8f, () => Prepare(quiet: true));
        }

        private void Update()
        {
            Ollama.Pump();
            if (Waiting.Count > 0 && Ready && !Ollama.Busy) Waiting.Dequeue()();
        }

        private void OnApplicationQuit()
        {
            if (Plugin.AiStopOllamaOnQuit.Value) Ollama.StopIfWeStartedIt();
        }

        public static void Prepare(bool quiet)
        {
            if (preparing) return;
            preparing = true;
            Ollama.Prepare(
                message =>
                {
                    Plugin.Log.LogInfo(message);
                    if (!quiet || !message.Contains("isn't running")) UpdateSocialLog.LogAdd(message, "yellow");
                },
                ok =>
                {
                    Ready = ok;
                    preparing = false;
                });
        }

        // ================================================================ deciding who answers

        /// <summary>Called with what the player just typed, before the game clears it.</summary>
        internal static void OnPlayerTyped(string text)
        {
            if (!Plugin.AiEnabled.Value || !Ready || string.IsNullOrEmpty(text) || !WorldContext.PlayerReady) return;
            text = text.Trim();

            if (text.StartsWith("/party ") || text.StartsWith("/p "))
            {
                string message = text.Substring(text.IndexOf(' ') + 1).Trim();
                if (message.Length == 0 || GameCommandWords.IsMatch(message)) return;
                var group = new List<SimPlayer>();
                foreach (SimPlayerTracking m in GameData.GroupMembers)
                {
                    if (m?.MyAvatar != null && SimLife.Alive(m.MyAvatar)) group.Add(m.MyAvatar);
                }
                if (group.Count == 0) return;
                SimPlayer named = group.Find(s => Mentions(message, s.name));
                SimPlayer speaker = named ?? group[Plugin.Rng.Next(group.Count)];
                Ask(speaker.name, speaker, null, Channel.Party, message);
                return;
            }

            if (text.StartsWith("/whisper ") || text.StartsWith("/w "))
            {
                string[] parts = text.Split(new[] { ' ' }, 3);
                if (parts.Length < 3 || GameCommandWords.IsMatch(parts[2])) return;
                SimPlayerTracking tracking = GameData.SimMngr.Sims.Find(s => string.Equals(s.SimName, parts[1], StringComparison.OrdinalIgnoreCase));
                if (tracking == null) return;
                Ask(tracking.SimName, tracking.MyAvatar, null, Channel.Whisper, parts[2]);
                return;
            }

            if (text.StartsWith("/")) return;
            if (GameCommandWords.IsMatch(text)) return;

            // Plain /say: whoever you're targeting, or a nearby sim you named.
            Character target = GameData.PlayerControl.CurrentTarget;
            if (target != null && target != GameData.PlayerControl.Myself &&
                Vector3.Distance(target.transform.position, SimLife.PlayerPos) <= 20f)
            {
                SimPlayer sim = target.GetComponent<SimPlayer>();
                if (sim != null && SimLife.Alive(sim))
                {
                    Ask(sim.name, sim, null, Channel.Say, text);
                    return;
                }
                NPC npc = target.GetComponent<NPC>();
                NPCDialogManager manager = target.GetComponent<NPCDialogManager>();
                if (npc != null && manager != null && npc.CheckLiving() && !MatchesKeyword(manager, text) &&
                    Vector3.Distance(target.transform.position, SimLife.PlayerPos) <= 8f)
                {
                    Ask(npc.NPCName, null, npc, Channel.Say, text);
                    return;
                }
            }
            foreach (SimPlayer sim in SimLife.NearbySims(20f, includeGroup: true))
            {
                if (Mentions(text, sim.name))
                {
                    Ask(sim.name, sim, null, Channel.Say, text);
                    return;
                }
            }
            // A question asked out loud might get an answer from someone close by.
            if (text.EndsWith("?") && Plugin.Rng.NextDouble() < Plugin.AiOverhearChance.Value)
            {
                var near = SimLife.NearbySims(10f, includeGroup: true).FindAll(SimLife.Idle);
                if (near.Count > 0)
                {
                    SimPlayer sim = near[Plugin.Rng.Next(near.Count)];
                    Ask(sim.name, sim, null, Channel.Say, text);
                }
            }
        }

        private static bool Mentions(string text, string name) =>
            !string.IsNullOrEmpty(name) && Regex.IsMatch(text, @"\b" + Regex.Escape(name) + @"\b", RegexOptions.IgnoreCase);

        /// <summary>Quest keywords belong to the game: if you said one, the NPC answers with its real dialogue.</summary>
        private static bool MatchesKeyword(NPCDialogManager manager, string text)
        {
            string lower = text.ToLowerInvariant();
            NPCDialog[] options = manager.MyDialogOptions ?? manager.GetComponents<NPCDialog>();
            foreach (NPCDialog option in options)
            {
                if (option?.KeywordToActivate == null) continue;
                foreach (string keyword in option.KeywordToActivate)
                {
                    if (!string.IsNullOrEmpty(keyword) && lower.Contains(keyword.ToLowerInvariant())) return true;
                }
            }
            return false;
        }

        // ================================================================ asking the model

        private static void Ask(string who, SimPlayer sim, NPC npc, Channel channel, string playerText)
        {
            if (Waiting.Count >= 3) Waiting.Dequeue();
            Waiting.Enqueue(() => Send(who, sim, npc, channel, playerText));
        }

        private static void Send(string who, SimPlayer sim, NPC npc, Channel channel, string playerText)
        {
            Plugin.Bonds.UseCharacter(WorldContext.PlayerName);
            string system = npc != null ? NpcPrompt(npc) : SimPrompt(who, sim, channel);
            if (!History.TryGetValue(who, out List<Ollama.Message> history))
            {
                history = new List<Ollama.Message>();
                History[who] = history;
            }
            string heard = WorldContext.PlayerName + (channel == Channel.Party ? " (to the group): " : channel == Channel.Whisper ? " (whispering to you): " : ": ") + playerText;
            var messages = new List<Ollama.Message> { new Ollama.Message("system", system) };
            messages.AddRange(history);
            messages.Add(new Ollama.Message("user", heard));

            float asked = Time.time;
            Ollama.Chat(messages, npc != null ? 110 : 70, reply =>
            {
                reply = Clean(reply, who, npc == null && SimLife.PersonaOf(sim) != "roleplayer");
                if (string.IsNullOrEmpty(reply)) return;
                history.Add(new Ollama.Message("user", heard));
                history.Add(new Ollama.Message("assistant", reply));
                while (history.Count > HistoryLength * 2) history.RemoveAt(0);

                // A short pause to "type", so replies don't arrive impossibly fast.
                float typing = Mathf.Clamp(reply.Length * 0.035f - (Time.time - asked), 0.3f, 3f);
                Scheduler.After(typing, () => Deliver(who, sim, npc, channel, reply));
            });
        }

        private static void Deliver(string who, SimPlayer sim, NPC npc, Channel channel, string reply)
        {
            if (npc != null)
            {
                if (npc == null || !npc.CheckLiving()) return;
                Speech.Face(npc, SimLife.PlayerPos);
                Speech.NpcSay(npc, reply);
                return;
            }
            switch (channel)
            {
                case Channel.Party:
                    if (sim != null) Speech.Party(sim, reply);
                    break;
                case Channel.Whisper:
                    string typed = sim != null ? GameData.SimMngr.PersonalizeString(reply, sim) : reply;
                    UpdateSocialLog.LogAdd(new ChatLogLine("[WHISPER FROM] " + who + ": " + typed, ChatLogLine.LogType.Whisper, "#EF0BAC"));
                    break;
                default:
                    if (sim != null && SimLife.Alive(sim))
                    {
                        if (sim.GetComponent<NPC>()?.CurrentAggroTarget == null) Speech.Face(sim, SimLife.PlayerPos);
                        Speech.Say(sim, reply);
                    }
                    break;
            }
        }

        /// <summary>Keeps one short chat line: no quotes, no "Name:" prefix, no narration.</summary>
        private static string Clean(string reply, string who, bool stripActions)
        {
            if (string.IsNullOrWhiteSpace(reply)) return null;
            reply = reply.Trim();
            int newline = reply.IndexOf('\n');
            if (newline > 0) reply = reply.Substring(0, newline).Trim();
            if (reply.StartsWith(who + ":", StringComparison.OrdinalIgnoreCase)) reply = reply.Substring(who.Length + 1).Trim();
            reply = reply.Trim('"', '“', '”');
            if (stripActions) reply = Regex.Replace(reply, @"\*[^*]*\*", "").Trim();
            if (reply.Length > 240) reply = reply.Substring(0, 240).TrimEnd() + "...";
            return reply.Length == 0 ? null : reply;
        }

        // ================================================================ prompts

        private static string Section(string name)
        {
            var sb = new StringBuilder();
            foreach (string line in Plugin.Ai.Get(name)) sb.Append(line).Append(' ');
            return sb.ToString().Trim();
        }

        private static string Fill(string text, Dictionary<string, string> tokens)
        {
            foreach (var pair in tokens) text = text.Replace("{" + pair.Key + "}", pair.Value ?? "");
            return text;
        }

        private static string SimPrompt(string who, SimPlayer sim, Channel channel)
        {
            SimPlayerTracking tracking = GameData.SimMngr.Sims.Find(s => s.SimName == who);
            string persona = SimLife.PersonaOf(sim);
            string about = SimVoices.AboutOf(persona);
            if (string.IsNullOrEmpty(about)) about = Section("ai.sim.default_personality");
            float opinion = tracking != null ? tracking.OpinionOfPlayer : 5f;

            var tokens = BaseTokens();
            tokens["name"] = who;
            tokens["class"] = tracking != null && !string.IsNullOrEmpty(tracking.ClassName) ? tracking.ClassName : "adventurer";
            tokens["level"] = tracking != null ? tracking.Level.ToString() : "?";
            tokens["personality"] = about;
            tokens["mood"] = Plugin.Bonds.MoodOf(who) == "low" ? "frustrated and a bit down" : Plugin.Bonds.MoodOf(who) == "high" ? "in a great mood" : "fine";
            tokens["opinion"] = opinion >= 15f ? "you really like them" : opinion >= 6f ? "you like them" : opinion < 2f ? "you're wary of them" : "you don't know them well";
            tokens["grouped"] = sim != null && sim.InGroup ? "You are in a group with them right now." : "You are not grouped with them.";
            tokens["history"] = HistoryText(who);
            tokens["channel"] = channel == Channel.Party ? "group chat" : channel == Channel.Whisper ? "a private whisper" : "local /say chat";
            return Fill(Section("ai.sim"), tokens) + " " + Fill(Section("ai.world"), tokens);
        }

        private static string NpcPrompt(NPC npc)
        {
            var tokens = BaseTokens();
            tokens["name"] = npc.NPCName;
            tokens["role"] = WorldContext.RoleOf(npc).ToString().ToLowerInvariant();
            tokens["attitude"] = WorldContext.IsHostileToPlayer(npc) ? "You dislike and distrust them." : "You are civil toward them.";
            var said = new StringBuilder();
            foreach (NPCDialog d in npc.GetComponents<NPCDialog>())
            {
                if (d != null && !string.IsNullOrEmpty(d.Dialog)) said.Append(d.Dialog.Replace('\n', ' ')).Append(' ');
            }
            string usual = said.ToString().Trim();
            if (usual.Length > 600) usual = usual.Substring(0, 600);
            tokens["usual"] = usual.Length > 0 ? usual : "(nothing in particular)";
            return Fill(Section("ai.npc"), tokens) + " " + Fill(Section("ai.world"), tokens);
        }

        private static Dictionary<string, string> BaseTokens()
        {
            Stats p = GameData.PlayerStats;
            return new Dictionary<string, string>
            {
                ["player"] = WorldContext.PlayerName,
                ["playerclass"] = WorldContext.PlayerClass,
                ["playerlevel"] = p != null ? p.Level.ToString() : "1",
                ["zone"] = WorldContext.Zone,
                ["time"] = WorldContext.TimeOfDay,
            };
        }

        private static string HistoryText(string who)
        {
            if (!Plugin.Bonds.Knows(who)) return "You haven't adventured with them before.";
            SharedHistory h = Plugin.Bonds.History(who);
            var parts = new List<string>();
            if (h.Fights > 0) parts.Add("fought " + h.Fights + " battles together" + (string.IsNullOrEmpty(h.LastZone) ? "" : ", most recently in " + h.LastZone));
            if (h.Wipes > 0) parts.Add("wiped together " + h.Wipes + " times, last in " + h.LastWipeZone);
            if (h.PlayerDeaths > 0) parts.Add("seen them die " + h.PlayerDeaths + " times");
            if (h.SimDeaths > 0) parts.Add("died " + h.SimDeaths + " times in their groups");
            if (h.Levels > 0) parts.Add("watched them level up " + h.Levels + " times");
            return parts.Count == 0 ? "You've met but haven't done much together." : "You have " + string.Join("; ", parts) + ".";
        }
    }

    /// <summary>Hands what the player typed to the AI before the game processes and clears it.</summary>
    [HarmonyPatch(typeof(TypeText), nameof(TypeText.CheckInput))]
    internal static class AIChatInputPatch
    {
        private static readonly System.Reflection.FieldInfo TypedField = AccessTools.Field(typeof(TypeText), "typed");

        private static void Prefix(TypeText __instance)
        {
            try
            {
                object typed = TypedField?.GetValue(__instance);
                string text = typed?.GetType().GetProperty("text")?.GetValue(typed) as string;
                AIChat.OnPlayerTyped(text);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("AI input hook failed: " + e);
            }
        }
    }

    /// <summary>/ai, /ai on, /ai off, /ai model NAME</summary>
    [HarmonyPatch(typeof(TypeText), "CheckCommands")]
    internal static class AICommandPatch
    {
        private static readonly System.Reflection.FieldInfo TypedField = AccessTools.Field(typeof(TypeText), "typed");

        private static bool Prefix(TypeText __instance)
        {
            object typed = TypedField?.GetValue(__instance);
            string text = (typed?.GetType().GetProperty("text")?.GetValue(typed) as string ?? "").Trim();
            if (text != "/ai" && !text.StartsWith("/ai ")) return true;
            string arg = text.Length > 3 ? text.Substring(4).Trim() : "";

            if (arg == "on")
            {
                Plugin.AiEnabled.Value = true;
                AIChat.Prepare(quiet: false);
            }
            else if (arg == "off")
            {
                Plugin.AiEnabled.Value = false;
                UpdateSocialLog.LogAdd("Living NPCs: AI chat off. Sims use their written lines.", "yellow");
            }
            else if (arg.StartsWith("model "))
            {
                Plugin.AiModel.Value = arg.Substring(6).Trim();
                UpdateSocialLog.LogAdd("Living NPCs: AI model set to " + Plugin.AiModel.Value + ".", "yellow");
                AIChat.Prepare(quiet: false);
            }
            else
            {
                string state = !Plugin.AiEnabled.Value ? "off" : AIChat.Ready ? "on and ready" : "on, but not connected";
                UpdateSocialLog.LogAdd("Living NPCs AI chat is " + state + " (model " + Plugin.AiModel.Value + "). " +
                                       "Target a sim or NPC and type to talk, use /p for your group, or /w Name to whisper. " +
                                       "Commands: /ai on, /ai off, /ai model NAME", "yellow");
            }
            return false;
        }
    }
}
