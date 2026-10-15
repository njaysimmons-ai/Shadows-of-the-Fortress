using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace LivingNPCs
{
    /// <summary>What one NPC remembers about the current character.</summary>
    public class NpcRecord
    {
        public int Hails;
        public DateTime LastSeenUtc;
    }

    /// <summary>
    /// Remembers how often, and how recently, each NPC has spoken with each character.
    /// Stored as one tab-separated file per character so saves never collide.
    /// </summary>
    public class NpcMemory
    {
        private readonly string folder;
        private readonly Dictionary<string, NpcRecord> records = new Dictionary<string, NpcRecord>();
        private string loadedFor;
        private bool dirty;

        public NpcMemory(string folder)
        {
            this.folder = folder;
            Directory.CreateDirectory(folder);
        }

        /// <summary>Switches to the given character's memory, saving the previous one first.</summary>
        public void UseCharacter(string characterName)
        {
            if (string.IsNullOrEmpty(characterName) || characterName == loadedFor)
            {
                return;
            }
            Save();
            records.Clear();
            loadedFor = characterName;
            string path = PathFor(characterName);
            if (!File.Exists(path))
            {
                return;
            }
            foreach (string line in File.ReadAllLines(path))
            {
                string[] parts = line.Split('\t');
                if (parts.Length < 3 || !int.TryParse(parts[1], out int hails) ||
                    !long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long ticks))
                {
                    continue;
                }
                records[parts[0]] = new NpcRecord { Hails = hails, LastSeenUtc = new DateTime(ticks, DateTimeKind.Utc) };
            }
        }

        /// <summary>Returns the record as it was before this meeting (null if never met), then logs the meeting.</summary>
        public NpcRecord RecordHail(string npcName)
        {
            records.TryGetValue(npcName, out NpcRecord before);
            NpcRecord snapshot = before == null ? null : new NpcRecord { Hails = before.Hails, LastSeenUtc = before.LastSeenUtc };
            if (before == null)
            {
                before = new NpcRecord();
                records[npcName] = before;
            }
            before.Hails++;
            before.LastSeenUtc = DateTime.UtcNow;
            dirty = true;
            return snapshot;
        }

        public void Save()
        {
            if (!dirty || loadedFor == null)
            {
                return;
            }
            var lines = new List<string>();
            foreach (var pair in records)
            {
                lines.Add(pair.Key + "\t" + pair.Value.Hails + "\t" + pair.Value.LastSeenUtc.Ticks.ToString(CultureInfo.InvariantCulture));
            }
            File.WriteAllLines(PathFor(loadedFor), lines);
            dirty = false;
        }

        private string PathFor(string characterName)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                characterName = characterName.Replace(c, '_');
            }
            return Path.Combine(folder, characterName + ".tsv");
        }
    }
}
