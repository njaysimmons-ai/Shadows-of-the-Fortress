using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace LivingNPCs
{
    /// <summary>
    /// How a sim feels about you, beyond friendship points: affection, attraction, respect, trust, envy and
    /// resentment, each 0-10. They move with what you say (judged by the AI after a conversation) and do
    /// (hugs, taunts, out-levelling them, wipes), shaped by the sim's personality, and fade slowly while you're
    /// apart. Together they decide whether a sim adores you, has fallen for you, envies you or can't stand you.
    /// </summary>
    public static class Feelings
    {
        public static readonly string[] Names = { "affection", "attraction", "respect", "trust", "envy", "resentment" };

        /// <summary>How strongly each personality feels each thing (1 = normal).</summary>
        private static float Sensitivity(string persona, string feeling)
        {
            switch (persona)
            {
                case "grump": return feeling == "resentment" ? 1.6f : feeling == "affection" ? 0.7f : 1f;
                case "wholesome": return feeling == "affection" || feeling == "trust" ? 1.4f : feeling == "resentment" ? 0.5f : 1f;
                case "tryhard": return feeling == "envy" ? 1.6f : feeling == "respect" ? 1.3f : 1f;
                case "lootgoblin": return feeling == "envy" ? 1.5f : 1f;
                case "hype": return feeling == "attraction" || feeling == "affection" ? 1.3f : 1f;
                case "roleplayer": return feeling == "attraction" || feeling == "respect" ? 1.3f : 1f;
                case "newbie": return feeling == "respect" || feeling == "trust" ? 1.4f : 1f;
                case "terse": return 0.7f;
                case "chill": return feeling == "resentment" || feeling == "envy" ? 0.6f : 1f;
                case "charmer": return feeling == "attraction" ? 1.8f : feeling == "affection" ? 1.3f : 1f;
                case "mentor": return feeling == "trust" || feeling == "affection" ? 1.3f : feeling == "envy" ? 0.4f : 1f;
                case "drama": return 1.5f;
                case "conspiracy": return feeling == "trust" ? 0.6f : feeling == "resentment" ? 1.4f : 1f;
                case "pirate": return feeling == "respect" || feeling == "trust" ? 1.2f : 1f;
                case "shy": return feeling == "trust" ? 0.8f : feeling == "attraction" || feeling == "affection" ? 1.3f : feeling == "resentment" ? 0.6f : 1f;
                case "braggart": return feeling == "envy" ? 1.8f : feeling == "respect" ? 0.8f : 1f;
                case "explorer": return feeling == "envy" ? 0.5f : feeling == "affection" ? 1.2f : 1f;
                case "sleepy": return 0.8f;
                case "superstitious": return feeling == "trust" || feeling == "resentment" ? 1.3f : 1f;
                default: return 1f;
            }
        }

        public static bool Competitive(string persona) =>
            persona == "tryhard" || persona == "grump" || persona == "lootgoblin" || persona == "jokester" || persona == "braggart" || persona == "drama";

        private static float Get(Friendship f, string feeling) => f.Feelings.TryGetValue(feeling, out float v) ? v : 0f;

        /// <summary>Shifts one feeling for a sim, scaled by their personality, and nudges the game's opinion of you.</summary>
        public static void Shift(string simName, string feeling, float amount)
        {
            if (!FriendshipEvents.On || !Plugin.EnableFeelings.Value || amount == 0f || Array.IndexOf(Names, feeling) < 0) return;
            Friendships friends = Plugin.FriendsNow();
            string key = Friendships.Sim(simName);
            Friendship f = friends.Get(key);
            string persona = SimVoices.PersonaOf(FriendshipEvents.Tracking(simName)?.MyAvatar);
            float scaled = amount * Sensitivity(persona, feeling) * Plugin.FeelingsSpeed.Value;
            string before = Strongest(f);
            f.Feelings[feeling] = Math.Max(0f, Math.Min(10f, Get(f, feeling) + scaled));
            friends.MarkDirty();

            // Warmth makes them keener to group and invite you; resentment the opposite.
            SimPlayerTracking t = FriendshipEvents.Tracking(simName);
            if (t != null && Plugin.FriendshipAffectsGame.Value)
            {
                float opinion = feeling == "affection" || feeling == "trust" ? 0.4f : feeling == "attraction" || feeling == "respect" ? 0.2f
                              : feeling == "resentment" ? -0.6f : feeling == "envy" ? -0.15f : 0f;
                t.OpinionOfPlayer += opinion * scaled;
            }

            string after = Strongest(f);
            if (after != before && after != null)
            {
                friends.Remember(key, MilestoneFor(after), "milestone");
                AIChat.DebugNote(simName + " now " + Describe(f));
            }
        }

        /// <summary>Feelings fade toward neutral while you're apart: grudges and crushes alike.</summary>
        public static void Fade(Friendship f, double daysApart)
        {
            if (daysApart < 1) return;
            float keep = (float)Math.Pow(0.97, daysApart);
            foreach (string n in Names)
            {
                if (f.Feelings.TryGetValue(n, out float v)) f.Feelings[n] = v * keep;
            }
        }

        /// <summary>The single strongest notable feeling, as a stance: "inlove", "smitten", "resentful"...</summary>
        public static string Strongest(Friendship f)
        {
            float aff = Get(f, "affection"), att = Get(f, "attraction"), res = Get(f, "resentment"), env = Get(f, "envy");
            float rsp = Get(f, "respect"), tru = Get(f, "trust");
            if (res >= 7f) return "hates";
            if (att >= 7f && aff >= 5f) return "inlove";
            if (res >= 4f) return "resentful";
            if (att >= 4f) return "smitten";
            if (env >= 5f) return "envious";
            if (aff >= 7f && tru >= 5f) return "devoted";
            if (rsp >= 6f) return "admiring";
            return null;
        }

        private static string MilestoneFor(string stance)
        {
            switch (stance)
            {
                case "hates": return "i started to really dislike you";
                case "inlove": return "i fell for you";
                case "resentful": return "you started to get on my nerves";
                case "smitten": return "i started to have a crush on you";
                case "envious": return "i got jealous of how well you're doing";
                case "devoted": return "you became one of my closest people";
                case "admiring": return "i started to really look up to you";
                default: return "my feelings about you changed";
            }
        }

        /// <summary>A mood name for line picking (e.g. [emote.hug.react.mood.smitten]), or null.</summary>
        public static string Mood(string simName)
        {
            if (!FriendshipEvents.On || !Plugin.EnableFeelings.Value) return null;
            Friendships friends = Plugin.FriendsNow();
            string key = Friendships.Sim(simName);
            return friends.Knows(key) ? Strongest(friends.Get(key)) : null;
        }

        /// <summary>Plain-language feelings for the AI prompt and /memories.</summary>
        public static string Describe(Friendship f)
        {
            var parts = new List<string>();
            float aff = Get(f, "affection"), att = Get(f, "attraction"), res = Get(f, "resentment"), env = Get(f, "envy");
            float rsp = Get(f, "respect"), tru = Get(f, "trust");
            if (att >= 7f && aff >= 5f) parts.Add("you've fallen in love with them, though you may not say it outright");
            else if (att >= 4f) parts.Add("you have a crush on them and get a bit flustered around them");
            else if (att >= 2f) parts.Add("you find them a little attractive");
            if (res >= 7f) parts.Add("you've come to really dislike them, even hate them");
            else if (res >= 4f) parts.Add("you resent them and they get on your nerves");
            else if (res >= 2f) parts.Add("they've annoyed you a few times");
            if (env >= 5f) parts.Add("you're envious of them (their level, gear or success) and it shows");
            else if (env >= 2.5f) parts.Add("you're a little jealous of them");
            if (aff >= 7f && att < 4f) parts.Add("you're deeply fond of them");
            else if (aff >= 4f && att < 4f) parts.Add("you're fond of them");
            if (rsp >= 6f) parts.Add("you admire them");
            else if (rsp >= 3f) parts.Add("you respect them");
            if (tru >= 6f) parts.Add("you trust them completely");
            else if (tru < 1f && res >= 3f) parts.Add("you don't trust them");
            return parts.Count == 0 ? "" : string.Join("; ", parts);
        }

        public static string Serialize(Friendship f)
        {
            var sb = new StringBuilder();
            foreach (var pair in f.Feelings)
            {
                if (pair.Value < 0.05f) continue;
                if (sb.Length > 0) sb.Append(',');
                sb.Append(pair.Key).Append('=').Append(pair.Value.ToString("0.##", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        public static void Deserialize(Friendship f, string text)
        {
            foreach (string part in (text ?? "").Split(','))
            {
                string[] kv = part.Split('=');
                if (kv.Length == 2 && float.TryParse(kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float v)) f.Feelings[kv[0]] = v;
            }
        }
    }
}
