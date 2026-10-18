using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace LivingNPCs
{
    /// <summary>A way of talking that a sim player can be given: typing habits plus extra lines.</summary>
    public class Persona
    {
        public string Id;
        public float Weight = 10f;
        public HashSet<int> Fits = new HashSet<int>();
        public float LowerChance, CapsChance, EmojiChance, ThirdPersonChance;
        public float TypoMin = 0.1f, TypoMax = 0.4f;
        public List<string> SignOffs = new List<string>();
        public string About = "";
        public readonly Dictionary<string, List<string>> Lines = new Dictionary<string, List<string>>();

        public bool FitsTemperament(int personalityType) => Fits.Count == 0 || Fits.Contains(personalityType);
    }

    /// <summary>
    /// Erenshor builds every generated sim from one blank template: the same handful of phrase pools
    /// and the same plain typing style. This gives each generated sim a persona, chosen from its name
    /// so it stays the same every session, which changes how it types and mixes its own lines into
    /// every phrase list the game draws from.
    /// </summary>
    public static class SimVoices
    {
        private static readonly Dictionary<string, FieldInfo> LanguageLists = new Dictionary<string, FieldInfo>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<SimPlayerLanguage, Persona> Assigned = new Dictionary<SimPlayerLanguage, Persona>();
        private static List<Persona> personas = new List<Persona>();
        private static Dictionary<string, string> overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static DialogueBank bank = new DialogueBank();

        static SimVoices()
        {
            foreach (FieldInfo field in typeof(SimPlayerLanguage).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (field.FieldType == typeof(List<string>))
                {
                    LanguageLists[field.Name] = field;
                }
            }
        }

        public static int PersonaCount => personas.Count;

        public static string AboutOf(string personaId)
        {
            Persona p = personaId == null ? null : personas.Find(x => string.Equals(x.Id, personaId, StringComparison.OrdinalIgnoreCase));
            return p != null ? p.About : "";
        }

        /// <summary>The persona id this sim was given, or null for hand-made sims and sims without one.</summary>
        public static string PersonaOf(SimPlayer sim)
        {
            if (sim == null) return null;
            SimPlayerLanguage language = sim.GetComponent<SimPlayerLanguage>();
            return language != null && Assigned.TryGetValue(language, out Persona p) ? p.Id : null;
        }

        public static void Load(DialogueBank source)
        {
            bank = source;
            var byId = new Dictionary<string, Persona>(StringComparer.OrdinalIgnoreCase);
            var newOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (string section in source.SectionNames)
            {
                if (section.StartsWith("sim.", StringComparison.OrdinalIgnoreCase))
                {
                    string simName = section.Substring(4);
                    foreach (string line in source.Get(section))
                    {
                        if (TrySetting(line, out string key, out string value) && key == "persona")
                        {
                            newOverrides[simName] = value;
                        }
                    }
                    continue;
                }
                if (!section.StartsWith("persona.", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                string rest = section.Substring(8);
                int dot = rest.IndexOf('.');
                string id = dot < 0 ? rest : rest.Substring(0, dot);
                if (!byId.TryGetValue(id, out Persona persona))
                {
                    persona = new Persona { Id = id };
                    byId[id] = persona;
                }
                if (dot < 0)
                {
                    foreach (string line in source.Get(section))
                    {
                        ApplySetting(persona, line);
                    }
                    continue;
                }
                string list = rest.Substring(dot + 1);
                if (!LanguageLists.ContainsKey(list))
                {
                    Plugin.Log.LogWarning("sims.txt: [" + section + "] does not match any sim phrase list; it will be ignored.");
                    continue;
                }
                persona.Lines[list] = new List<string>(source.Get(section));
            }
            personas = new List<Persona>(byId.Values);
            overrides = newOverrides;
        }

        private static bool TrySetting(string line, out string key, out string value)
        {
            key = value = null;
            if (!line.StartsWith("@")) return false;
            int eq = line.IndexOf('=');
            if (eq < 0) return false;
            key = line.Substring(1, eq - 1).Trim().ToLowerInvariant();
            value = line.Substring(eq + 1).Trim();
            return true;
        }

        private static float ParseFloat(string value) =>
            float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : 0f;

        private static void ApplySetting(Persona persona, string line)
        {
            if (!TrySetting(line, out string key, out string value)) return;
            switch (key)
            {
                case "weight": persona.Weight = ParseFloat(value); break;
                case "about": persona.About = value; break;
                case "lower": persona.LowerChance = ParseFloat(value); break;
                case "caps": persona.CapsChance = ParseFloat(value); break;
                case "emojis": persona.EmojiChance = ParseFloat(value); break;
                case "thirdperson": persona.ThirdPersonChance = ParseFloat(value); break;
                case "typos":
                    string[] range = value.Split('-');
                    persona.TypoMin = ParseFloat(range[0]);
                    persona.TypoMax = range.Length > 1 ? ParseFloat(range[1]) : persona.TypoMin;
                    break;
                case "fits":
                    foreach (string part in value.Split(','))
                    {
                        if (int.TryParse(part.Trim(), out int type)) persona.Fits.Add(type);
                    }
                    break;
                case "signoff":
                    foreach (string part in value.Split('|'))
                    {
                        if (part.Trim().Length > 0) persona.SignOffs.Add(part.Trim());
                    }
                    break;
            }
        }

        /// <summary>FNV-1a, so a sim's persona never changes between sessions or machines.</summary>
        private static int StableHash(string text)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (char c in text)
                {
                    hash = (hash ^ c) * 16777619;
                }
                return (int)(hash & 0x7fffffff);
            }
        }

        private static Persona Choose(string simName, int personalityType, System.Random rng)
        {
            if (overrides.TryGetValue(simName, out string forced))
            {
                Persona match = personas.Find(p => string.Equals(p.Id, forced, StringComparison.OrdinalIgnoreCase));
                if (match != null) return match;
            }
            var candidates = personas.FindAll(p => p.Weight > 0f && p.FitsTemperament(personalityType));
            if (candidates.Count == 0) candidates = personas.FindAll(p => p.Weight > 0f);
            float total = 0f;
            foreach (Persona p in candidates) total += p.Weight;
            if (total <= 0f) return null;
            double roll = rng.NextDouble() * total;
            foreach (Persona p in candidates)
            {
                roll -= p.Weight;
                if (roll <= 0) return p;
            }
            return candidates[candidates.Count - 1];
        }

        public static void Apply(GameObject simObject, SimPlayerTracking tracking)
        {
            if (!Plugin.EnableSimVoices.Value || simObject == null || tracking == null || personas.Count == 0)
            {
                return;
            }
            string name = tracking.SimName;
            if (string.IsNullOrEmpty(name) || (!Plugin.SimVoicesForPremade.Value && IsPremade(name)))
            {
                return;
            }
            SimPlayer sim = simObject.GetComponent<SimPlayer>();
            SimPlayerLanguage language = simObject.GetComponent<SimPlayerLanguage>();
            if (sim == null || language == null)
            {
                return;
            }

            var rng = new System.Random(StableHash(name));
            Persona persona = Choose(name, sim.PersonalityType, rng);
            if (persona == null)
            {
                return;
            }

            sim.TypesInAllLowers = rng.NextDouble() < persona.LowerChance;
            sim.TypesInAllCaps = !sim.TypesInAllLowers && rng.NextDouble() < persona.CapsChance;
            sim.LovesEmojis = rng.NextDouble() < persona.EmojiChance;
            sim.TypesInThirdPerson = rng.NextDouble() < persona.ThirdPersonChance;
            sim.TypoRate = Mathf.Lerp(persona.TypoMin, persona.TypoMax, (float)rng.NextDouble());
            sim.TypoChance = sim.TypoRate;
            sim.SignOffLine = new List<string>();
            if (persona.SignOffs.Count > 0)
            {
                sim.SignOffLine.Add(persona.SignOffs[rng.Next(persona.SignOffs.Count)]);
            }

            // Lines are drawn at random each session (like the game does), so two sims who share a
            // persona still won't share every phrase.
            var shuffle = new System.Random();
            float share = Mathf.Clamp(Plugin.SimPersonaShare.Value, 0.05f, 0.95f);
            foreach (var pair in persona.Lines)
            {
                var target = (List<string>)LanguageLists[pair.Key].GetValue(language);
                if (target == null || pair.Value.Count == 0)
                {
                    continue;
                }
                var picked = new List<string>(pair.Value);
                for (int i = picked.Count - 1; i > 0; i--)
                {
                    int j = shuffle.Next(i + 1);
                    (picked[i], picked[j]) = (picked[j], picked[i]);
                }
                int keep = Math.Min(picked.Count, Math.Max(3, target.Count));
                picked.RemoveRange(keep, picked.Count - keep);
                int repeats = target.Count == 0 ? 1 : Math.Max(1, (int)Math.Round(share * target.Count / ((1f - share) * keep)));
                for (int r = 0; r < repeats; r++)
                {
                    target.AddRange(picked);
                }
            }

            Assigned[language] = persona;
            WorldIndex.NotePersona(name, persona.Id);
            if (Plugin.LogSimPersonas.Value)
            {
                Plugin.Log.LogInfo("Sim " + name + " is " + persona.Id +
                                   (sim.TypesInAllLowers ? ", types in lowercase" : sim.TypesInAllCaps ? ", types in caps" : "") +
                                   (sim.LovesEmojis ? ", loves emojis" : ""));
            }
        }

        internal static bool IsPremade(string name)
        {
            var premades = GameData.SimMngr != null ? GameData.SimMngr.ActualSims : null;
            if (premades == null) return false;
            foreach (GameObject premade in premades)
            {
                if (premade != null && premade.name == name) return true;
            }
            return false;
        }

        /// <summary>
        /// The game's small-talk shouts come from one pool per zone, shared by every sim. A sim with
        /// a persona sometimes says something from its own small talk, or a zone line, instead.
        /// </summary>
        public static string SmallTalk(SimPlayerLanguage language, string fromGame)
        {
            if (!Plugin.EnableSimVoices.Value || language == null || !Assigned.TryGetValue(language, out Persona persona))
            {
                return fromGame;
            }
            if (Plugin.Rng.NextDouble() >= Plugin.SimSmallTalkShare.Value)
            {
                return fromGame;
            }
            string zone = WorldContext.Zone;
            string line = bank.PickAny(Plugin.Rng, new[] { "smalltalk.zone." + zone, "persona." + persona.Id + ".zone." + zone });
            if (line != null && Plugin.Rng.NextDouble() < 0.35)
            {
                return line;
            }
            List<string> own = language.GenericLines;
            return own != null && own.Count > 0 ? own[Plugin.Rng.Next(own.Count)] : fromGame;
        }

        public static void ForgetDestroyed()
        {
            var dead = new List<SimPlayerLanguage>();
            foreach (var key in Assigned.Keys)
            {
                if (key == null) dead.Add(key);
            }
            foreach (var key in dead) Assigned.Remove(key);
        }
    }

    [HarmonyPatch(typeof(SimPlayerMngr), nameof(SimPlayerMngr.LoadSimPlayerTemplate))]
    internal static class SimSpawnPatch
    {
        private static void Postfix(GameObject _spawningSim, SimPlayerTracking _loadFrom)
        {
            try
            {
                SimVoices.Apply(_spawningSim, _loadFrom);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not give sim a persona: " + e);
            }
        }
    }

    [HarmonyPatch(typeof(SimPlayerLanguage), nameof(SimPlayerLanguage.GetGeneric))]
    internal static class SimSmallTalkPatch
    {
        private static void Postfix(SimPlayerLanguage __instance, ref string __result)
        {
            try
            {
                __result = SimVoices.SmallTalk(__instance, __result);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Sim small talk failed: " + e);
            }
        }
    }
}
