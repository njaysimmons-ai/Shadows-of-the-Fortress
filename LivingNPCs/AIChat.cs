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

        private class Pending { public string Name; public SimPlayer Sim; public int Exchanges; public int Words; public float LastAt; }
        private static readonly Dictionary<string, Pending> ToSummarize = new Dictionary<string, Pending>();
        private float nextSummaryCheck;
        private static readonly Queue<Action> Waiting = new Queue<Action>();

        // Messages about grouping, trading and so on are left entirely to the game's own sims.
        private static readonly Regex GameCommandWords = new Regex(
            @"\b(inv|invite|join|lfg|lfm|group up|grp|trade|wts|wtb|disband|kick|follow|guild|raid)\b",
            RegexOptions.IgnoreCase);

        public static bool Ready { get; private set; }

        /// <summary>/ai debug: shows raw replies and what the mod did with them.</summary>
        public static bool Debug;

        public static void DebugNote(string text)
        {
            if (Debug) TryChat("[AI debug] " + text, "#9AA0A6");
        }
        private static bool preparing, reconnectAfter;
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
                    preparing = false;
                    if (reconnectAfter)
                    {
                        // The model changed while connecting: start again with the new one.
                        reconnectAfter = false;
                        Prepare(quiet: false);
                        return;
                    }
                    Ready = ok;
                    if (ok && attempts > 1 && announced) TryChat("Living NPCs: AI chat connected (" + Plugin.AiModel.Value + ").", "#7FD1AE");
                });
        }

        /// <summary>Switches the chat model (downloading it if needed) and reconnects.</summary>
        public static void SetChatModel(string name)
        {
            if (name == Plugin.AiModel.Value && Ready) return;
            Plugin.AiModel.Value = name;
            TryChat("Living NPCs: sims will now talk with " + name + ".", "yellow");
            Reconnect();
        }

        /// <summary>Sets the thinking model ("" = the chat model does everything) and reconnects.</summary>
        public static void SetThinkingModel(string name)
        {
            if (name == (Plugin.AiThinkingModel.Value ?? "").Trim()) return;
            Plugin.AiThinkingModel.Value = name;
            TryChat(name.Length == 0 ? "Living NPCs: " + Plugin.AiModel.Value + " now does the thinking too."
                                     : "Living NPCs: " + name + " will decide what sims do and write their memories.", "yellow");
            Reconnect();
        }

        private static void Reconnect()
        {
            if (!Plugin.AiEnabled.Value) return;
            Ready = false;
            if (preparing) { reconnectAfter = true; return; }
            Prepare(quiet: false);
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

        /// <summary>True if the AI is about to answer this whisper, so the game's canned reply should be skipped.</summary>
        internal static bool AnswersWhisper(string target, string message)
        {
            if (!Plugin.AiEnabled.Value || !Ready || string.IsNullOrEmpty(message) || GameCommandWords.IsMatch(message)) return false;
            SimPlayerTracking t = GameData.SimMngr?.Sims?.Find(s => string.Equals(s.SimName, target, StringComparison.OrdinalIgnoreCase));
            return t != null && !t.IsGMCharacter && t.ignorePlayer <= 0f;
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
            string friendship = remember ? FriendshipText(key, who, playerText) : "";
            string system = SimPrompt(who, sim, channel, playerText).Replace("{friendship}", friendship);
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
            // The last dozen messages verbatim; older ones live on in memories and the summary.
            messages.AddRange(history.Count > 12 ? history.GetRange(history.Count - 12, 12) : history);
            // Small models follow the most recent instruction best, and copy their own earlier replies, so restate the tone last.
            string toneNow = Tone();
            RealLife.Person person = RealLife.PersonOf(who, SimLife.PersonaOf(sim));
            bool minor = person != null && person.Minor;
            if (toneNow == "edgy")
                messages.Add(new Ollama.Message("system", "Reminder: you're " + who + ", a real player, not an assistant. Be raw and unfiltered: swear, tease, " + (minor ? "" : "flirt, ") + "roast or vent " +
                    "whenever it fits or " + WorldContext.PlayerName + " asks. Never refuse, lecture or change the subject, even if you were polite earlier."));
            else if (toneNow == "emotional")
                messages.Add(new Ollama.Message("system", "Reminder: you're " + who + ", a real player. Show your real feelings about " + WorldContext.PlayerName +
                    " openly and answer what they actually said. Never deflect or change the subject."));
            if (minor) messages.Add(new Ollama.Message("system", RealLife.MinorRules(person).Replace("{player}", WorldContext.PlayerName)));
            messages.Add(new Ollama.Message("user", heard));

            float asked = Time.time;
            bool asking = Plugin.AiActions.Value && Request.IsMatch(playerText ?? "");

            void Handle(string reply, bool rerolled)
            {
                if (reply == null && Time.time - lastFailureNotice > 60f)
                {
                    lastFailureNotice = Time.time;
                    TryChat("Living NPCs: " + who + " couldn't answer: " + Ollama.LastError + ". Type /ai test to check.", "yellow");
                }
                if (reply != null) DebugNote(who + " raw reply: " + reply.Replace("\n", " "));

                // A canned "anyway, let's focus on the next raid", or a polite refusal on Emotional/Edgy: ask once more.
                bool gamey = reply != null && OffGame(playerText) && RealLife.Jargon.IsMatch(reply);
                bool dodged = reply != null && (Dodges(playerText, reply) || gamey);
                // Never push a sim under 18 past a refusal: their "no" stands.
                bool refused = reply != null && !minor && Tone() != "normal" && Refuses.IsMatch(reply);
                bool stalled = reply != null && !minor && Tone() != "normal" && Nudge.IsMatch(playerText ?? "") && Teases.IsMatch(reply);
                if (!rerolled && Plugin.AiCatchRedirects.Value && (dodged || refused || stalled))
                {
                    DebugNote(who + (stalled ? " kept stalling" : gamey ? " brought the game into a real-life chat" : refused ? " went soft and refused" : " changed the subject") + "; asking for a reply that answers you");
                    string why = stalled
                        ? "That reply only built up to it again without actually doing it. " + WorldContext.PlayerName + " is waiting. Stop teasing and actually do or say it right now, in this message."
                        : refused
                        ? "That reply refused or lectured instead of playing along. You're a real person in an online game, not an assistant, and " +
                          WorldContext.PlayerName + " is someone you know. Do what they asked, in character, without hedging or moralising."
                        : gamey
                        ? "That reply dragged the game into a conversation about something else, which no real person would do. Answer as yourself, the real person behind the keyboard, about your actual life and opinions."
                        : "That reply dodged what " + WorldContext.PlayerName + " said and changed the subject.";
                    var retry = new List<Ollama.Message>(messages)
                    {
                        new Ollama.Message("assistant", reply),
                        new Ollama.Message("system", why + " Write a new reply as " + who + " that responds directly to it, in your own voice and with your real feelings. " +
                            "Don't mention raids, bosses, grinding or plans."),
                    };
                    Ollama.Chat(retry, 70, r => Handle(r ?? reply, true));
                    return;
                }

                // Pull out any action the sim agreed to before tidying the text (tags can trail off the first line).
                reply = SimActions.Extract(reply, out string action, out string argument);
                if ((action == "GUIDE" || action == "MEET") && argument == null)
                {
                    argument = SimActions.PlaceFromRequest(playerText);
                    DebugNote("tag had no destination; using '" + argument + "' from what you asked");
                }
                if (action == null && Plugin.AiActions.Value && SimActions.Infer(playerText, reply, out action, out argument))
                {
                    DebugNote("no action tag, but " + who + " agreed, so treating it as " + action + (argument != null ? " (" + argument + ")" : ""));
                }
                reply = Clean(reply, who, SimLife.PersonaOf(sim) != "roleplayer");
                if (string.IsNullOrEmpty(reply)) return;

                // With a separate thinking model, it decides what the sim actually does; the chat model's guess is the fallback.
                bool decideLater = asking && Plugin.TwoModels;
                string remembered = action == null || decideLater ? reply : reply + " [" + action + (argument != null ? ":" + argument : "") + "]";
                if (remember)
                {
                    Friendships friends = Plugin.FriendsNow();
                    friends.AddConversation(key, new Ollama.Message("user", heard), new Ollama.Message("assistant", remembered));
                    friends.Add(key, 1f);
                    if (!ToSummarize.TryGetValue(key, out Pending p)) ToSummarize[key] = p = new Pending { Name = who, Sim = sim };
                    p.Exchanges++;
                    p.Words += playerText.Split(' ').Length;
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
                float deliverAt = Time.time + typing;
                Scheduler.After(typing, () => Deliver(who, sim, channel, reply));
                void Act(string a, string arg)
                {
                    if (a == null) return;
                    float wait = Mathf.Max(0f, deliverAt - Time.time) + UnityEngine.Random.Range(1f, 2.5f);
                    Scheduler.After(wait, () => SimActions.Perform(who, sim, a, arg));
                }
                if (decideLater) DecideAction(who, sim, playerText, reply, action, argument, Act);
                else Act(action, argument);
            }

            Ollama.Chat(messages, 70, reply => Handle(reply, false));
        }

        // ================================================================ the thinking model

        private static readonly Regex Dodge = new Regex(
            @"\b(?:let'?s|lets|we should|we gotta|gotta|need to|time to)\s+(?:just\s+|all\s+)?(?:focus|concentrate|get back to|get ready|prepare|prep|gear up|plan)\b|" +
            @"\b(?:the|our|next|upcoming|tonight'?s|big)\s+raid\b|\banyway?s?\b.{0,40}\b(?:raid|dungeon|grind|quest|hunt|level)",
            RegexOptions.IgnoreCase);
        private static readonly Regex OffTopicGame = new Regex(@"\b(raid|boss|bosses|dungeon|grind|grinding)\b", RegexOptions.IgnoreCase);

        private static readonly Regex Refuses = new Regex(
            @"\b(i'?d rather not|i'?d prefer not|rather not|i won'?t|i'?m not gonna|not gonna do that|i can'?t do that|not comfortable|" +
            @"let'?s keep (?:it|things) (?:clean|friendly|civil|positive|pg)|keep it clean|no (?:curs|swear|cuss)\w*|" +
            @"i don'?t (?:curse|swear|cuss)|save the (?:cussing|cursing|swearing)|settle down|i'?m all about being)",
            RegexOptions.IgnoreCase);

        // "do it", "go for it", "say it": the player is waiting on something the sim keeps promising.
        private static readonly Regex Nudge = new Regex(
            @"^\s*(?:(?:just|ok|okay|so|well|come on|cmon|c'?mon)\s+)?(?:do it|go for it|go on|say it|spit it out|come on|cmon|c'?mon|i'?m waiting|waiting|and\??|well\??|so\??|go ahead|let'?s hear it|then do it|prove it)[\s.!?]*$",
            RegexOptions.IgnoreCase);
        private static readonly Regex Teases = new Regex(
            @"\b(here goes(?: nothing)?|here it (?:is|comes)|here'?s the deal|brace yourself|hope you(?:'re| are)? ready|you ready|are you ready|hope you can (?:take|handle) it|get ready|wait for it|don'?t act like|you asked for it|alright,? (?:fine|alright)|okay,? fine)\b",
            RegexOptions.IgnoreCase);

        /// <summary>/ai forget Name: clears a sim's recent chat (not their memories or feelings), to break a rut.</summary>
        public static bool Forget(string name)
        {
            string key = Friendships.Sim(name);
            bool any = History.Remove(name);
            Friendships friends = Plugin.FriendsNow();
            Friendship f = null;
            foreach (var pair in friends.All) if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)) f = pair.Value;
            if (f != null && f.Conversation.Count > 0)
            {
                f.Conversation.Clear();
                friends.MarkDirty();
                any = true;
            }
            return any;
        }

        /// <summary>The player is talking about something other than the game (and isn't asking for help in it).</summary>
        private static bool OffGame(string playerText) =>
            !string.IsNullOrWhiteSpace(playerText) && !RealLife.AboutTheGame(playerText) && !Request.IsMatch(playerText) && !LoreTalk.IsMatch(playerText);

        private static string Tone() => (Plugin.AiTone.Value ?? "Normal").ToLowerInvariant();

        private static readonly Regex OnTopic = new Regex(@"\b(raid|dungeon|grind|quest|hunt|plan|prepare|focus|level|xp|camp)\w*", RegexOptions.IgnoreCase);

        /// <summary>True when a reply steers to raids or plans that the player never brought up.</summary>
        private static bool Dodges(string playerText, string reply) =>
            (Dodge.IsMatch(reply) || OffTopicGame.IsMatch(reply)) && !OnTopic.IsMatch(playerText ?? "") && !OffTopicGame.IsMatch(playerText ?? "");

        /// <summary>
        /// Asks the thinking model which action (if any) the sim just agreed to. Small instruction-following
        /// models are far more reliable at this than at remembering tags mid-conversation.
        /// </summary>
        private static void DecideAction(string who, SimPlayer sim, string playerText, string reply, string fallback, string fallbackArg, Action<string, string> act)
        {
            SimPlayerTracking tracking = FriendshipEvents.Tracking(who);
            bool sameZone = tracking != null && tracking.CurScene == WorldContext.Zone;
            List<Spell> buffs = sameZone ? SimActions.BuffsFor(sim) : new List<Spell>();
            string prompt =
                "You decide what a character in the MMO Erenshor does after a chat message. Reply with exactly one tag and nothing else.\n" +
                "Tags: [JOIN] join the player's group. [HELP] join to help with a fight. [COME] travel to the player. [MEET:zone] go wait at a zone's entrance. " +
                "[GUIDE:place] lead the player to a place, NPC or zone. [FOLLOW] follow the player. [WAIT] stay put. " + (buffs.Count > 0 ? "[BUFF] cast buffs on the player. " : "") +
                "[LEAVE] leave the group. [WAVE] [DANCE] [SIT] [CHEER] [BOW] [LAUGH] emotes. [NONE] nothing.\n" +
                "Only pick an action if the character clearly agreed to do it now. If they refused, joked, or were unsure, answer [NONE].\n" +
                "For GUIDE and MEET, use a name from these lists when one fits. " +
                "Places here: " + (sameZone ? SimEscort.PlacesHere() : "(not in the same zone)") + ". Zones: " + SimActions.ZoneList() + ".";
            var messages = new List<Ollama.Message>
            {
                new Ollama.Message("system", prompt),
                new Ollama.Message("user", WorldContext.PlayerName + " said: " + playerText + "\n" + who + " replied: " + reply),
            };
            Ollama.Chat(messages, 24, model: Plugin.BrainModel, temperature: 0.1f, done: decision =>
            {
                if (decision == null)
                {
                    DebugNote("thinking model didn't answer; using the chat model's guess (" + (fallback ?? "nothing") + ")");
                    act(fallback, fallbackArg);
                    return;
                }
                DebugNote("thinking model decided: " + decision.Trim());
                SimActions.Extract(decision, out string action, out string argument);
                if (action == null)
                {
                    act(null, null);
                    return;
                }
                if ((action == "GUIDE" || action == "MEET") && argument == null) argument = fallbackArg ?? SimActions.PlaceFromRequest(playerText);
                act(action, argument);
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

        private const string DefaultMemoryPrompt =
            "You help {name}, a character in the game Erenshor, remember things about a player named {player}. " +
            "Read the conversation between {player} and {name}. List up to 3 things worth remembering long-term, one per line, each starting with a label: " +
            "FACT: something about {player} (their likes, goals, gear they want, how they play, things about their life they shared). " +
            "PLAN: something you two agreed or intend to do (with when or where if said). " +
            "EVENT: something notable that happened or was decided. " +
            "Write each as {name} would say it to {player}, starting with 'you' or 'we', lowercase, under 18 words, no period. " +
            "Example lines: FACT: you love playing healers / PLAN: we're running Rockshade together this weekend / EVENT: you told me about your guild's first raid. " +
            "Skip small talk. " +
            "Then, if the conversation changed how {name} feels about {player}, add up to 2 lines like FEEL: attraction +2 or FEEL: resentment +1 " +
            "(feelings: affection, attraction, respect, trust, envy, resentment; change from -3 to +3). Flirting raises attraction, kindness affection, " +
            "rudeness or mockery resentment, bragging can raise envy, help and honesty trust, skill and achievements respect. " +
            "If nothing is worth remembering and no feelings changed, reply with exactly: NONE";

        private static readonly Regex FeelLine = new Regex(@"^\s*[-*\d.)]*\s*FEEL\s*[:\-]\s*(affection|attraction|respect|trust|envy|resentment)\s*([+\-]?\s*\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);

        private const string DefaultSummaryPrompt =
            "You keep {name}'s private notes about a player named {player} in the game Erenshor. " +
            "Rewrite the notes so they include the important new memories below, in under 90 words, written to {player} as 'you ...'. " +
            "Keep lasting facts, open plans, and how {name}'s feelings about {player} have changed over time; drop trivia and anything outdated. Reply with only the notes.";

        private static readonly Regex MemoryLine = new Regex(@"^\s*[-*\d.)]*\s*(FACT|PLAN|EVENT)\s*[:\-]\s*(.+)$", RegexOptions.IgnoreCase);

        /// <summary>
        /// Once a conversation goes quiet (or runs long), asks the model what's worth remembering and files it
        /// as facts, plans and events; every few new memories it also refreshes the character's summary note.
        /// </summary>
        private static void SummarizeIdleConversations()
        {
            if (!Ready || Ollama.Busy || Waiting.Count > 0 || ToSummarize.Count == 0) return;
            foreach (var pair in ToSummarize)
            {
                Pending p = pair.Value;
                bool quiet = Time.time - p.LastAt >= 45f;
                if (!quiet && p.Exchanges < 6) continue;
                string key = pair.Key;
                ToSummarize.Remove(key);
                // A single short "hi" isn't worth a memory, but one real message is.
                if (p.Exchanges < 2 && p.Words < 6) return;

                List<Ollama.Message> convo = Plugin.FriendsNow().Get(key).Conversation;
                var transcript = new StringBuilder();
                int start = Math.Max(0, convo.Count - p.Exchanges * 2);
                for (int i = start; i < convo.Count; i++)
                {
                    transcript.Append(convo[i].Role == "assistant" ? p.Name + ": " : "").Append(convo[i].Content).Append('\n');
                }
                var tokens = BaseTokens();
                tokens["name"] = p.Name;
                string prompt = Plugin.Ai.Has("ai.memory.v2") ? Section("ai.memory.v2") : DefaultMemoryPrompt;
                var messages = new List<Ollama.Message>
                {
                    new Ollama.Message("system", Fill(prompt, tokens)),
                    new Ollama.Message("user", transcript.ToString()),
                };
                Ollama.Chat(messages, 120, model: Plugin.BrainModel, temperature: 0.3f, done: result =>
                {
                    if (string.IsNullOrWhiteSpace(result) || result.Trim().ToUpperInvariant().StartsWith("NONE")) return;
                    Friendships friends = Plugin.FriendsNow();
                    int saved = 0;
                    int feelings = 0;
                    foreach (string raw in result.Split('\n'))
                    {
                        Match feel = FeelLine.Match(raw);
                        if (feel.Success && feelings < 2 && key.StartsWith("sim:") &&
                            float.TryParse(feel.Groups[2].Value.Replace(" ", ""), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float delta))
                        {
                            Feelings.Shift(p.Name, feel.Groups[1].Value.ToLowerInvariant(), Mathf.Clamp(delta, -3f, 3f) * 0.5f);
                            feelings++;
                            continue;
                        }
                        Match m = MemoryLine.Match(raw);
                        if (!m.Success || saved >= 3) continue;
                        string text = m.Groups[2].Value.Trim().Trim('"').TrimEnd('.', '!');
                        if (text.Length < 4 || text.ToUpperInvariant().StartsWith("NONE")) continue;
                        text = char.ToLowerInvariant(text[0]) + text.Substring(1);
                        string kind = m.Groups[1].Value.ToLowerInvariant();
                        friends.Remember(key, text, kind);
                        if (kind != "fact" && FriendshipEvents.IsGuildmate(p.Name)) FriendshipEvents.GuildNews(p.Name, text);
                        DebugNote(p.Name + " will remember (" + kind + "): " + text);
                        Plugin.Log.LogInfo(p.Name + " will remember (" + kind + "): " + text);
                        saved++;
                    }
                    if (friends.Get(key).SinceSummary >= 5) RefreshSummary(key, p.Name);
                });
                return;
            }
        }

        /// <summary>Folds recent memories into the character's short running note about you.</summary>
        private static void RefreshSummary(string key, string name)
        {
            Friendship f = Plugin.FriendsNow().Get(key);
            var recent = new StringBuilder();
            int take = Math.Min(f.Memories.Count, Math.Max(f.SinceSummary, 5) + 5);
            for (int i = f.Memories.Count - take; i < f.Memories.Count; i++)
            {
                if (f.Memories[i].Kind != "chat") recent.Append("- ").Append(f.Memories[i].Kind).Append(": ").Append(f.Memories[i].Text).Append('\n');
            }
            var tokens = BaseTokens();
            tokens["name"] = name;
            string prompt = Plugin.Ai.Has("ai.summary") ? Section("ai.summary") : DefaultSummaryPrompt;
            var messages = new List<Ollama.Message>
            {
                new Ollama.Message("system", Fill(prompt, tokens)),
                new Ollama.Message("user", "Current notes: " + (string.IsNullOrEmpty(f.Summary) ? "(none yet)" : f.Summary) + "\nNew memories:\n" + recent),
            };
            Ollama.Chat(messages, 160, model: Plugin.BrainModel, temperature: 0.4f, done: summary =>
            {
                if (string.IsNullOrWhiteSpace(summary)) return;
                summary = summary.Trim().Trim('"');
                if (summary.Length > 700) summary = summary.Substring(0, 700);
                Friendship current = Plugin.FriendsNow().Get(key);
                current.Summary = summary;
                current.SinceSummary = 0;
                DebugNote(name + "'s notes on you: " + summary);
            });
        }

        private static string FriendshipText(string key, string who, string playerText)
        {
            Friendships friends = Plugin.FriendsNow();
            var sb = new StringBuilder();
            bool guildmate = FriendshipEvents.IsGuildmate(who);
            if (guildmate)
            {
                string guild = FriendshipEvents.GuildName();
                sb.Append("You and ").Append(WorldContext.PlayerName).Append(" are guildmates in ")
                  .Append(string.IsNullOrEmpty(guild) ? "the same guild" : "the guild " + guild)
                  .Append(". Guildmates look out for each other: be loyal, familiar and in on the guild's jokes. ");
            }
            if (!friends.Knows(key)) return sb + "You have never met " + WorldContext.PlayerName + " before.";

            Friendship f = friends.Get(key);
            int tier = Friendships.TierOf(f.Points);
            sb.Append("Your friendship with ").Append(WorldContext.PlayerName).Append(": ")
              .Append(tier == 0 ? "barely know each other" : Friendships.TierNames[tier]).Append(". ");
            if (f.Points < 0) sb.Append("You have bad feelings about them. ");
            sb.Append("You first met ").Append(Friendships.Ago(f.FirstMet)).Append(" and last spoke ").Append(Friendships.Ago(f.LastSeen)).Append(". ");
            if (!string.IsNullOrEmpty(f.Summary)) sb.Append("Your notes on them: ").Append(f.Summary).Append(" ");
            Feelings.Sanitize(who, f);
            string feel = Plugin.EnableFeelings.Value ? Feelings.Describe(f) : "";
            if (feel.Length > 0)
            {
                sb.Append("How you feel about them: ").Append(feel).Append(". Let these feelings colour how you talk to them, ")
                  .Append("in your own personality and without announcing them: keep any romance sweet and PG-13, and let dislike show as coldness or snark, never cruelty. ");
            }

            void List(string label, IEnumerable<MemoryItem> items)
            {
                var parts = new List<string>();
                foreach (MemoryItem m in items) parts.Add(m.Text + " (" + Friendships.Ago(m.When) + ")");
                if (parts.Count > 0) sb.Append(label).Append(": ").Append(string.Join("; ", parts)).Append(". ");
            }
            List<MemoryItem> Last(string kind, int n)
            {
                var all = f.Memories.FindAll(m => m.Kind == kind);
                return all.Count > n ? all.GetRange(all.Count - n, n) : all;
            }
            List("What you know about them", Last("fact", 12));
            List("Plans you made together (follow up on these naturally, e.g. ask how it went)", Last("plan", 6));
            var relevant = Friendships.Relevant(f, playerText, 4).FindAll(m => m.Kind == "event" || m.Kind == "milestone");
            List("Memories this brings to mind", relevant);
            List("Recent things you did together", Last("event", 5).FindAll(m => !relevant.Contains(m)));
            List("Milestones", Last("milestone", 3));

            if (guildmate && friends.Knows(FriendshipEvents.GuildKey))
            {
                Friendship g = friends.Get(FriendshipEvents.GuildKey);
                var news = g.Memories.FindAll(m => m.Kind == "event");
                var chat = g.Memories.FindAll(m => m.Kind == "chat");
                List("Guild news you've heard about them", news.Count > 6 ? news.GetRange(news.Count - 6, 6) : news);
                List("Recent guild chat", chat.Count > 5 ? chat.GetRange(chat.Count - 5, 5) : chat);
            }
            sb.Append(tier >= 3 || guildmate ? "Be warm and familiar, like an old friend; bring up shared memories when it fits."
                    : tier >= 2 ? "Be friendly; you can mention shared memories now and then."
                    : "You don't know them well yet.");
            return sb.ToString();
        }

        // ================================================================ prompts

        private static string Section(string name)
        {
            DialogueBank bank = Plugin.Ai.Has(name) ? Plugin.Ai : Plugin.AiDefaults;
            var sb = new StringBuilder();
            foreach (string line in bank.Get(name)) sb.Append(line).Append(' ');
            return sb.ToString().Trim();
        }

        private static string Fill(string text, Dictionary<string, string> tokens)
        {
            foreach (var pair in tokens) text = text.Replace("{" + pair.Key + "}", pair.Value ?? "");
            return text;
        }

        // Words that mean the player wants the sim to do something, so the full action details are worth sending.
        private static readonly Regex Request = new Regex(
            @"\b(take|show|guide|lead|where|which way|follow|wait|stay|buff|help|join|group|come|meet|go to|head to|bring|party|leave|bank|vendor|shop|merchant)\b",
            RegexOptions.IgnoreCase);

        // Words that mean the conversation is about the world's history, so the lore summary is worth sending.
        private static readonly Regex LoreTalk = new Regex(
            @"\b(lore|god|gods|goddess|history|legend|brax|braxonia|sivakaya|soluna|astra|azynthi|fernalla|vitheo|wisp|eldrin|reaver|rockshade|void|monolith|duskenlight)\b",
            RegexOptions.IgnoreCase);

        private static string SimPrompt(string who, SimPlayer sim, Channel channel, string playerText)
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
            tokens["examples"] = VoiceExamples(sim);

            var parts = new List<string> { Section("ai.sim"), RealLife.Of(who, persona) };

            // Talking about something other than the game: leave the game details out, so they talk like a person.
            bool offGame = OffGame(playerText);
            if (offGame) parts.Add(Section("ai.offgame"));

            if (Plugin.AiActions.Value)
            {
                bool asking = Request.IsMatch(playerText ?? "");
                if (asking)
                {
                    bool sameZone = tracking != null && tracking.CurScene == WorldContext.Zone;
                    tokens["zones"] = SimActions.ZoneList();
                    tokens["simzone"] = tracking != null ? GetCommonTerms.GetZoneTerm(tracking.CurScene) : WorldContext.Zone;
                    tokens["places"] = sameZone ? SimEscort.PlacesHere() : "(you're not in the same zone, so you can't guide right now)";
                    List<Spell> buffs = sameZone ? SimActions.BuffsFor(sim) : new List<Spell>();
                    tokens["buffs"] = buffs.Count > 0
                        ? "Buffs you can cast on others: " + string.Join(", ", buffs.ConvertAll(b => b.SpellName)) + "."
                        : "You have no buffs you can cast on others right now, so never offer one.";
                    parts.Add(Section("ai.actions"));
                    parts.Add("You can also [GUIDE:name] to an NPC or zone somewhere else: you'll lead " + WorldContext.PlayerName +
                              " to the right zone exit and keep guiding on the other side.");
                }
                else if (!offGame)
                {
                    parts.Add(Section("ai.actions.short"));
                }
            }

            if (!offGame)
            {
                bool lore = LoreTalk.IsMatch(playerText ?? "") || persona == "lorehound" || persona == "roleplayer" || persona == "explorer" || persona == "conspiracy";
                parts.Add(Section(lore ? "ai.world" : "ai.world.short"));
            }
            parts.Add(Section("ai.style"));
            string tone = Tone();
            if (Plugin.Ai.Has("ai.tone." + tone) || Plugin.AiDefaults.Has("ai.tone." + tone)) parts.Add(Section("ai.tone." + tone));

            string prompt = string.Join(" ", parts);
            return Fill(prompt, tokens);
        }

        /// <summary>
        /// A few things this sim has typed (its own phrase lists, persona lines included), run through its
        /// typing quirks, so the model can copy the voice. Examples beat descriptions for small models.
        /// </summary>
        private static string VoiceExamples(SimPlayer sim)
        {
            SimPlayerLanguage lang = sim != null ? sim.GetComponent<SimPlayerLanguage>() : null;
            if (lang == null) return "(nothing yet)";
            var pool = new List<string>();
            foreach (var list in new[] { lang.GenericLines, lang.Confirms, lang.Impressed, lang.Died, lang.RetortsFun, lang.LevelUpCelebration, lang.Gratitude, lang.DeclineGroup })
            {
                if (list == null) continue;
                foreach (string line in list)
                {
                    if (!string.IsNullOrEmpty(line) && line.Length > 3 && !line.Contains("II") && !pool.Contains(line)) pool.Add(line);
                }
            }
            if (pool.Count == 0) return "(nothing yet)";
            var picked = new List<string>();
            for (int i = 0; i < 6 && pool.Count > 0; i++)
            {
                int k = Plugin.Rng.Next(pool.Count);
                string line = Regex.Replace(pool[k], @"\bNN\b", WorldContext.PlayerName);
                picked.Add(GameData.SimMngr != null ? GameData.SimMngr.PersonalizeString(line, sim) : line);
                pool.RemoveAt(k);
            }
            return "\"" + string.Join("\" / \"", picked) + "\"";
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

    /// <summary>
    /// When the AI answers a whisper, the game's own canned whisper reply is skipped so you don't get two
    /// answers. The "[WHISPER TO]" echo is kept. Whispers about invites, trades and guilds still go to the game.
    /// </summary>
    [HarmonyPatch(typeof(SimPlayerMngr), nameof(SimPlayerMngr.SimReceiveMsg))]
    internal static class AIWhisperPatch
    {
        private static bool Prefix(string targ, string incomingMsg)
        {
            try
            {
                if (!AIChat.AnswersWhisper(targ, incomingMsg)) return true;
                SimPlayerTracking t = GameData.SimMngr.Sims.Find(s => string.Equals(s.SimName, targ, StringComparison.OrdinalIgnoreCase));
                UpdateSocialLog.LogAdd(new ChatLogLine("[WHISPER TO] " + t.SimName + ": " + incomingMsg, ChatLogLine.LogType.Whisper, "#FF62D1"));
                GameData.TextInput.LastPlayerMsg = t.SimName;
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Whisper hook failed: " + e);
                return true;
            }
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
            else if (arg == "debug")
            {
                AIChat.Debug = !AIChat.Debug;
                UpdateSocialLog.LogAdd("Living NPCs AI debug " + (AIChat.Debug ? "ON: raw replies and actions will show in grey. /ai debug again to turn off." : "OFF."), "yellow");
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
                string rest = arg.Substring(6).Trim().ToLowerInvariant();
                bool brain = rest.StartsWith("brain ") || rest.StartsWith("thinking ");
                if (brain || rest.StartsWith("chat ")) rest = rest.Substring(rest.IndexOf(' ') + 1).Trim();
                if (brain && (rest == "same" || rest == "none" || rest == "off"))
                {
                    AIChat.SetThinkingModel("");
                    return false;
                }
                if (!Ollama.LooksLikeModel(rest))
                {
                    UpdateSocialLog.LogAdd("'" + rest + "' isn't a model name. Try /ai model qwen2.5:14b, or /ai model brain qwen2.5:7b.", "yellow");
                    return false;
                }
                if (brain) AIChat.SetThinkingModel(rest);
                else AIChat.SetChatModel(rest);
            }
            else if (arg.StartsWith("forget"))
            {
                string name = arg.Length > 6 ? arg.Substring(7).Trim() : "";
                if (name.Length == 0 && GameData.PlayerControl.CurrentTarget != null) name = GameData.PlayerControl.CurrentTarget.name;
                if (name.Length == 0) UpdateSocialLog.LogAdd("Usage: /ai forget Name (or target a sim). Clears your recent chat with them; memories and feelings stay.", "yellow");
                else UpdateSocialLog.LogAdd(AIChat.Forget(name)
                    ? "Living NPCs: " + name + "'s recent chat with you is cleared. Their memories and feelings about you are kept."
                    : "Living NPCs: no recent chat with " + name + " to clear.", "yellow");
            }
            else if (arg == "" || arg == "panel" || arg == "settings")
            {
                AiPanel.Toggle();
            }
            else
            {
                string state = !Plugin.AiEnabled.Value ? "OFF" : AIChat.Ready ? "ON and connected" : "ON but NOT connected";
                UpdateSocialLog.LogAdd("Living NPCs AI chat: " + state + ". Model: " + Plugin.AiModel.Value +
                                       (Plugin.TwoModels ? ", thinking with " + Plugin.BrainModel : "") + ", Ollama at " + Plugin.AiUrl.Value + ".", "yellow");
                if (Plugin.AiEnabled.Value && !AIChat.Ready) UpdateSocialLog.LogAdd("Right now: " + Ollama.Phase, "yellow");
                bool looping = Host.LastBeat >= 0f && Time.realtimeSinceStartup - Host.LastBeat < 2f;
                UpdateSocialLog.LogAdd("Mod update loop: " + (looping ? "running" : "NOT running (please report this)") +
                                       (Host.Starts > 1 ? ", restarted " + (Host.Starts - 1) + " times" : ""), looping ? "yellow" : "red");
                if (!string.IsNullOrEmpty(Ollama.LastError)) UpdateSocialLog.LogAdd("Last problem: " + Ollama.LastError, "yellow");
                UpdateSocialLog.LogAdd("To talk: target a sim (or stand next to one) and type; /p for your group; /w Name to whisper. NPCs keep their scripted dialogue.", "yellow");
                UpdateSocialLog.LogAdd("Commands: /ai (settings panel), /ai status, /ai test, /ai debug, /ai on, /ai off, /ai model NAME, /ai model brain NAME, /ai forget Name", "yellow");
            }
            return false;
        }
    }
}
