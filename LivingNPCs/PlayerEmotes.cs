using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace LivingNPCs
{
    /// <summary>
    /// Adds emote commands (/wave, /bow, /cheer, ...). Every [emote.NAME.self] section in life.txt
    /// becomes a command. Sims and NPCs you emote at react with their own actions and words, and
    /// nearby sims may join in when you /dance.
    /// </summary>
    [HarmonyPatch(typeof(TypeText), "CheckCommands")]
    internal static class PlayerEmotes
    {
        private static readonly FieldInfo TypedField = AccessTools.Field(typeof(TypeText), "typed");

        private static string TypedText(TypeText input)
        {
            object typed = TypedField?.GetValue(input);
            return typed?.GetType().GetProperty("text")?.GetValue(typed) as string;
        }

        private static bool Prefix(TypeText __instance)
        {
            try
            {
                if (!Plugin.EnableEmotes.Value) return true;
                string text = TypedText(__instance);
                if (string.IsNullOrEmpty(text) || text[0] != '/') return true;
                string command = text.Trim().Split(' ')[0].Substring(1).ToLowerInvariant();

                if (command == "emotes")
                {
                    ListEmotes();
                    return false;
                }
                if (command == "dance")
                {
                    // The game plays the player's dance; we only invite others to join.
                    Scheduler.After(1f, NearbyJoinDance);
                    return true;
                }
                if (!Plugin.Life.Has("emote." + command + ".self")) return true;

                Perform(command);
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Emote failed: " + e);
                return true;
            }
        }

        private static void ListEmotes()
        {
            var names = new List<string>();
            foreach (string section in Plugin.Life.SectionNames)
            {
                if (section.StartsWith("emote.") && section.EndsWith(".self"))
                {
                    names.Add("/" + section.Substring(6, section.Length - 11));
                }
            }
            names.Sort();
            names.Add("/dance");
            UpdateSocialLog.LogAdd("Emotes: " + string.Join(" ", names), "yellow");
        }

        private static void Perform(string command)
        {
            Character target = GameData.PlayerControl.CurrentTarget;
            Character me = GameData.PlayerControl.Myself;
            if (target == me || (target != null && Vector3.Distance(target.transform.position, SimLife.PlayerPos) > 25f))
            {
                target = null;
            }
            string targetName = target != null ? target.name : null;

            string self = Lines.Pick(target != null && Plugin.Life.Has("emote." + command + ".target")
                ? "emote." + command + ".target"
                : "emote." + command + ".self");
            Speech.PlayerEmote(Lines.Fill(self, new Dictionary<string, string> { ["target"] = targetName }));
            if (target != null) Speech.Face(GameData.PlayerControl, target.transform.position);
            Speech.Animate(me, Plugin.Life.PickFirst(Plugin.Rng, "emote." + command + ".anim"));

            if (target != null)
            {
                SimPlayer sim = target.GetComponent<SimPlayer>();
                if (sim != null)
                {
                    if (SimLife.Idle(sim)) SimRespond(sim, command, direct: true, delay: UnityEngine.Random.Range(0.8f, 2f));
                    return;
                }
                NPC npc = target.GetComponent<NPC>();
                if (npc != null && npc.GetComponent<NPCDialogManager>() != null && npc.CheckLiving() &&
                    npc.CurrentAggroTarget == null && !WorldContext.IsHostileToPlayer(npc))
                {
                    NpcRespond(npc, command);
                }
                return;
            }

            // Emoting at nobody in particular: a nearby sim or two might notice.
            float delay = UnityEngine.Random.Range(1f, 2f);
            int reacted = 0;
            foreach (SimPlayer sim in SimLife.NearbySims(10f, includeGroup: true))
            {
                if (reacted >= 2) break;
                if (!SimLife.Idle(sim) || Plugin.Rng.NextDouble() > 0.4) continue;
                SimRespond(sim, command, direct: false, delay: delay);
                delay += UnityEngine.Random.Range(1f, 2.5f);
                reacted++;
            }
        }

        private static void SimRespond(SimPlayer sim, string command, bool direct, float delay)
        {
            string persona = SimLife.PersonaOf(sim);
            string mood = SimLife.MoodOf(sim);
            var tokens = new Dictionary<string, string> { ["player"] = WorldContext.PlayerName };
            string action = Lines.Fill(Lines.Pick("emote." + command + ".react", persona, mood), tokens);
            string reply = direct || Plugin.Rng.NextDouble() < 0.4
                ? Lines.Fill(Lines.Pick("emote." + command + ".reply", persona, mood), tokens)
                : null;

            Scheduler.After(delay, () =>
            {
                if (!SimLife.Idle(sim)) return;
                SimLife.ActOut(sim, action, SimLife.PlayerPos);
                if (reply != null) Scheduler.After(UnityEngine.Random.Range(1f, 2f), () => Speech.Say(sim, reply));
            });
        }

        private static void NpcRespond(NPC npc, string command)
        {
            string action = Plugin.Life.PickFirst(Plugin.Rng, "emote." + command + ".npc", "emote.any.npc");
            string words = Plugin.Life.Has("emote." + command + ".npcsay") && Plugin.Rng.NextDouble() < 0.5
                ? Plugin.Life.PickFirst(Plugin.Rng, "emote." + command + ".npcsay")
                : null;
            Scheduler.After(UnityEngine.Random.Range(0.8f, 1.6f), () =>
            {
                if (npc == null || !npc.CheckLiving()) return;
                Speech.Face(npc, SimLife.PlayerPos);
                Speech.Emote(npc.NPCName, WorldContext.Fill(Speech.TakeAnimation(action, out _), npc));
                if (words != null) Scheduler.After(1.2f, () => Speech.NpcSay(npc, WorldContext.Fill(words, npc)));
            });
        }

        private static void NearbyJoinDance()
        {
            float delay = 0.5f;
            int joined = 0;
            foreach (SimPlayer sim in SimLife.NearbySims(12f, includeGroup: true))
            {
                if (joined >= 3) break;
                if (!SimLife.Idle(sim) || Plugin.Rng.NextDouble() > 0.5) continue;
                string action = Lines.Pick("emote.dance.react", SimLife.PersonaOf(sim), SimLife.MoodOf(sim));
                Scheduler.After(delay, () => { if (SimLife.Idle(sim)) SimLife.ActOut(sim, action ?? "[dance] joins the dance.", SimLife.PlayerPos); });
                delay += UnityEngine.Random.Range(0.8f, 2f);
                joined++;
            }
        }
    }

    /// <summary>When a sim greets you, it sometimes brings up something you went through together.</summary>
    [HarmonyPatch(typeof(SimPlayerLanguage), nameof(SimPlayerLanguage.GetTargetedHello))]
    internal static class MemoryGreetingPatch
    {
        private static void Postfix(SimPlayerTracking _sim, ref string __result)
        {
            try
            {
                if (!Plugin.EnableLife.Value || _sim == null || Plugin.Rng.NextDouble() > Plugin.MemoryChance.Value) return;
                Plugin.Bonds.UseCharacter(WorldContext.PlayerName);
                string memory = SimLife.MemoryLine(_sim.SimName, "memory.greet", _sim.MyAvatar);
                if (memory != null) __result = (__result ?? "").TrimEnd() + " " + memory;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Memory greeting failed: " + e);
            }
        }
    }
}
