using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityEngine;

namespace LivingNPCs
{
    /// <summary>
    /// Lets you actually talk to sim players. (NPCs stay scripted, as in a live MMO.) What you type goes to a local AI model along with who
    /// the character is: their persona, mood, history with you, the zone, and Erenshor's lore. The
    /// reply comes back through the game's own chat, typing quirks and all. The game's normal handling
    /// of what you type still happens; the AI only adds conversation.
    /// </summary>
    public class AIChat : MonoBehaviour
    {
        private enum Channel { Say, Party, Whisper }

        private const int HistoryLength = 8;

        // Session-only fallback when friendships are off; otherwise conversations are saved with the friendship.
        private static readonly Dictionary<string, List<Ollama.Message>> History = new Dictionary<string, List<Ollama.Message>>();

        private class Pending { public string Name; public SimPlayer Sim; public int Exchanges; public float LastAt; }
        private static readonly Dictionary<string, Pending> ToSummarize = new Dictionary<string, Pending>();
        private float nextSummaryCheck;
        private static readonly Queue<Action> Waiting = new Queue<Action>();

        // Messages about grouping, trading and so on are left entirely to the game's own sims.
        private static readonly Regex GameCommandWords = new Regex(
            @"\b(inv|invite|join|lfg|lfm|group up|grp|trade|wts|wtb|disband|kick|follow|guild|raid)\b",
            RegexOptions.IgnoreCase);

        public static bool Ready { get; private set; }
        private static bool preparing;
        private static int attempts;
        private static float nextRetry = 8f;
        private static bool announced;
        private static float lastFailureNotice = -999f;

        private void Start()
        {
        }

        private void Update()
        {
            Ollama.Pump();

            // Keep trying to connect (Ollama may start after the game), and say how it went once you're in the world.
            if (Plugin.AiEnabled.Value && !Ready && !preparing && Time.time >= nextRetry)
            {
                nextRetry = Time.time + 60f;
                Prepare(quiet: true);
            }
            if (!announced && Plugin.AiEnabled.Value && WorldContext.PlayerReady && attempts > 0)
            {
                announced = true;
                UpdateSocialLog.LogAdd(Ready
                    ? "Living NPCs: AI chat is on (" + Plugin.AiModel.Value + "). Target a sim, or stand next to one, and type to talk."
                    : "Living NPCs: AI chat isn't ready yet: " + (string.IsNullOrEmpty(Ollama.LastError) ? Ollama.Phase : Ollama.LastError) + ". Type /ai for progress.", Ready ? "#7FD1AE" : "yellow");
            }

            if (Waiting.Count > 0 && Ready && !Ollama.Busy) Waiting.Dequeue()();
            if (Time.time >= nextSummaryCheck)
            {
                nextSummaryCheck = Time.time + 10f;
                SummarizeIdleConversations();
            }
        }

        private void OnApplicationQuit()
        {
            if (Plugin.AiStopOllamaOnQuit.Value) Ollama.StopIfWeStartedIt();
        }

        public static void Prepare(bool quiet)
        {
            if (preparing) return;
            preparing = true;
            attempts++;
            Ollama.Prepare(
                message =>
                {
                    Plugin.Log.LogInfo(message);
                    // Routine connection chatter stays in the log; downloads and explicit /ai requests show in chat.
                    if (!quiet || message.Contains("downloading") || message.Contains("failed")) TryChat(message, "yellow");
                },
                ok =>
                {
                    Ready = ok;
                    preparing = false;
                    if (ok && attempts > 1 && announced) TryChat("Living NPCs: AI chat connected (" + Plugin.AiModel.Value + ").", "#7FD1AE");
                });
        }

        /// <summary>Chat messages sent before the chat window exists (main menu) would be lost or throw.</summary>
        private static void TryChat(string message, string color)
        {
            if (!WorldContext.PlayerReady) return;
            try { UpdateSocialLog.LogAdd(message, color); }
            catch (Exception) { }
        }

