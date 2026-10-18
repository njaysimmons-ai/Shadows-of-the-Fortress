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
        // Lenient on purpose: models write [GUIDE:bank], [GUIDE: the bank], [Guide to the bank], [GUIDE - bank], (GUIDE:bank)...
        private static readonly Regex Tag = new Regex(
            @"[\[(]\s*(JOIN|LEAVE|COME|MEET|GUIDE|LEAD|FOLLOW|WAIT|STAY|BUFF|HELP|WAVE|DANCE|SIT|CHEER|BOW|LAUGH)\b\s*(?:[:=\-]|\bto\b|\bat\b|\bin\b)?\s*(?:to\s+|at\s+|in\s+)?([^\])]*)[\])]",
            RegexOptions.IgnoreCase);
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

        // Multi-zone guiding: who is leading you where, continued each time you change zone together.
        private class Route
        {
            public string Sim;
            public string FinalZone;
            public string Npc;      // null when the destination is the zone itself
            public float Until;
        }
        private static readonly List<Route> Routes = new List<Route>();

        // Sims who joined just to help with a fight, and when they'll give up waiting for one.
        private static readonly Dictionary<string, float> Helpers = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
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
                if (action == "LEAD") action = "GUIDE";
                argument = m.Groups[2].Success ? m.Groups[2].Value.Trim().Trim('"', '\'', '.') : null;
                if (string.IsNullOrEmpty(argument)) argument = null;
                reply = reply.Remove(m.Index, m.Length);
            }
            return AnyTag.Replace(reply, "").Replace("  ", " ").Trim();
        }

        // ---------------------------------------------------------------- reading intent when there's no tag

        private static readonly Regex Agrees = new Regex(
            @"\b(sure|yes|yeah|yep|yup|ya|ok|okay|k|follow me|this way|come on|let'?s go|lets go|omw|on my way|got it|of course|np|no problem|aye|right this way|gladly|happy to|alright|you got it)\b",
            RegexOptions.IgnoreCase);
        private static readonly Regex Refuses = new Regex(@"\b(no|nah|nope|can'?t|cannot|sorry|busy|later|not now|don'?t know|no idea)\b", RegexOptions.IgnoreCase);
        private static readonly Regex AskGuide = new Regex(
            @"\b(?:take|show|lead|guide|bring|walk|get)\s+me\s+(?:to|towards|over to|the way to)\s+(?<place>[^?.!,]+)|\bwhere(?:'s|\s+is|\s+are|\s+can\s+i\s+find)\s+(?<place>[^?.!,]+)|\bshow\s+me\s+(?:where\s+)?(?<place>(?:the\s+)?[^?.!,]+?)\s+is\b",
            RegexOptions.IgnoreCase);
        private static readonly Regex AskFollow = new Regex(@"\b(follow me|come with me|tag along)\b", RegexOptions.IgnoreCase);
        private static readonly Regex AskWait = new Regex(@"\b(wait here|stay here|wait for me|stay put|hold on here)\b", RegexOptions.IgnoreCase);
        private static readonly Regex AskBuff = new Regex(@"\b(buff|buffs)\b", RegexOptions.IgnoreCase);
        private static readonly Regex AskHelp = new Regex(@"\b(help me (?:with|kill|fight)|help with this|give me a hand)\b", RegexOptions.IgnoreCase);

        /// <summary>The place in a request like "take me to the bank" or "where is Prestigio?", or null.</summary>
        public static string PlaceFromRequest(string playerText)
        {
            Match g = AskGuide.Match(playerText ?? "");
            return g.Success ? g.Groups["place"].Value.Trim() : null;
        }

        /// <summary>
        /// If the sim clearly agreed to what you asked but forgot the tag, work out the action from the two
        /// messages. Only fires on a plain yes, never on a refusal.
        /// </summary>
        public static bool Infer(string playerText, string reply, out string action, out string argument)
        {
            action = argument = null;
            if (string.IsNullOrEmpty(playerText) || string.IsNullOrEmpty(reply)) return false;
            if (!Agrees.IsMatch(reply) || Refuses.IsMatch(reply)) return false;
            Match g = AskGuide.Match(playerText);
            if (g.Success) { action = "GUIDE"; argument = g.Groups["place"].Value.Trim(); }
            else if (AskFollow.IsMatch(playerText)) action = "FOLLOW";
            else if (AskWait.IsMatch(playerText)) action = "WAIT";
            else if (AskBuff.IsMatch(playerText)) action = "BUFF";
            else if (AskHelp.IsMatch(playerText)) action = "HELP";
            return action != null;
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
            AIChat.DebugNote(simName + " will " + action + (argument != null ? " (" + argument + ")" : "") +
                             (sim == null ? ", but isn't spawned near you" : tracking.CurScene != WorldContext.Zone ? ", from " + tracking.CurScene : ""));
            try
            {
                bool here = sim != null && tracking.CurScene == WorldContext.Zone && SimLife.Alive(sim);
                switch (action)
                {
                    case "JOIN":
                    case "COME":
                        if (here) { SimEscort.Stop(sim); Join(sim); }
                        else ComeToPlayer(tracking);
                        break;
                    case "HELP":
                        if (here) { SimEscort.Stop(sim); Join(sim); }
                        else ComeToPlayer(tracking);
                        Helpers[tracking.SimName] = Time.time + 15f * 60f;
                        break;
                    case "LEAVE":
                        if (sim != null) SimEscort.Stop(sim);
                        Leave(sim);
                        break;
                    case "GUIDE":
                        if (!here) { AIChat.DebugNote("can't guide: " + simName + " isn't in your zone"); break; }
                        Routes.RemoveAll(r => string.Equals(r.Sim, sim.name, StringComparison.OrdinalIgnoreCase));
                        if (Plugin.GuideStyle.Value == "Mist" && SimEscort.FindPlace(argument, out Vector3 spot, out string spotName))
                        {
                            // Mist style: the sim points the way with a trail of mist instead of walking you there.
                            AIChat.DebugNote("conjuring a mist trail to '" + spotName + "'");
                            Speech.Face(sim, spot);
                            Speech.Emote(sim.name, "gestures, and a trail of mist drifts toward " + spotName + ".");
                            MistTrail.BeginAt(spot, spotName, quiet: true);
                            break;
                        }
                        if (SimEscort.Guide(sim, argument)) { AIChat.DebugNote("guiding to '" + argument + "' in this zone"); break; }
                        if (!Plugin.GuideAcrossZones.Value)
                        {
                            AIChat.DebugNote("couldn't find '" + argument + "' here, and guiding across zones is off");
                            Scheduler.After(1.5f, () => Speech.Say(sim, "hm, i don't think that's around here"));
                            break;
                        }
                        if (GuideElsewhere(sim, argument)) AIChat.DebugNote("guiding toward '" + argument + "' in another zone");
                        else
                        {
                            AIChat.DebugNote("couldn't find '" + argument + "' here or in any zone the mod has seen");
                            string lost = Lines.Pick("action.guide.unknown", SimLife.PersonaOf(sim)) ?? "hm, actually i'm not sure where that is";
                            Scheduler.After(1.5f, () => Speech.Say(sim, lost));
                        }
                        break;
                    case "FOLLOW":
                        if (here) SimEscort.Follow(sim);
                        else ComeToPlayer(tracking);
                        break;
                    case "WAIT":
                    case "STAY":
                        if (here) SimEscort.Wait(sim);
                        break;
                    case "BUFF":
                        if (here) Buff(sim);
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

        /// <summary>
        /// The place isn't in this zone: work out which zone it's in (a zone name, or an NPC the mod has seen
        /// on your travels), join up so you cross zones together, and lead you to the right exit.
        /// </summary>
        private static bool GuideElsewhere(SimPlayer sim, string target)
        {
            string here = WorldContext.Zone;
            string zone = FindZone(target);
            string npc = null;
            if (zone == null && WorldIndex.FindNpc(target, out string npcName, out string npcZone))
            {
                zone = npcZone;
                npc = npcName;
            }
            if (zone == null || string.Equals(zone, here, StringComparison.OrdinalIgnoreCase)) return false;

            var route = new Route { Sim = sim.name, FinalZone = zone, Npc = npc, Until = Time.time + 25f * 60f };
            if (!LeadToward(sim, route)) return false;
            Routes.Add(route);
            // Groupmates come through zone lines with you, so the sim can keep guiding on the other side.
            if (!sim.InGroup) Join(sim);
            return true;
        }

        /// <summary>Walks you to the exit for the next zone on the route. Returns false if there's no known way.</summary>
        private static bool LeadToward(SimPlayer sim, Route route)
        {
            string hop = WorldIndex.NextHop(WorldContext.Zone, route.FinalZone);
            if (hop == null) return false;
            string finalName = route.Npc ?? GetCommonTerms.GetZoneTerm(route.FinalZone);
            bool lastHop = string.Equals(hop, route.FinalZone, StringComparison.OrdinalIgnoreCase);
            return SimEscort.GuideToExit(sim, hop, s =>
            {
                Speech.Emote(s.name, "points to the way to " + GetCommonTerms.GetZoneTerm(hop) + ".");
                string section = lastHop && route.Npc == null ? "action.guide.zoneexit.final" : "action.guide.zoneexit";
                string line = Lines.Fill(Lines.Pick(section, SimLife.PersonaOf(s)) ?? "through here to {next}, then we'll find {final}",
                    new Dictionary<string, string>
                    {
                        ["next"] = GetCommonTerms.GetZoneTerm(hop),
                        ["final"] = finalName,
                        ["player"] = WorldContext.PlayerName,
                    });
                Scheduler.After(1.2f, () => Speech.Say(s, line));
            });
        }

        /// <summary>Called by SimLife when you arrive in a new zone: carry on any routes in progress.</summary>
        public static void OnZoneChanged()
        {
            WorldIndex.Scan();
            for (int i = Routes.Count - 1; i >= 0; i--)
            {
                Route route = Routes[i];
                if (Time.time > route.Until) { Routes.RemoveAt(i); continue; }
                ContinueRoute(route, 0);
            }
        }

        /// <summary>Groupmates take a moment to appear after a zone change, so retry for a while.</summary>
        private static void ContinueRoute(Route route, int attempt)
        {
            SimPlayerTracking t = FriendshipEvents.Tracking(route.Sim);
            SimPlayer sim = t?.MyAvatar;
            if (sim == null || t.CurScene != WorldContext.Zone || !SimLife.Alive(sim))
            {
                if (attempt < 15) Scheduler.After(2f, () => ContinueRoute(route, attempt + 1));
                else Routes.Remove(route);
                return;
            }
            string persona = SimLife.PersonaOf(sim);
            if (string.Equals(WorldContext.Zone, route.FinalZone, StringComparison.OrdinalIgnoreCase))
            {
                Routes.Remove(route);
                if (route.Npc != null && SimEscort.Guide(sim, route.Npc))
                {
                    string go = Lines.Fill(Lines.Pick("action.guide.continue", persona) ?? "this way, {final}'s not far",
                        new Dictionary<string, string> { ["final"] = route.Npc, ["player"] = WorldContext.PlayerName });
                    Scheduler.After(2f, () => Speech.Party(sim, go));
                }
                else
                {
                    string made = Lines.Fill(Lines.Pick("action.guide.zonearrived", persona) ?? "made it, welcome to {final}!",
                        new Dictionary<string, string> { ["final"] = GetCommonTerms.GetZoneTerm(route.FinalZone), ["player"] = WorldContext.PlayerName });
                    Scheduler.After(2f, () => Speech.Party(sim, made));
                }
                return;
            }
            if (!LeadToward(sim, route))
            {
                Routes.Remove(route);
                string lost = Lines.Pick("action.guide.unknown", persona) ?? "hm, i lost the way from here, sorry";
                Scheduler.After(2f, () => Speech.Party(sim, lost));
                return;
            }
            string onward = Lines.Fill(Lines.Pick("action.guide.continue", persona) ?? "keep following me, {final}'s this way",
                new Dictionary<string, string> { ["final"] = route.Npc ?? GetCommonTerms.GetZoneTerm(route.FinalZone), ["player"] = WorldContext.PlayerName });
            Scheduler.After(2f, () => Speech.Party(sim, onward));
        }

        private static void Leave(SimPlayer sim)
        {
            if (sim == null || !sim.InGroup) return;
            Character c = sim.GetComponent<Character>();
            if (c != null) GameData.SimPlayerGrouping.ForceDismissFromGroup(c);
        }

        /// <summary>The buffs this sim can cast on someone else, by name, for the AI prompt.</summary>
        public static List<Spell> BuffsFor(SimPlayer sim)
        {
            var list = new List<Spell>();
            NPC npc = sim != null ? sim.GetComponent<NPC>() : null;
            if (npc?.MyBuffSpells == null) return list;
            foreach (Spell s in npc.MyBuffSpells)
            {
                if (s != null && !s.SelfOnly && s.Type == Spell.SpellType.Beneficial && !s.GrantInvisibility && !list.Contains(s)) list.Add(s);
            }
            return list;
        }

        /// <summary>Walks into range if needed, then casts up to two of its buffs on you.</summary>
        private static void Buff(SimPlayer sim)
        {
            List<Spell> buffs = BuffsFor(sim);
            string persona = SimLife.PersonaOf(sim);
            if (buffs.Count == 0)
            {
                string none = Lines.Pick("action.buff.none", persona) ?? "oh, i don't actually have any buffs i can give, sorry";
                Scheduler.After(1.5f, () => Speech.Say(sim, none));
                return;
            }
            CastSpell caster = sim.GetComponent<CastSpell>();
            if (caster == null) return;
            bool walked = false;
            int cast = 0;
            float giveUp = Time.time + 30f;

            void Try()
            {
                if (sim == null || !SimLife.Alive(sim) || cast >= Math.Min(2, buffs.Count) || Time.time > giveUp)
                {
                    if (walked && sim != null) SimEscort.Stop(sim);
                    return;
                }
                if (caster.isCasting()) { Scheduler.After(0.5f, Try); return; }
                Spell spell = buffs[cast];
                float range = Math.Max(3f, spell.SpellRange - 1f);
                if (Vector3.Distance(sim.transform.position, SimLife.PlayerPos) > range)
                {
                    if (!walked) { walked = true; SimEscort.Follow(sim); }
                    Scheduler.After(0.5f, Try);
                    return;
                }
                Speech.Face(sim, SimLife.PlayerPos);
                if (caster.StartSpell(spell, GameData.PlayerStats))
                {
                    cast++;
                    Scheduler.After(1f, Try);
                }
                else
                {
                    if (walked) SimEscort.Stop(sim);
                    if (cast == 0)
                    {
                        string oom = Lines.Pick("action.buff.oom", persona) ?? "oom, gimme a sec";
                        Speech.Say(sim, oom);
                    }
                }
            }
            Scheduler.After(1f, Try);
        }

        /// <summary>Called by SimLife when a fight ends: helpers who came for one fight head off.</summary>
        public static void OnFightWon(List<SimPlayer> group)
        {
            foreach (SimPlayer sim in group)
            {
                if (sim == null || !Helpers.ContainsKey(sim.name)) continue;
                Helpers.Remove(sim.name);
                string bye = Lines.Fill(Lines.Pick("action.help.done", SimLife.PersonaOf(sim)) ?? "glad i could help, {player}! catch you later",
                    new Dictionary<string, string> { ["player"] = WorldContext.PlayerName });
                SimPlayer s = sim;
                Scheduler.After(UnityEngine.Random.Range(5f, 9f), () =>
                {
                    if (s == null || GameData.InCombat) return;
                    Speech.Party(s, bye);
                    Scheduler.After(2f, () => Leave(s));
                });
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

            // Helpers who never got their fight drift off after a while.
            var expired = new List<string>();
            foreach (var pair in Helpers) if (Time.time > pair.Value) expired.Add(pair.Key);
            foreach (string k in expired)
            {
                Helpers.Remove(k);
                SimPlayerTracking t = FriendshipEvents.Tracking(k);
                if (t?.MyAvatar != null && t.MyAvatar.InGroup) Leave(t.MyAvatar);
            }
            SimEscort.ForgetDestroyed();
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
