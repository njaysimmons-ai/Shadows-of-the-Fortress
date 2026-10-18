using System;
using System.Collections.Generic;
using UnityEngine;

namespace LivingNPCs
{
    /// <summary>
    /// Makes sim players act like people sharing the world with you:
    /// idle actions with animations, reactions to deaths, wipes and level-ups, group banter shaped by
    /// mood and friendships, and memories of what you've been through together.
    /// </summary>
    public class SimLife : MonoBehaviour
    {
        private const float Tick = 0.5f;
        private const float NearbyRadius = 30f;

        private readonly Dictionary<string, bool> wasAlive = new Dictionary<string, bool>();
        private readonly Dictionary<string, int> lastLevel = new Dictionary<string, int>();
        private readonly Dictionary<SimPlayer, Vector3> lastPos = new Dictionary<SimPlayer, Vector3>();

        private float nextTick, nextIdle, nextBanter;
        private int playerLevel = -1;
        private bool playerAlive = true, wiped, wasInCombat;
        private string zone;

        private static SimBonds Bonds => Plugin.Bonds;
        private static System.Random Rng => Plugin.Rng;

        private void Update()
        {
            if (!Plugin.EnableLife.Value || !WorldContext.PlayerReady || GameData.SimMngr == null) return;
            float now = Time.time;
            if (now < nextTick) return;
            nextTick = now + Tick;

            try
            {
                Bonds.UseCharacter(WorldContext.PlayerName);
                Plugin.FriendsNow();
                FriendshipEvents.GreetOnLogin();
                if (zone != WorldContext.Zone)
                {
                    zone = WorldContext.Zone;
                    wasAlive.Clear();
                    lastLevel.Clear();
                    lastPos.Clear();
                    Bonds.Save();
                    Plugin.Friends.Save();
                    Scheduler.After(4f, SimActions.OnZoneChanged);
                    nextIdle = now + 20f;
                    nextBanter = now + RandomRange(Plugin.BanterMinSeconds.Value, Plugin.BanterMaxSeconds.Value);
                }
                Bonds.DecayMoods(Tick);
                SimActions.Tick();

                List<SimPlayer> group = GroupSims();
                WatchPlayer(group);
                WatchGroup(group);
                WatchCombat(group);
                if (now >= nextIdle)
                {
                    nextIdle = now + RandomRange(Plugin.IdleMinSeconds.Value, Plugin.IdleMaxSeconds.Value);
                    IdleAction();
                }
                if (now >= nextBanter)
                {
                    nextBanter = now + RandomRange(Plugin.BanterMinSeconds.Value, Plugin.BanterMaxSeconds.Value);
                    GroupBanter(group);
                }
                RememberPositions();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Sim life update failed: " + e);
                nextTick = now + 5f;
            }
        }

        // ================================================================ helpers

        private static float RandomRange(float a, float b) => b <= a ? a : UnityEngine.Random.Range(a, b);

        internal static bool Alive(SimPlayer sim) => sim != null && sim.MyStats != null && sim.MyStats.CurrentHP > 0;

        internal static bool Idle(SimPlayer sim)
        {
            NPC npc = sim.GetComponent<NPC>();
            return Alive(sim) && (npc == null || npc.CurrentAggroTarget == null);
        }

        internal static Vector3 PlayerPos => GameData.PlayerControl.transform.position;

        private static List<SimPlayer> GroupSims()
        {
            var list = new List<SimPlayer>();
            if (GameData.GroupMembers == null) return list;
            foreach (SimPlayerTracking member in GameData.GroupMembers)
            {
                if (member != null && member.MyAvatar != null) list.Add(member.MyAvatar);
            }
            return list;
        }

        internal static List<SimPlayer> NearbySims(float radius, bool includeGroup)
        {
            var list = new List<SimPlayer>();
            Vector3 p = PlayerPos;
            foreach (SimPlayer sim in GameData.SimMngr.ActiveSimInstances)
            {
                if (sim == null || !Alive(sim) || (!includeGroup && sim.InGroup)) continue;
                if (Vector3.Distance(sim.transform.position, p) <= radius) list.Add(sim);
            }
            return list;
        }