        /// <summary>Sends a fixed test prompt and prints the result, for /ai test.</summary>
        public static void Test()
        {
            if (!Ready)
            {
                UpdateSocialLog.LogAdd(preparing
                    ? "AI test: still getting ready: " + Ollama.Phase + ". Try again when that finishes."
                    : "AI test: not connected. " + Ollama.LastError + " Retrying now...", "yellow");
                Prepare(quiet: false);
                return;
            }
            UpdateSocialLog.LogAdd("AI test: asking " + Plugin.AiModel.Value + "...", "yellow");
            float started = Time.time;
            var messages = new List<Ollama.Message>
            {
                new Ollama.Message("system", "You are a friendly adventurer in the fantasy game Erenshor. Reply with one short sentence."),
                new Ollama.Message("user", "Hello! How are you today?"),
            };
            Ollama.Chat(messages, 40, reply =>
            {
                UpdateSocialLog.LogAdd(reply != null
                    ? "AI test reply (" + (Time.time - started).ToString("0.0") + "s): " + reply
                    : "AI test failed: " + Ollama.LastError, reply != null ? "#7FD1AE" : "yellow");
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
                Ask(speaker.name, speaker, Channel.Party, message);
                return;
            }

            if (text.StartsWith("/whisper ") || text.StartsWith("/w "))
            {
                string[] parts = text.Split(new[] { ' ' }, 3);
                if (parts.Length < 3 || GameCommandWords.IsMatch(parts[2])) return;
                SimPlayerTracking tracking = GameData.SimMngr.Sims.Find(s => string.Equals(s.SimName, parts[1], StringComparison.OrdinalIgnoreCase));
                if (tracking == null) return;
                Ask(tracking.SimName, tracking.MyAvatar, Channel.Whisper, parts[2]);
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
                    Ask(sim.name, sim, Channel.Say, text);
                    return;
                }
                // Talking to an NPC is the game's business: they answer with their scripted dialogue, never AI.
                if (target.GetComponent<NPCDialogManager>() != null) return;
            }
            foreach (SimPlayer sim in SimLife.NearbySims(20f, includeGroup: true))
            {
                if (Mentions(text, sim.name))
                {
                    Ask(sim.name, sim, Channel.Say, text);
                    return;
                }
            }
            // Talking right next to a sim without targeting anyone: the closest one answers.
            float radius = Plugin.AiNearbyRadius.Value;
            if (radius > 0f)
            {
                SimPlayer closest = null;
                float best = radius;
                foreach (SimPlayer sim in SimLife.NearbySims(radius, includeGroup: true))
                {
                    float d = Vector3.Distance(sim.transform.position, SimLife.PlayerPos);
                    if (d <= best && SimLife.Idle(sim))
                    {
                        best = d;
                        closest = sim;
                    }
                }
                if (closest != null)
                {
                    Ask(closest.name, closest, Channel.Say, text);
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
                    Ask(sim.name, sim, Channel.Say, text);
                }
            }
        }

        private static bool Mentions(string text, string name) =>
            !string.IsNullOrEmpty(name) && Regex.IsMatch(text, @"\b" + Regex.Escape(name) + @"\b", RegexOptions.IgnoreCase);

        // ================================================================ asking the model

        private static void Ask(string who, SimPlayer sim, Channel channel, string playerText)
        {
            if (Waiting.Count >= 3) Waiting.Dequeue();
            Waiting.Enqueue(() => Send(who, sim, channel, playerText));
        }

        private static void Send(string who, SimPlayer sim, Channel channel, string playerText)
        {
            Plugin.Bonds.UseCharacter(WorldContext.PlayerName);
            string key = Friendships.Sim(who);
            bool remember = FriendshipEvents.On;
            string friendship = remember ? FriendshipText(key, who) : "";
            string system = SimPrompt(who, sim, channel).Replace("{friendship}", friendship);
            List<Ollama.Message> history;
            if (remember)
            {
                Plugin.FriendsNow().Meet(key, WorldContext.Zone);
                history = Plugin.Friends.Get(key).Conversation;
            }
            else if (!History.TryGetValue(who, out history))
            {
                history = new List<Ollama.Message>();
                History[who] = history;
            }
            string heard = WorldContext.PlayerName + (channel == Channel.Party ? " (to the group): " : channel == Channel.Whisper ? " (whispering to you): " : ": ") + playerText;
            var messages = new List<Ollama.Message> { new Ollama.Message("system", system) };
            messages.AddRange(history);
            messages.Add(new Ollama.Message("user", heard));

            float asked = Time.time;
            Ollama.Chat(messages, 70, reply =>
            {
                if (reply == null && Time.time - lastFailureNotice > 60f)
                {
                    lastFailureNotice = Time.time;
                    TryChat("Living NPCs: " + who + " couldn't answer: " + Ollama.LastError + ". Type /ai test to check.", "yellow");
                }
                // Pull out any action the sim agreed to before tidying the text (tags can trail off the first line).
                reply = SimActions.Extract(reply, out string action, out string argument);
                reply = Clean(reply, who, SimLife.PersonaOf(sim) != "roleplayer");
                if (string.IsNullOrEmpty(reply)) return;
                string remembered = action == null ? reply : reply + " [" + action + (argument != null ? ":" + argument : "") + "]";
                if (remember)
                {
                    Friendships friends = Plugin.FriendsNow();
                    friends.AddConversation(key, new Ollama.Message("user", heard), new Ollama.Message("assistant", remembered));
                    friends.Add(key, 1f);
                    if (!ToSummarize.TryGetValue(key, out Pending p)) ToSummarize[key] = p = new Pending { Name = who, Sim = sim };
                    p.Exchanges++;
                    p.LastAt = Time.time;
                }
                else
                {
                    history.Add(new Ollama.Message("user", heard));
                    history.Add(new Ollama.Message("assistant", remembered));
                    while (history.Count > HistoryLength * 2) history.RemoveAt(0);
                }

                // A short pause to "type", so replies don't arrive impossibly fast.
                float typing = Mathf.Clamp(reply.Length * 0.035f - (Time.time - asked), 0.3f, 3f);
                Scheduler.After(typing, () =>
                {
                    Deliver(who, sim, channel, reply);
                    if (action != null) Scheduler.After(UnityEngine.Random.Range(1f, 2.5f), () => SimActions.Perform(who, sim, action, argument));
                });
            });
        }

