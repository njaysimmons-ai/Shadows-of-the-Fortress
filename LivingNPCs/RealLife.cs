using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace LivingNPCs
{
    /// <summary>
    /// The person behind each sim: an age, a job, where they live, hobbies and a quirk, picked from the
    /// sim's name so they're the same every session. Lets sims talk about real life like real players,
    /// instead of only the game. The lists live in ai.txt ([ai.life.*]) and can be edited.
    /// </summary>
    internal static class RealLife
    {
        // Words that mean the conversation is about the game. Without any, sims talk as themselves.
        private static readonly Regex GameTalk = new Regex(
            @"\b(raid|raids|boss|bosses|dungeon|quest|quests|level|levels|lvl|ding|xp|exp|loot|drop|drops|gear|armor|armour|weapon|sword|staff|mob|mobs|pull|pulls|aggro|" +
            @"tank|healer|heals|dps|buff|buffs|spell|spells|mana|zone|zones|guild|group|party|grp|inv|camp|spawn|respawn|wipe|wiped|corpse|vendor|bank|gold|plat|" +
            @"paladin|warrior|ranger|druid|arcanist|duelist|stormcaller|reaver|windblade|class|skill|skills|erenshor|azure|stowaway|brake|loomingwood|rottenfoot|" +
            @"willowwatch|silkengrass|hidden hills|rockshade|game|server|online|log ?in|log ?off|logging|afk|npc|npcs|lore)\b",
            RegexOptions.IgnoreCase);

        /// <summary>Game jargon that sounds wrong in a conversation about something else.</summary>
        public static readonly Regex Jargon = new Regex(
            @"\b(raid|raids|boss|bosses|dungeon|dungeons|grind|grinding|loot|xp|quest|quests|mobs|pulls|dps|aggro|wipe|leveling|level up|gear up|adventure|adventures|adventuring|realm)\b",
            RegexOptions.IgnoreCase);

        public static bool AboutTheGame(string text) => GameTalk.IsMatch(text ?? "");

        private static readonly Dictionary<string, string> Cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static void Reset() => Cache.Clear();

        /// <summary>One or two sentences about who this sim is outside the game.</summary>
        public static string Of(string name, string persona)
        {
            if (string.IsNullOrEmpty(name)) return "";
            if (Cache.TryGetValue(name, out string cached)) return cached;
            uint h = Hash(name);
            int age = Age(persona, ref h);
            string job = Pick("ai.life.jobs" + (age <= 23 ? ".young" : ""), ref h) ?? Pick("ai.life.jobs", ref h) ?? "works an ordinary job";
            string place = Pick("ai.life.places", ref h) ?? "in a small town";
            string hobby1 = Pick("ai.life.hobbies", ref h) ?? "music";
            string hobby2 = Pick("ai.life.hobbies", ref h) ?? "cooking";
            if (hobby2 == hobby1) hobby2 = Pick("ai.life.hobbies", ref h) ?? "movies";
            string quirk = Pick("ai.life.quirks", ref h);
            string text = "Outside the game you're a real person: " + age + " years old, you " + job + " and live " + place +
                          ". You're into " + hobby1 + " and " + hobby2 + "." + (quirk != null ? " " + Char.ToUpperInvariant(quirk[0]) + quirk.Substring(1) + "." : "");
            Cache[name] = text;
            return text;
        }

        private static int Age(string persona, ref uint h)
        {
            int lo = 21, hi = 38;
            switch (persona)
            {
                case "newbie": case "hype": case "drama": case "lootgoblin": lo = 19; hi = 25; break;
                case "oldschool": case "mentor": lo = 34; hi = 52; break;
                case "grump": lo = 28; hi = 48; break;
            }
            return lo + (int)(Next(ref h) % (uint)(hi - lo + 1));
        }

        private static string Pick(string section, ref uint h)
        {
            DialogueBank bank = Plugin.Ai.Has(section) ? Plugin.Ai : Plugin.AiDefaults;
            IReadOnlyList<string> lines = bank.Get(section);
            if (lines.Count == 0) return null;
            return lines[(int)(Next(ref h) % (uint)lines.Count)].Trim();
        }

        private static uint Hash(string s)
        {
            uint h = 2166136261;
            foreach (char c in s.ToLowerInvariant()) h = (h ^ c) * 16777619;
            return h;
        }

        private static uint Next(ref uint h)
        {
            h ^= h << 13;
            h ^= h >> 17;
            h ^= h << 5;
            return h;
        }
    }
}
