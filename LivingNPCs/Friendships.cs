using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace LivingNPCs
{
    /// <summary>
    /// One thing a character remembers about you. Kinds: "fact" (about you: likes, goals, what you're like),
    /// "plan" (something you agreed to do together), "event" (something that happened), "milestone"
    /// (how you met, friendship tiers; never forgotten), "chat" (what you said in guild chat).
    /// </summary>
    public class MemoryItem
    {
        public DateTime When;
        public string Text;
        public string Kind;

        public MemoryItem(DateTime when, string text, string kind)
        {
            When = when;
            Text = text;
            Kind = kind;
        }
    }

    /// <summary>A long-term bond between your character and one sim or NPC.</summary>
    public class Friendship
    {
        public float Points;
        public DateTime FirstMet = DateTime.UtcNow;
        public DateTime LastSeen = DateTime.UtcNow;
        public int Talks;
        public int GainDay;
        public float GainedToday;
        public readonly List<MemoryItem> Memories = new List<MemoryItem>();
        public readonly List<Ollama.Message> Conversation = new List<Ollama.Message>();

        /// <summary>A short running note of what they know about you, rewritten by the AI as memories pile up.</summary>
        public string Summary = "";
        public readonly Dictionary<string, float> Feelings = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        public int SinceSummary;

        public IEnumerable<MemoryItem> OfKind(string kind)
        {
            foreach (MemoryItem m in Memories) if (m.Kind == kind) yield return m;
        }
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

        private const int MaxConversation = 30;

        /// <summary>How many of each kind of memory a character keeps; guildmates keep twice as many.</summary>
        private static int Cap(string kind, bool guildmate)
        {
            int cap = kind == "fact" ? 40 : kind == "plan" ? 15 : kind == "chat" ? 12 : kind == "milestone" ? 1000 : 60;
            return guildmate ? cap * 2 : cap;
        }

        /// <summary>Set by FriendshipEvents: whether a key belongs to a guildmate (or is the guild's shared memory).</summary>
        public static Func<string, bool> IsGuildmate = _ => false;

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
                        string text = Unescape(p[3]);
                        string kind = p.Length >= 5 ? p[4] : (text.StartsWith("we became") || text.StartsWith("we first met") ? "milestone" : "event");
                        Get(p[1]).Memories.Add(new MemoryItem(Date(p[2]), text, kind));
                        break;
                    case "E" when p.Length >= 3:
                        Feelings.Deserialize(Get(p[1]), p[2]);
                        break;
                    case "S" when p.Length >= 3:
                        Get(p[1]).Summary = Unescape(p[2]);
                        break;
                    case "C" when p.Length >= 4:
                        Get(p[1]).Conversation.Add(new Ollama.Message(p[2], Unescape(p[3])));
                        break;
                }
            }
            dirty = false;
        }

        public bool Knows(string key) => all.ContainsKey(key);

        public void MarkDirty() => dirty = true;

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
            Feelings.Fade(f, (DateTime.UtcNow - f.LastSeen).TotalDays);
            f.LastSeen = DateTime.UtcNow;
            if (amount > 0)
            {
                int today = DateTime.UtcNow.Year * 1000 + DateTime.UtcNow.DayOfYear;
                if (f.GainDay != today)
                {
                    f.GainDay = today;
                    f.GainedToday = 0f;
                }
                float cap = Plugin.FriendshipDailyCap.Value * (IsGuildmate(key) ? 1.5f : 1f);
                amount = Math.Min(amount, Math.Max(0f, cap - f.GainedToday));
                f.GainedToday += amount;
            }
            // Guildmates bond faster.
            if (amount > 0 && IsGuildmate(key)) amount *= 1.5f;
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
        public void Remember(string key, string memory, string kind = null)
        {
            if (string.IsNullOrWhiteSpace(memory)) return;
            memory = memory.Trim().TrimEnd('.', '!');
            if (kind == null) kind = memory.StartsWith("we became") || memory.StartsWith("we first met") ? "milestone" : "event";
            Friendship f = Get(key);
            foreach (MemoryItem m in f.Memories)
            {
                // Facts and plans aren't repeated at all; events can recur on another day.
                bool same = string.Equals(m.Text, memory, StringComparison.OrdinalIgnoreCase);
                if (same && (kind == "fact" || kind == "plan" || (DateTime.UtcNow - m.When).TotalHours < 24)) return;
            }
            f.Memories.Add(new MemoryItem(DateTime.UtcNow, memory, kind));
            if (kind != "chat") f.SinceSummary++;

            // Forget the oldest of this kind first. Plans also lapse after two weeks.
            f.Memories.RemoveAll(m => m.Kind == "plan" && (DateTime.UtcNow - m.When).TotalDays > 14);
            int cap = Cap(kind, IsGuildmate(key));
            int count = 0;
            foreach (MemoryItem m in f.Memories) if (m.Kind == kind) count++;
            while (count > cap)
            {
                int drop = f.Memories.FindIndex(m => m.Kind == kind);
                f.Memories.RemoveAt(drop);
                count--;
            }
            dirty = true;
        }

        /// <summary>Memories that share words with what you just said, so the right things come back to mind.</summary>
        public static List<MemoryItem> Relevant(Friendship f, string text, int max)
        {
            var found = new List<MemoryItem>();
            if (string.IsNullOrEmpty(text)) return found;
            var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string w in System.Text.RegularExpressions.Regex.Split(text.ToLowerInvariant(), "[^a-z0-9']+"))
            {
                if (w.Length >= 4) words.Add(w);
            }
            if (words.Count == 0) return found;
            for (int i = f.Memories.Count - 1; i >= 0 && found.Count < max; i--)
            {
                foreach (string w in System.Text.RegularExpressions.Regex.Split(f.Memories[i].Text.ToLowerInvariant(), "[^a-z0-9']+"))
                {
                    if (words.Contains(w)) { found.Add(f.Memories[i]); break; }
                }
            }
            return found;
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
                foreach (MemoryItem m in f.Memories) lines.Add(string.Join("\t", "M", pair.Key, m.When.Ticks.ToString(), Escape(m.Text), m.Kind));
                if (!string.IsNullOrEmpty(f.Summary)) lines.Add("S\t" + pair.Key + "\t" + Escape(f.Summary));
                string feelings = Feelings.Serialize(f);
                if (feelings.Length > 0) lines.Add("E\t" + pair.Key + "\t" + feelings);
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
