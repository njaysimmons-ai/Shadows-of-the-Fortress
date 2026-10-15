using System;
using System.Collections.Generic;
using System.IO;

namespace LivingNPCs
{
    /// <summary>
    /// Lines of dialogue grouped by section, loaded from a plain-text file:
    ///
    ///   # comment
    ///   [greet.first]
    ///   Haven't seen you around before, {player}.
    ///
    /// Section names are case-insensitive. Blank lines are ignored.
    /// </summary>
    public class DialogueBank
    {
        private readonly Dictionary<string, List<string>> sections =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        public int LineCount { get; private set; }

        public static DialogueBank Parse(TextReader reader)
        {
            var bank = new DialogueBank();
            List<string> current = null;
            string raw;
            while ((raw = reader.ReadLine()) != null)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#"))
                {
                    continue;
                }
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    string name = line.Substring(1, line.Length - 2).Trim();
                    if (!bank.sections.TryGetValue(name, out current))
                    {
                        current = new List<string>();
                        bank.sections[name] = current;
                    }
                    continue;
                }
                if (current != null)
                {
                    current.Add(line);
                    bank.LineCount++;
                }
            }
            return bank;
        }

        public static DialogueBank Load(string path)
        {
            using (var reader = new StreamReader(path))
            {
                return Parse(reader);
            }
        }

        public bool Has(string section)
        {
            return sections.TryGetValue(section, out var lines) && lines.Count > 0;
        }

        public IReadOnlyList<string> Get(string section)
        {
            return sections.TryGetValue(section, out var lines) ? lines : (IReadOnlyList<string>)Array.Empty<string>();
        }

        /// <summary>Returns a random line from the first section that has any, or null.</summary>
        public string PickFirst(Random rng, params string[] candidates)
        {
            foreach (string section in candidates)
            {
                if (section != null && Has(section))
                {
                    var lines = sections[section];
                    return lines[rng.Next(lines.Count)];
                }
            }
            return null;
        }

        /// <summary>Returns a random line drawn from every listed section combined, or null.</summary>
        public string PickAny(Random rng, IEnumerable<string> candidates)
        {
            var pool = new List<string>();
            foreach (string section in candidates)
            {
                if (section != null && sections.TryGetValue(section, out var lines))
                {
                    pool.AddRange(lines);
                }
            }
            return pool.Count == 0 ? null : pool[rng.Next(pool.Count)];
        }
    }
}