        private static void Deliver(string who, SimPlayer sim, Channel channel, string reply)
        {
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

        // ================================================================ long-term memory

        /// <summary>
        /// Once a conversation has gone quiet, asks the model what the character should remember
        /// from it, and keeps that as a memory of you.
        /// </summary>
        private static void SummarizeIdleConversations()
        {
            if (!Ready || Ollama.Busy || Waiting.Count > 0 || ToSummarize.Count == 0) return;
            foreach (var pair in ToSummarize)
            {
                Pending p = pair.Value;
                if (Time.time - p.LastAt < 60f) continue;
                string key = pair.Key;
                ToSummarize.Remove(key);
                if (p.Exchanges < 2) return;

                List<Ollama.Message> convo = Plugin.FriendsNow().Get(key).Conversation;
                var transcript = new StringBuilder();
                int start = Math.Max(0, convo.Count - p.Exchanges * 2);
                for (int i = start; i < convo.Count; i++)
                {
                    transcript.Append(convo[i].Role == "assistant" ? p.Name + ": " : "").Append(convo[i].Content).Append('\n');
                }
                var tokens = BaseTokens();
                tokens["name"] = p.Name;
                tokens["transcript"] = transcript.ToString();
                var messages = new List<Ollama.Message>
                {
                    new Ollama.Message("system", Fill(Section("ai.memory"), tokens)),
                    new Ollama.Message("user", transcript.ToString()),
                };
                Ollama.Chat(messages, 50, summary =>
                {
                    summary = Clean(summary, p.Name, true);
                    if (string.IsNullOrEmpty(summary) || summary.ToUpperInvariant().StartsWith("NONE")) return;
                    summary = summary.TrimEnd('.', '!');
                    if (summary.Length > 0) summary = char.ToLowerInvariant(summary[0]) + summary.Substring(1);
                    Plugin.FriendsNow().Remember(key, summary);
                    Plugin.Log.LogInfo(p.Name + " will remember: " + summary);
                });
                return;
            }
        }

        private static string FriendshipText(string key, string who)
        {
            Friendships friends = Plugin.FriendsNow();
            if (!friends.Knows(key)) return "You have never met " + WorldContext.PlayerName + " before.";
            Friendship f = friends.Get(key);
            int tier = Friendships.TierOf(f.Points);
            var sb = new StringBuilder();
            sb.Append("Your friendship with ").Append(WorldContext.PlayerName).Append(": ")
              .Append(tier == 0 ? "barely know each other" : Friendships.TierNames[tier]).Append(". ");
            if (f.Points < 0) sb.Append("You have bad feelings about them. ");
            sb.Append("You first met ").Append(Friendships.Ago(f.FirstMet)).Append(" and last spoke ").Append(Friendships.Ago(f.LastSeen)).Append(". ");
            if (f.Memories.Count > 0)
            {
                sb.Append("Things you remember about them: ");
                int from = Math.Max(0, f.Memories.Count - 12);
                for (int i = from; i < f.Memories.Count; i++)
                {
                    sb.Append(f.Memories[i].Value).Append(" (").Append(Friendships.Ago(f.Memories[i].Key)).Append(")");
                    sb.Append(i < f.Memories.Count - 1 ? "; " : ". ");
                }
            }
            sb.Append(tier >= 3 ? "Be warm and familiar, like an old friend; bring up shared memories when it fits."
                    : tier >= 2 ? "Be friendly; you can mention shared memories now and then."
                    : "You don't know them well yet.");
            return sb.ToString();
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
            tokens["zones"] = SimActions.ZoneList();
            string actions = Section("ai.actions");
            if (string.IsNullOrEmpty(actions) || !actions.Contains("[GUIDE")) actions = DefaultActions;
            tokens["actions"] = Plugin.AiActions.Value ? actions : "";
            tokens["simzone"] = tracking != null ? GetCommonTerms.GetZoneTerm(tracking.CurScene) : WorldContext.Zone;
            bool sameZone = tracking != null && tracking.CurScene == WorldContext.Zone;
            tokens["places"] = sameZone ? SimEscort.PlacesHere() : "(you're not in the same zone, so you can't guide right now)";
            List<Spell> buffs = sameZone ? SimActions.BuffsFor(sim) : new List<Spell>();
            var buffNames = buffs.ConvertAll(b => b.SpellName);
            tokens["buffs"] = buffs.Count > 0 ? "Buffs you can cast on others: " + string.Join(", ", buffNames) + "."
                : "You have no buffs you can cast on others right now, so never offer one.";
            tokens["channel"] = channel == Channel.Party ? "group chat" : channel == Channel.Whisper ? "a private whisper" : "local /say chat";
            string prompt = Section("ai.sim");
            // Older copies of ai.txt don't mention actions; add them so sims can still act.
            if (Plugin.AiActions.Value && !prompt.Contains("{actions}")) prompt += " {actions}";
            return Fill(Fill(prompt, tokens), tokens) + " " + Fill(Section("ai.world"), tokens);
        }

        private const string DefaultActions = "You can act on what you say. If you agree to do something right now, end your message with ONE action tag: [JOIN] to join {player}'s group (same zone only), [HELP] to join just to help with a fight and leave afterwards, [COME] to travel to where {player} is and join them, [MEET:zone name] to go to a zone and wait at its entrance for {player}, [GUIDE:place] to lead {player} to a place or NPC in your current zone, [FOLLOW] to follow {player} around without grouping, [WAIT] to stay where you are, [BUFF] to cast your buffs on {player}, [LEAVE] to leave {player}'s group, or an emote: [WAVE] [DANCE] [SIT] [CHEER] [BOW] [LAUGH]. Only use a tag when you really mean to do it now, and never promise something without the tag. Never offer to trade, sell or give items. Zones you can name: {zones}. You are currently in {simzone}. Places and NPCs in this zone you can guide to: {places}. {buffs}";

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

            if (arg == "test")
            {
                AIChat.Test();
            }
            else if (arg == "on")
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
                string name = arg.Substring(6).Trim().ToLowerInvariant();
                if (!Ollama.LooksLikeModel(name))
                {
                    UpdateSocialLog.LogAdd("'" + name + "' isn't a model name. Try /ai model qwen2.5:14b, llama3.1:8b or llama3.2:3b.", "yellow");
                    return false;
                }
                Plugin.AiModel.Value = name;
                UpdateSocialLog.LogAdd("Living NPCs: AI model set to " + Plugin.AiModel.Value + ".", "yellow");
                AIChat.Prepare(quiet: false);
            }
            else
            {
                string state = !Plugin.AiEnabled.Value ? "OFF" : AIChat.Ready ? "ON and connected" : "ON but NOT connected";
                UpdateSocialLog.LogAdd("Living NPCs AI chat: " + state + ". Model: " + Plugin.AiModel.Value + ", Ollama at " + Plugin.AiUrl.Value + ".", "yellow");
                if (Plugin.AiEnabled.Value && !AIChat.Ready) UpdateSocialLog.LogAdd("Right now: " + Ollama.Phase, "yellow");
                bool looping = Host.LastBeat >= 0f && Time.realtimeSinceStartup - Host.LastBeat < 2f;
                UpdateSocialLog.LogAdd("Mod update loop: " + (looping ? "running" : "NOT running (please report this)") +
                                       (Host.Starts > 1 ? ", restarted " + (Host.Starts - 1) + " times" : ""), looping ? "yellow" : "red");
                if (!string.IsNullOrEmpty(Ollama.LastError)) UpdateSocialLog.LogAdd("Last problem: " + Ollama.LastError, "yellow");
                UpdateSocialLog.LogAdd("To talk: target a sim (or stand next to one) and type; /p for your group; /w Name to whisper. NPCs keep their scripted dialogue.", "yellow");
                UpdateSocialLog.LogAdd("Commands: /ai test, /ai on, /ai off, /ai model NAME", "yellow");
            }
            return false;
        }
    }
}
