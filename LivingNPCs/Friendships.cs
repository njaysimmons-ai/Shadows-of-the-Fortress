using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace LivingNPCs
{
    /// <summary>A long-term bond between your character and one sim or NPC.</summary>
    public class Friendship
    {
        public float Points;
        public DateTime FirstMet = DateTime.UtcNow;
        public DateTime LastSeen = DateTime.UtcNow;
        public int Talks;
        public int GainDay;
        public float GainedToday;
        public readonly List<KeyValuePair<DateTime, string>> Memories = new List<KeyValuePair<DateTime, string>>();
        public readonly List<Ollama.Message> Conversation = new List<Ollama.Message>();
    }

    /// <summary>
    /// Friendships that last: points earned by adventuring, talking and emoting together, friendship
    /// tiers, a journal of things each character remembers about you, and your recent AI conversations.
    /// Saved per character in memory/&lt;character&gt;.friends.tsv. Keys are "sim:Name" or "npc:Name".
    /// </summary>
    public class Friendships
    {
        public static readonly float[] TierPoints = { 0f, 10f, 30f, 70f, 150f, 300f };
        public static readonly string[] TierNames = { "Stranger", "Acquaintance", "Friend", "Good Friend", "Close Friend", "Best Friend" };
        public static readonly string[] TierIds = { "stranger", "acquaintance", "friend", "goodfriend", "closefriend", "bestfriend" };

        private const int MaxMemories = 40;
        private const int MaxConversation = 16;

        private readonly string folder;
        private readonly Dictionary<string, Friendship> all = new Dictionary<string, Friendship>(StringComparer.OrdinalIgnoreCase);
        private string loadedFor;
        private bool dirty;

        /// <summary>Raised on the game thread when someone reaches a new tier: (key, new tier).</summary>
        public event Action<string, int> TierReached;

        public Friendships(string folder)
        {
            this.folder = folder;
            Directory.CreateDirectory(folder);
        }

        public static string Sim(string name) => "sim:" + name;
        public static string Npc(string name) => "npc:" + name;
        public static string NameOf(string key) => key.Substring(key.IndexOf(':') + 1);

        public IEnumerable<KeyValuePair<string, Friendship>> All => all;

        public void UseCharacter(string characterName)
        {
            if (string.IsNullOrEmpty(characterName) || characterName == loadedFor) return;
            Save();
            all.Clear();
            loadedFor = characterName;
            string path = PathFor(characterName);
            if (!File.Exists(path)) return;
            foreach (string line in File.ReadAllLines(path))
            {
                string[] p = line.Split('\t');
                if (p.Length < 2) continue;
                Friendship f;
                switch (p[0])
                {
                    case "F" when p.Length >= 8:
                        f = Get(p[1]);
                        f.Points = Float(p[2]);
                        f.FirstMet = Date(p[3]);
                        f.LastSeen = Date(p[4]);
                        f.Talks = Int(p[5]);
                        f.GainDay = Int(p[6]);
                        f.GainedToday = Float(p[7]);
                        break;
                    case "M" when p.Length >= 4:
                        Get(p[1]).Memories.Add(new KeyValuePair<DateTime, string>(Date(p[2]), Unescape(p[3])));
                        break;
                    case "C" when p.Length >= 4:
                        Get(p[1]).Conversation.Add(new Ollama.Message(p[2], Unescape(p[3])));
                        break;
                }
            }
            dirty = false;
        }

        public bool Knows(string key) => all.ContainsKey(key);

        public Friendship Get(string key)
        {
            if (!all.TryGetValue(key, out Friendship f))
            {
                f = new Friendship();
                all[key] = f;
                dirty = true;
            }
            return f;
        }

        public static int TierOf(float points)
        {
            int tier = 0;
            for (int i = 0; i < TierPoints.Length; i++) if (points >= TierPoints[i]) tier = i;
            return tier;
        }

        public int Tier(string key) => all.TryGetValue(key, out Friendship f) ? TierOf(f.Points) : 0;

        /// <summary>
        /// Adds (or removes) friendship. Gains are capped per real day per character so grinding
        /// can't rush a bond; losses are never capped.
        /// </summary>
        public void Add(string key, float amount)
        {
            Friendship f = Get(key);
            f.LastSeen = DateTime.UtcNow;
            if (amount > 0)
            {
                int today = DateTime.UtcNow.Year * 1000 + DateTime.UtcNow.DayOfYear;
                if (f.GainDay != today)
                {
                    f.GainDay = today;
                    f.GainedToday = 0f;
                }
                float cap = Plugin.FriendshipDailyCap.Value;
                amount = Math.Min(amount, Math.Max(0f, cap - f.GainedToday));
                f.GainedToday += amount;
            }
            int before = TierOf(f.Points);
            f.Points = Math.Max(-50f, f.Points + amount * Plugin.FriendshipSpeed.Value);
            int after = TierOf(f.Points);
            dirty = true;
            if (after > before)
            {
                Remember(key, "we became " + (after == TierIds.Length - 1 ? "best friends" : TierNames[after].ToLowerInvariant() + "s"));
                TierReached?.Invoke(key, after);
            }
        }

        /// <summary>
        /// Writes a memory as a short phrase that reads after "remember when", e.g.
        /// "we wiped together in Rottenfoot". The same memory isn't repeated within a day.
        /// </summary>
        public void Remember(string key, string memory)
        {
            if (string.IsNullOrWhiteSpace(memory)) return;
            memory = memory.Trim().TrimEnd('.', '!');
            Friendship f = Get(key);
            foreach (var m in f.Memories)
            {
                if (string.Equals(m.Value, memory, StringComparison.OrdinalIgnoreCase) &&
                    (DateTime.UtcNow - m.Key).TotalHours < 24) return;
            }
            f.Memories.Add(new KeyValuePair<DateTime, string>(DateTime.UtcNow, memory));
            // Forget the oldest everyday memories first, but always keep how you met and milestones.
            while (f.Memories.Count > MaxMemories)
            {
                int drop = f.Memories.FindIndex(1, m => !m.Value.StartsWith("we became") && !m.Value.StartsWith("we first met"));
                f.Memories.RemoveAt(drop < 0 ? 1 : drop);
            }
            dirty = true;
        }

        public void AddConversation(string key, Ollama.Message heard, Ollama.Message reply)
        {
            Friendship f = Get(key);
            f.Conversation.Add(heard);
            f.Conversation.Add(reply);
            while (f.Conversation.Count > MaxConversation) f.Conversation.RemoveAt(0);
            f.Talks++;
            dirty = true;
        }

        /// <summary>Notes a first meeting, if this is one.</summary>
        public void Meet(string key, string zone)
        {
            if (Knows(key)) return;
            Get(key);
            Remember(key, "we first met in " + zone);
        }

        public static string Ago(DateTime when)
        {
            double days = (DateTime.UtcNow - when).TotalDays;
            if (days < 1) return "today";
            if (days < 2) return "yesterday";
            if (days < 14) return (int)days + " days ago";
            if (days < 60) return (int)(days / 7) + " weeks ago";
            return (int)(days / 30) + " months ago";
        }

        // ---------------------------------------------------------------- persistence

        public void Save()
        {
            if (!dirty || loadedFor == null) return;
            var lines = new List<string>();
            foreach (var pair in all)
            {
                Friendship f = pair.Value;
                lines.Add(string.Join("\t", "F", pair.Key, F(f.Points), f.FirstMet.Ticks.ToString(), f.LastSeen.Ticks.ToString(),
                    f.Talks.ToString(), f.GainDay.ToString(), F(f.GainedToday)));
                foreach (var m in f.Memories) lines.Add(string.Join("\t", "M", pair.Key, m.Key.Ticks.ToString(), Escape(m.Value)));
                foreach (var c in f.Conversation) lines.Add(string.Join("\t", "C", pair.Key, c.Role, Escape(c.Content)));
            }
            File.WriteAllLines(PathFor(loadedFor), lines);
            dirty = false;
        }

        private static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        private static float Float(string s) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0f;
        private static int Int(string s) => int.TryParse(s, out int v) ? v : 0;
        private static DateTime Date(string s) => long.TryParse(s, out long t) ? new DateTime(t, DateTimeKind.Utc) : DateTime.UtcNow;
        private static string Escape(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\r", "").Replace("\n", "\\n");

        private static string Unescape(string s)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    char n = s[++i];
                    sb.Append(n == 't' ? '\t' : n == 'n' ? '\n' : n);
                }
                else sb.Append(s[i]);
            }
            return sb.ToString();
        }

        private string PathFor(string characterName)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) characterName = characterName.Replace(c, '_');
            return Path.Combine(folder, characterName + ".friends.tsv");
        }
    }
}
