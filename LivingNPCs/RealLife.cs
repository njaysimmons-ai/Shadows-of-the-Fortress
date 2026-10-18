using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace LivingNPCs
{
    /// <summary>
    /// The person behind each sim: age, gender, what they do (school, work or retirement), where they
    /// live, hobbies and a quirk, picked from the sim's name so they're the same every session. Lets sims
    /// talk about real life like real players, instead of only the game. The lists live in ai.txt
    /// ([ai.people.*]) and can be edited.
    ///
    /// Players under 18 never flirt and never develop romantic feelings, whatever their persona or tone.
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

        public class Person
        {
            public int Age;
            public string Gender;       // "man", "woman" or "nonbinary"
            public string Life;         // "a nurse who works long shifts", "in seventh grade"
            public string Place;        // "in the suburbs"
            public string Hobbies;      // "hiking and true crime podcasts"
            public string Quirk;
            public string Extra;        // anything you wrote yourself in the /ai panel
            public bool Edited;

            public bool Minor => Age < 18;

            public string Noun => Age < 13 ? (Gender == "man" ? "boy" : Gender == "woman" ? "girl" : "kid")
                                : Age < 18 ? (Gender == "man" ? "teenage boy" : Gender == "woman" ? "teenage girl" : "teenager")
                                : Gender == "nonbinary" ? "nonbinary person" : Gender;

            public string AgeGroup => Age < 14 ? "Kids (10-13)" : Age < 18 ? "Teens (14-17)" : Age < 30 ? "18-29" : Age < 45 ? "30-44" : Age < 60 ? "45-59" : "60+";

            /// <summary>"34-year-old woman, a nurse who works long shifts, living in the suburbs. Into hiking and true crime podcasts. Always tired from work."</summary>
            public string Summary =>
                Age + "-year-old " + Noun + ", " + Life + ", living " + Place + "." +
                (string.IsNullOrEmpty(Hobbies) ? "" : " Into " + Hobbies + ".") +
                (string.IsNullOrEmpty(Quirk) ? "" : " " + Sentence(Quirk)) +
                (string.IsNullOrEmpty(Extra) ? "" : " " + Sentence(Extra));
        }

        private static string Sentence(string s)
        {
            s = s.Trim();
            if (s.Length == 0) return "";
            s = Char.ToUpperInvariant(s[0]) + s.Substring(1);
            return ".!?".IndexOf(s[s.Length - 1]) >= 0 ? s : s + ".";
        }

        private static readonly Dictionary<string, Person> Cache = new Dictionary<string, Person>(StringComparer.OrdinalIgnoreCase);

        public static void Reset() => Cache.Clear();

        /// <summary>For the AI prompt: who this sim is outside the game, plus the under-18 rules when they apply.</summary>
        public static string Of(string name, string persona)
        {
            Person p = PersonOf(name, persona);
            if (p == null) return "";
            string text = "Outside the game you're a real person: a " + p.Summary.Replace(". Into ", ". You're into ") +
                          " Your pronouns are " + (p.Gender == "man" ? "he/him" : p.Gender == "woman" ? "she/her" : "they/them") + ".";
            return p.Minor ? text + " " + MinorRules(p) : text;
        }

        public static string MinorRules(Person p) =>
            "You're only " + p.Age + ", so talk like a " + (p.Age < 13 ? "kid" : "teenager") + " your age. You never flirt, and anything romantic or sexual is completely off-limits: " +
            "if {player} goes there, you get weirded out and shut it down the way a real " + (p.Age < 13 ? "kid" : "teen") + " would.";

        public static bool IsMinor(string name) => PersonOf(name, null)?.Minor ?? false;

        /// <summary>The person behind a sim. The persona is looked up if not given.</summary>
        public static Person PersonOf(string name, string persona)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (persona != null && Cache.TryGetValue(name + "|" + persona, out Person known)) return known;
            SimPlayerTracking t = FriendshipEvents.Tracking(name);
            persona = persona ?? (t?.MyAvatar != null ? SimVoices.PersonaOf(t.MyAvatar) : null) ?? WorldIndex.PersonaOf(name);
            string cacheKey = name + "|" + persona;
            if (Cache.TryGetValue(cacheKey, out Person cached)) return cached;

            uint h = Hash(name);
            var p = new Person { Age = Age(ref h) };
            // Some personas only make sense for adults: the old-timer, the mentor and the flirt.
            if (persona == "oldschool" || persona == "mentor") p.Age = Math.Max(p.Age, 30 + p.Age % 25);
            if (persona == "charmer") p.Age = Math.Max(p.Age, 19 + p.Age % 20);
            p.Gender = Gender(t?.Gender, ref h);

            string stage = p.Age < 14 ? "kid" : p.Age < 18 ? "teen" : p.Age < 23 ? "young" : p.Age >= 65 ? "retired" : "adult";
            if (stage == "young" && Next(ref h) % 10 < 3) stage = "adult";
            if (p.Age >= 60 && p.Age < 65 && Next(ref h) % 10 < 3) stage = "retired";
            p.Life = Pick("ai.people.life." + stage, ref h) ?? Pick("ai.people.life.adult", ref h) ?? "working an ordinary job";
            bool youngster = stage == "kid" || stage == "teen";
            p.Place = Pick(youngster ? "ai.people.places.family" : "ai.people.places", ref h) ?? "in a small town";
            string hobbies = youngster ? "ai.people.hobbies.young" : "ai.people.hobbies";
            string hobby1 = Pick(hobbies, ref h) ?? "music";
            string hobby2 = Pick(hobbies, ref h) ?? "movies";
            for (int i = 0; i < 3 && hobby2 == hobby1; i++) hobby2 = Pick(hobbies, ref h) ?? "movies";
            p.Hobbies = hobby1 + " and " + hobby2;
            p.Quirk = Pick(youngster ? "ai.people.quirks.young" : stage == "retired" ? "ai.people.quirks.retired" : "ai.people.quirks", ref h)
                      ?? Pick("ai.people.quirks", ref h);
            ApplyEdits(name, p);
            Cache[cacheKey] = p;
            return p;
        }

        // ---------------------------------------------------------------- your edits (the /ai panel)

        public static readonly string[] Fields = { "age", "gender", "life", "place", "hobbies", "quirk", "extra" };
        private static readonly Dictionary<string, Dictionary<string, string>> Edits = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        private static string editsPath;

        private static void ApplyEdits(string name, Person p)
        {
            if (!Edits.TryGetValue(name, out var e) || e.Count == 0) return;
            p.Edited = true;
            if (e.TryGetValue("age", out string age) && int.TryParse(age, out int a)) p.Age = Math.Max(10, Math.Min(99, a));
            if (e.TryGetValue("gender", out string g) && (g == "man" || g == "woman" || g == "nonbinary")) p.Gender = g;
            if (e.TryGetValue("life", out string life)) p.Life = life;
            if (e.TryGetValue("place", out string place)) p.Place = place;
            if (e.TryGetValue("hobbies", out string hobbies)) p.Hobbies = hobbies;
            if (e.TryGetValue("quirk", out string quirk)) p.Quirk = quirk;
            if (e.TryGetValue("extra", out string extra)) p.Extra = extra;
        }

        /// <summary>Saves your version of a sim's background. Pass null to go back to the generated one.</summary>
        public static void SetEdits(string name, Dictionary<string, string> fields)
        {
            if (fields == null || fields.Count == 0) Edits.Remove(name);
            else Edits[name] = new Dictionary<string, string>(fields);
            Reset();
            SaveEdits();
        }

        public static void LoadEdits(string folder)
        {
            editsPath = System.IO.Path.Combine(folder, "people.tsv");
            Edits.Clear();
            if (!System.IO.File.Exists(editsPath)) return;
            foreach (string line in System.IO.File.ReadAllLines(editsPath))
            {
                string[] p = line.Split('\t');
                if (p.Length < 3) continue;
                if (!Edits.TryGetValue(p[0], out var e)) Edits[p[0]] = e = new Dictionary<string, string>();
                e[p[1]] = p[2].Replace("\\n", " ");
            }
            Reset();
        }

        private static void SaveEdits()
        {
            if (editsPath == null) return;
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(editsPath));
            var lines = new List<string>();
            foreach (var person in Edits)
                foreach (var field in person.Value)
                    lines.Add(person.Key + "\t" + field.Key + "\t" + (field.Value ?? "").Replace("\t", " ").Replace("\r", " ").Replace("\n", " "));
            try { System.IO.File.WriteAllLines(editsPath, lines); }
            catch (Exception e) { Plugin.Log.LogError("Couldn't save people.tsv: " + e.Message); }
        }

        // Roughly who plays online games: mostly adults, some teens and kids, a few retirees.
        private static int Age(ref uint h)
        {
            uint roll = Next(ref h) % 100;
            int lo, hi;
            if (roll < 6) { lo = 10; hi = 13; }
            else if (roll < 16) { lo = 14; hi = 17; }
            else if (roll < 50) { lo = 18; hi = 29; }
            else if (roll < 78) { lo = 30; hi = 44; }
            else if (roll < 92) { lo = 45; hi = 59; }
            else { lo = 60; hi = 70; }
            return lo + (int)(Next(ref h) % (uint)(hi - lo + 1));
        }

        // Most people play their own gender; some don't, and a few are nonbinary.
        private static string Gender(string character, ref uint h)
        {
            uint roll = Next(ref h) % 100;
            bool male = character == null ? Next(ref h) % 2 == 0 : character == "Male";
            if (roll < 5) return "nonbinary";
            if (roll < 18) male = !male;
            return male ? "man" : "woman";
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
            return h == 0 ? 1u : h;
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
