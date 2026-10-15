using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace LivingNPCs
{
    /// <summary>
    /// Adds a Bald hairstyle. The game shows whichever hair model matches the character's saved
    /// hairstyle name and hides the rest, so a name that matches no model ("Bald") means no hair, and
    /// it saves and loads like any other style. Beards are separate and unaffected.
    /// </summary>
    internal static class Appearance
    {
        public const string BaldName = "Bald";

        private static readonly AccessTools.FieldRef<Personalize, int> HairIndex =
            AccessTools.FieldRefAccess<Personalize, int>("hairIndex");

        /// <summary>The menu click the original arrows play. Audio types are reached by reflection to keep dependencies small.</summary>
        private static void Click()
        {
            try
            {
                object source = AccessTools.Field(typeof(GameData), "PlayerAud").GetValue(null);
                object clip = AccessTools.Field(GameData.Misc.GetType(), "Click").GetValue(GameData.Misc);
                source?.GetType().GetMethod("PlayOneShot", new[] { clip.GetType(), typeof(float) })
                    ?.Invoke(source, new[] { clip, (object)(GameData.UIVolume * GameData.MasterVol) });
            }
            catch
            {
                // No sound is fine.
            }
        }

        internal static bool IsBaldSlot(Personalize p) => HairIndex(p) >= p.AllHair.Count;

        private static void Apply(string hairName)
        {
            ModularParts body = GameData.PlayerInv.Modulars;
            body.HairName = hairName;
            body.UpdateHair(body.HairName, body.HairCol);
        }

        // ---------------------------------------------------------------- character creation

        /// <summary>The style arrows now cycle through every hairstyle plus Bald at the end.</summary>
        [HarmonyPatch(typeof(Personalize), nameof(Personalize.ChangeHairStyle))]
        private static class HairArrows
        {
            private static bool Prefix(Personalize __instance, int dir)
            {
                if (!Plugin.BaldOption.Value) return true;
                try
                {
                    Click();
                    int count = __instance.AllHair.Count;
                    int index = HairIndex(__instance);
                    if (index < count) __instance.AllHair[index].gameObject.SetActive(false);
                    index = ((index + dir) % (count + 1) + count + 1) % (count + 1);
                    HairIndex(__instance) = index;
                    if (index == count)
                    {
                        Apply(BaldName);
                    }
                    else
                    {
                        __instance.AllHair[index].gameObject.SetActive(true);
                        Apply(__instance.AllHair[index].name);
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError("Hair style change failed: " + e);
                }
                return false;
            }
        }

        /// <summary>Shows "Bald" instead of a number for the bald slot.</summary>
        [HarmonyPatch(typeof(Personalize), "Update")]
        private static class HairLabel
        {
            private static readonly System.Reflection.FieldInfo LabelField = AccessTools.Field(typeof(Personalize), "HairStyleTxt");

            private static void Postfix(Personalize __instance)
            {
                if (!Plugin.BaldOption.Value || !IsBaldSlot(__instance)) return;
                object label = LabelField?.GetValue(__instance);
                label?.GetType().GetProperty("text")?.SetValue(label, BaldName);
            }
        }

        /// <summary>
        /// Switching gender looks up the selected hairstyle by index, which would fail on the bald
        /// slot. Let it use a real style, then make the new body bald again.
        /// </summary>
        [HarmonyPatch(typeof(Personalize), nameof(Personalize.ChangeGender))]
        private static class GenderSwitch
        {
            private static void Prefix(Personalize __instance, out bool __state)
            {
                __state = IsBaldSlot(__instance);
                if (__state) HairIndex(__instance) = 0;
            }

            private static void Postfix(Personalize __instance, bool __state)
            {
                if (!__state) return;
                HairIndex(__instance) = __instance.AllHair.Count;
                foreach (Transform hair in __instance.AllHair) hair.gameObject.SetActive(false);
                Apply(BaldName);
            }
        }

        // ---------------------------------------------------------------- existing characters

        /// <summary>The hairstyle names available on the current body, in order.</summary>
        private static List<string> Styles()
        {
            var styles = new List<string>();
            Transform root = GameData.PlayerInv.Modulars.Hair;
            if (root == null) return styles;
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t != root && t.name != "All_01_Hair" && t.GetComponent<Renderer>() != null && !styles.Contains(t.name))
                {
                    styles.Add(t.name);
                }
            }
            styles.Add(BaldName);
            return styles;
        }

        /// <summary>/hair, /hair prev, /hair bald: restyle the character you're playing.</summary>
        [HarmonyPatch(typeof(TypeText), "CheckCommands")]
        private static class HairCommand
        {
            private static readonly System.Reflection.FieldInfo TypedField = AccessTools.Field(typeof(TypeText), "typed");

            private static bool Prefix(TypeText __instance)
            {
                object typed = TypedField?.GetValue(__instance);
                string text = (typed?.GetType().GetProperty("text")?.GetValue(typed) as string ?? "").Trim().ToLowerInvariant();
                if (text != "/hair" && !text.StartsWith("/hair ") && text != "/bald") return true;
                try
                {
                    List<string> styles = Styles();
                    string current = GameData.PlayerInv.Modulars.HairName;
                    int index = Math.Max(0, styles.IndexOf(current));
                    string arg = text == "/bald" ? "bald" : text.Length > 5 ? text.Substring(6).Trim() : "";
                    if (arg == "bald") index = styles.Count - 1;
                    else if (arg == "prev") index = (index - 1 + styles.Count) % styles.Count;
                    else index = (index + 1) % styles.Count;
                    Apply(styles[index]);
                    UpdateSocialLog.LogAdd("Hairstyle " + (styles[index] == BaldName ? "Bald" : (index + 1) + " of " + (styles.Count - 1)) +
                                           ". /hair for the next style, /hair prev to go back, /hair bald for none. It's saved with your character.", "yellow");
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError("/hair failed: " + e);
                }
                return false;
            }
        }
    }
}
