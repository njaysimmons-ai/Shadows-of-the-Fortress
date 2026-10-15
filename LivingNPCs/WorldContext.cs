using UnityEngine;
using UnityEngine.SceneManagement;

namespace LivingNPCs
{
    public enum NpcRole
    {
        Townsfolk,
        Vendor,
        Guard,
        Banker,
    }

    /// <summary>Reads the bits of game state that dialogue reacts to, and fills in {tokens}.</summary>
    public static class WorldContext
    {
        public static string Zone => SceneManager.GetActiveScene().name;

        public static string TimeOfDay
        {
            get
            {
                if (GameData.Time == null)
                {
                    return "day";
                }
                int hour = GameData.Time.GetHour();
                if (hour >= 5 && hour < 12) return "morning";
                if (hour >= 12 && hour < 17) return "afternoon";
                if (hour >= 17 && hour < 21) return "evening";
                return "night";
            }
        }

        public static bool PlayerReady =>
            GameData.PlayerStats != null && !GameData.InCharSelect && !GameData.Zoning && GameData.PlayerControl != null;

        public static string PlayerName => GameData.PlayerStats != null ? GameData.PlayerStats.MyName : "traveler";

        public static string PlayerClass
        {
            get
            {
                Class cls = GameData.PlayerStats != null ? GameData.PlayerStats.CharacterClass : null;
                if (cls == null) return "adventurer";
                return string.IsNullOrEmpty(cls.DisplayName) ? cls.ClassName : cls.DisplayName;
            }
        }

        public static bool PlayerHurt =>
            GameData.PlayerStats != null && GameData.PlayerStats.CurrentMaxHP > 0 &&
            GameData.PlayerStats.CurrentHP < GameData.PlayerStats.CurrentMaxHP * 0.35f;

        public static NpcRole RoleOf(NPC npc)
        {
            string name = npc.NPCName ?? "";
            if (npc.GetComponent<VendorInventory>() != null) return NpcRole.Vendor;
            if (name.Contains("Bank") || name == "Prestigio Valusha" || name == "Validus Greencent" || name == "Comstock Retalio") return NpcRole.Banker;
            Character character = npc.GetChar();
            if (character != null && (character.MyFaction == Character.Faction.GoodGuard || character.MyFaction == Character.Faction.EvilGuard)) return NpcRole.Guard;
            if (name.Contains("Guard") || name.Contains("Sentry") || name.Contains("Watch")) return NpcRole.Guard;
            return NpcRole.Townsfolk;
        }

        public static bool IsHostileToPlayer(NPC npc)
        {
            Character character = npc.GetChar();
            if (character == null) return false;
            return (character.MyWorldFaction != null && character.MyWorldFaction.FactionValue < 0f) ||
                   (character.AggressiveTowards != null && character.AggressiveTowards.Contains(Character.Faction.Player));
        }

        public static string Fill(string line, NPC npc)
        {
            if (line == null) return null;
            Stats stats = GameData.PlayerStats;
            return line
                .Replace("{player}", PlayerName)
                .Replace("{class}", PlayerClass)
                .Replace("{level}", stats != null ? stats.Level.ToString() : "1")
                .Replace("{npc}", npc != null ? npc.NPCName : "")
                .Replace("{zone}", Zone)
                .Replace("{time}", TimeOfDay);
        }
    }
}