        private static T PickOne<T>(List<T> list) => list.Count == 0 ? default : list[Rng.Next(list.Count)];

        private static void Shuffle<T>(List<T> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = Rng.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        internal static string PersonaOf(SimPlayer sim) => SimVoices.PersonaOf(sim);

        /// <summary>A strong feeling about you (smitten, resentful...) wins over their passing mood.</summary>
        internal static string MoodOf(SimPlayer sim) => Feelings.Mood(sim.name) ?? Bonds.MoodOf(sim.name);

        private static string Line(string section, SimPlayer sim, params string[] tokens)
        {
            string line = Lines.Pick(section, PersonaOf(sim), MoodOf(sim));
            var map = new Dictionary<string, string> { ["zone"] = WorldContext.Zone };
            for (int i = 0; i + 1 < tokens.Length; i += 2) map[tokens[i]] = tokens[i + 1];
            return Lines.Fill(line, map);
        }

        private static string BondWith(SimPlayer a, SimPlayer b) =>
            SimBonds.Bond(Bonds.Affinity(a.name, b.name, PersonaOf(a), PersonaOf(b)));

        /// <summary>Picks a relationship-specific section if one has lines, otherwise the plain one.</summary>
        private static string Line(string section, SimPlayer sim, SimPlayer about)
        {
            string bond = BondWith(sim, about);
            string specific = section + "." + bond;
            bool useSpecific = bond != "neutral" && Plugin.Life.Has(specific) && Rng.NextDouble() < 0.6;
            return Line(useSpecific ? specific : section, sim, "other", about.name);
        }

        internal static void ActOut(SimPlayer sim, string line, Vector3? faceTowards = null)
        {
            if (sim == null || line == null) return;
            line = Speech.TakeAnimation(line, out string animation);
            if (faceTowards.HasValue && sim.GetComponent<NPC>()?.CurrentAggroTarget == null)
            {
                Speech.Face(sim, faceTowards.Value);
            }
            Speech.Animate(sim.GetComponent<Character>(), animation);
            Speech.Emote(sim.name, line);
        }

        private void RememberPositions()
        {
            lastPos.Clear();
            foreach (SimPlayer sim in GameData.SimMngr.ActiveSimInstances)
            {
                if (sim != null) lastPos[sim] = sim.transform.position;
            }
        }

        private bool StandingStill(SimPlayer sim) =>
            lastPos.TryGetValue(sim, out Vector3 before) && Vector3.Distance(before, sim.transform.position) < 0.2f;

        /// <summary>Adds friendship with a sim and, optionally, a memory of why.</summary>
        internal static void Befriend(SimPlayer sim, float points, string memory)
        {
            if (sim == null || !FriendshipEvents.On) return;
            Friendships friends = Plugin.FriendsNow();
            string key = Friendships.Sim(sim.name);
            friends.Meet(key, WorldContext.Zone);
            friends.Add(key, points);
            if (memory != null)
            {
                friends.Remember(key, memory);
                if (FriendshipEvents.IsGuildmate(sim.name)) FriendshipEvents.GuildNews(sim.name, memory);
            }
        }

        internal static int TierWith(SimPlayer sim) =>
            sim != null && FriendshipEvents.On ? Plugin.FriendsNow().Tier(Friendships.Sim(sim.name)) : 0;

        // ================================================================ events

        private void WatchPlayer(List<SimPlayer> group)
        {
            Stats stats = GameData.PlayerStats;
            if (playerLevel >= 0 && stats.Level > playerLevel) OnPlayerLevel(group);
            playerLevel = stats.Level;

            bool alive = stats.CurrentHP > 0;
            if (playerAlive && !alive) OnPlayerDeath(group);
            if (alive) wiped = false;
            playerAlive = alive;

            if (!alive && !wiped && group.Count > 0 && group.TrueForAll(s => !Alive(s)))
            {
                wiped = true;
                OnWipe(group);
            }
        }

        private void WatchGroup(List<SimPlayer> group)
        {
            foreach (SimPlayer sim in group)
            {
                string name = sim.name;
                bool alive = Alive(sim);
                if (wasAlive.TryGetValue(name, out bool before) && before && !alive) OnSimDeath(sim, group);
                wasAlive[name] = alive;

                int level = sim.MyStats != null ? sim.MyStats.Level : 0;
                if (lastLevel.TryGetValue(name, out int prev) && level > prev) OnSimLevel(sim, group);
                lastLevel[name] = level;
            }
        }

        private void WatchCombat(List<SimPlayer> group)
        {
            bool inCombat = GameData.InCombat;
            if (wasInCombat && !inCombat && playerAlive && group.Count > 0) OnFightWon(group);
            wasInCombat = inCombat;
        }

        private static void React(List<SimPlayer> who, int max, float chance, Action<SimPlayer, float> act)
        {
            var pool = new List<SimPlayer>(who);
            Shuffle(pool);
            float delay = RandomRange(1f, 2.5f);
            int count = 0;
            foreach (SimPlayer sim in pool)
            {
                if (count >= max) break;
                if (Rng.NextDouble() > chance) continue;
                act(sim, delay);
                delay += RandomRange(1.5f, 3.5f);
                count++;
            }
        }

        private void OnSimDeath(SimPlayer dead, List<SimPlayer> group)
        {
            Bonds.ShiftMood(dead.name, -0.25f);
            Bonds.History(dead.name).SimDeaths++;
            var witnesses = group.FindAll(s => s != dead && Alive(s));
            foreach (SimPlayer s in witnesses)
            {
                if (BondWith(s, dead) == "friend") Bonds.ShiftMood(s.name, -0.1f);
            }
            React(witnesses, 2, Plugin.ReactionChance.Value, (sim, delay) =>
            {
                string line = Line("react.death", sim, dead);
                Scheduler.After(delay, () => { if (Alive(sim)) Speech.Party(sim, line); });
            });
        }

        private void OnPlayerDeath(List<SimPlayer> group)
        {
            foreach (SimPlayer sim in group) Bonds.History(sim.name).PlayerDeaths++;
            React(group.FindAll(Alive), 2, Plugin.ReactionChance.Value, (sim, delay) =>
            {
                string line = Line("react.playerdeath", sim);
                Scheduler.After(delay, () => { if (Alive(sim)) Speech.Party(sim, line); });
            });
            React(NearbySims(20f, includeGroup: false), 1, 0.4f, (sim, delay) =>
            {
                string line = Line("react.playerdeath.witness", sim);
                Scheduler.After(delay, () => { if (Alive(sim)) ActOut(sim, line); });
            });
        }

        private void OnWipe(List<SimPlayer> group)
        {
            foreach (SimPlayer sim in group)
            {
                Bonds.ShiftMood(sim.name, -0.4f);
                string p = PersonaOf(sim);
                if (p == "grump" || p == "tryhard") Feelings.Shift(sim.name, "resentment", 0.4f);
                else Feelings.Shift(sim.name, "trust", 0.1f); // survived it together
                SharedHistory h = Bonds.History(sim.name);
                h.Wipes++;
                h.LastWipeZone = WorldContext.Zone;
                Befriend(sim, 1f, "we wiped together in " + WorldContext.Zone);
            }
            SimPlayer speaker = PickOne(group);
            if (speaker != null)
            {
                string line = Line("react.wipe", speaker);
                Scheduler.After(RandomRange(4f, 7f), () => Speech.Party(speaker, line));
            }
        }

        private void OnPlayerLevel(List<SimPlayer> group)
        {
            FriendshipEvents.GuildNews(null, "you reached level " + GameData.PlayerStats.Level);
            foreach (SimPlayer sim in group)
            {
                // Competitive types get jealous when you pull ahead; others are impressed.
                bool ahead = sim.MyStats != null && GameData.PlayerStats.Level > sim.MyStats.Level;
                if (ahead && Feelings.Competitive(PersonaOf(sim))) Feelings.Shift(sim.name, "envy", 0.6f);
                else Feelings.Shift(sim.name, "respect", 0.3f);
                Bonds.History(sim.name).Levels++;
                Bonds.ShiftMood(sim.name, 0.2f);
                Befriend(sim, 3f, "you hit level " + GameData.PlayerStats.Level + " while we were grouped");
            }
            React(group.FindAll(Alive), 2, 0.85f, (sim, delay) =>
            {
                string line = Line("react.levelup.player", sim);
                Scheduler.After(delay, () => Speech.Party(sim, line));
            });
            React(NearbySims(25f, includeGroup: false), 3, 0.5f, (sim, delay) =>
            {
                string action = Line("react.levelup.witness", sim);
                string words = Rng.NextDouble() < 0.5 ? Line("react.levelup.witness.say", sim) : null;
                Scheduler.After(delay, () =>
                {
                    if (!Idle(sim)) return;
                    ActOut(sim, action, PlayerPos);
                    if (words != null) Scheduler.After(1.2f, () => Speech.Say(sim, words));
                });
            });
        }

        private void OnSimLevel(SimPlayer leveled, List<SimPlayer> group)
        {
            Bonds.ShiftMood(leveled.name, 0.4f);
            Befriend(leveled, 2f, "i hit level " + (leveled.MyStats != null ? leveled.MyStats.Level : 0) + " in your group");
            React(group.FindAll(s => s != leveled && Alive(s)), 1, 0.6f, (sim, delay) =>
            {
                string line = Line("react.levelup.sim", sim, leveled);
                Scheduler.After(delay + 2f, () => Speech.Party(sim, line));
            });
        }

        private void OnFightWon(List<SimPlayer> group)
        {
            string here = WorldContext.Zone;
            for (int i = 0; i < group.Count; i++)
            {
                SharedHistory h = Bonds.History(group[i].name);
                h.Fights++;
                h.LastZone = here;
                Bonds.ShiftMood(group[i].name, 0.03f);
                Befriend(group[i], 0.5f, null);
                Feelings.Shift(group[i].name, "trust", 0.05f);
                Feelings.Shift(group[i].name, "respect", 0.03f);
                for (int j = i + 1; j < group.Count; j++) Bonds.Nudge(group[i].name, group[j].name, 0.01f);
            }
            SimActions.OnFightWon(group);
            if (Rng.NextDouble() > Plugin.PostFightChance.Value) return;

            Stats p = GameData.PlayerStats;
            bool close = p.CurrentHP < p.CurrentMaxHP * 0.3f ||
                         group.Exists(s => Alive(s) && s.MyStats.CurrentHP < s.MyStats.CurrentMaxHP * 0.25f);
            SimPlayer speaker = PickOne(group.FindAll(Alive));
            if (speaker == null) return;
            string line = Line(close ? "react.fight.close" : "react.fight.won", speaker);
            Scheduler.After(RandomRange(1.5f, 3.5f), () => { if (!GameData.InCombat) Speech.Party(speaker, line); });
        }

        // ================================================================ group banter

        private void GroupBanter(List<SimPlayer> group)
        {
            var living = group.FindAll(Idle);
            if (living.Count == 0 || GameData.InCombat || !playerAlive) return;
            SimPlayer speaker = PickOne(living);
            double roll = Rng.NextDouble();

            if (living.Count >= 2 && roll < 0.45)
            {
                SimPlayer other = PickOne(living.FindAll(s => s != speaker));
                string bond = BondWith(speaker, other);
                string line = Line("banter." + bond, speaker, "other", other.name);
                Speech.Party(speaker, line);
                Bonds.Nudge(speaker.name, other.name, bond == "rival" ? -0.01f : 0.01f);
                if (Rng.NextDouble() < 0.6)
                {
                    string reply = Line("banter.reply." + BondWith(other, speaker), other, "other", speaker.name);
                    Scheduler.After(RandomRange(3f, 6f), () => { if (Idle(other)) Speech.Party(other, reply); });
                }
                return;
            }

            if (roll < 0.7)
            {
                string memory = MemoryLine(speaker, "memory.banter");
                if (memory != null)
                {
                    Speech.Party(speaker, memory);
                    return;
                }
            }

            string zoneSection = "banter.zone." + WorldContext.Zone;
            string talk = Plugin.Life.Has(zoneSection) && Rng.NextDouble() < 0.4 ? Line(zoneSection, speaker) : Line("banter", speaker);
            Speech.Party(speaker, talk);
        }

        /// <summary>A line about something you and this sim went through, or null if nothing stands out.</summary>
        internal static string MemoryLine(SimPlayer sim, string prefix) => MemoryLine(sim.name, prefix, sim);

        internal static string MemoryLine(string simName, string prefix, SimPlayer sim)
        {
            if (!Bonds.Knows(simName)) return null;
            SharedHistory h = Bonds.History(simName);
            var options = new List<KeyValuePair<string, string>>();
            if (h.Wipes > 0 && !string.IsNullOrEmpty(h.LastWipeZone)) options.Add(new KeyValuePair<string, string>("wipe", h.Wipes.ToString()));
            if (h.PlayerDeaths >= 3) options.Add(new KeyValuePair<string, string>("playerdeaths", h.PlayerDeaths.ToString()));
            if (h.SimDeaths >= 3) options.Add(new KeyValuePair<string, string>("simdeaths", h.SimDeaths.ToString()));
            if (h.Fights >= 15) options.Add(new KeyValuePair<string, string>("fights", h.Fights.ToString()));
            if (h.Levels > 0) options.Add(new KeyValuePair<string, string>("levels", h.Levels.ToString()));
            if (options.Count == 0) return null;
            var pick = options[Rng.Next(options.Count)];
            string line = Lines.Pick(prefix + "." + pick.Key, sim != null ? PersonaOf(sim) : null, Bonds.MoodOf(simName));
            return Lines.Fill(line, new Dictionary<string, string>
            {
                ["count"] = pick.Value,
                ["wipezone"] = h.LastWipeZone,
                ["lastzone"] = h.LastZone,
                ["zone"] = WorldContext.Zone,
            });
        }

        // ================================================================ idle actions

        private void IdleAction()
        {
            var candidates = NearbySims(NearbyRadius, includeGroup: !GameData.InCombat);
            candidates = candidates.FindAll(s => Idle(s) && StandingStill(s));
            if (candidates.Count == 0) return;
            SimPlayer sim = PickOne(candidates);
            float toPlayer = Vector3.Distance(sim.transform.position, PlayerPos);

            bool friend = TierWith(sim) >= 2;
            if ((toPlayer < 6f && Rng.NextDouble() < 0.25) || (friend && toPlayer < 15f && Rng.NextDouble() < 0.4))
            {
                ActOut(sim, Line(friend && Plugin.Life.Has("action.toplayer.friend") ? "action.toplayer.friend" : "action.toplayer", sim), PlayerPos);
                return;
            }

            var neighbours = candidates.FindAll(o => o != sim && Vector3.Distance(o.transform.position, sim.transform.position) < 8f);
            if (neighbours.Count > 0 && Rng.NextDouble() < 0.35)
            {
                SimPlayer other = PickOne(neighbours);
                ActOut(sim, Line("action.social", sim, other), other.transform.position);
                if (Rng.NextDouble() < 0.5)
                {
                    string back = Line("action.social.back", other, sim);
                    Scheduler.After(RandomRange(1.5f, 3f), () =>
                    {
                        if (Idle(other)) ActOut(other, back, sim != null ? sim.transform.position : (Vector3?)null);
                    });
                }
                return;
            }

            ActOut(sim, Line("action.idle", sim));
        }
    }
}
