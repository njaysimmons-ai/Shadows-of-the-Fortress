using System.Collections.Generic;

namespace LivingNPCs
{
    /// <summary>
    /// Picks lines from life.txt, preferring the sim's persona and mood:
    /// [react.death.grump] beats [react.death.mood.low] beats [react.death].
    /// </summary>
    public static class Lines
    {
        public static string Pick(string section, string persona = null, string mood = null)
        {
            DialogueBank bank = Plugin.Life;
            System.Random rng = Plugin.Rng;
            if (bank == null) return null;
            string personal = persona != null ? section + "." + persona : null;
            string moody = mood != null && mood != "neutral" ? section + ".mood." + mood : null;

            if (personal != null && bank.Has(personal) && rng.NextDouble() < 0.65) return bank.PickFirst(rng, personal);
            if (moody != null && bank.Has(moody) && rng.NextDouble() < 0.6) return bank.PickFirst(rng, moody);
            return bank.PickFirst(rng, section, personal, moody);
        }

        public static string Fill(string line, IDictionary<string, string> tokens)
        {
            if (line == null) return null;
            foreach (var pair in tokens)
            {
                line = line.Replace("{" + pair.Key + "}", pair.Value ?? "");
            }
            return WorldContext.Fill(line, null);
        }
    }
}
