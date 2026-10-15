using System;
using System.Collections.Generic;
using UnityEngine;

namespace LivingNPCs
{
    /// <summary>
    /// Lets friendly NPCs near the player speak up on their own: a vendor calling out wares,
    /// a guard muttering about the night shift, a townsperson noticing the weather.
    /// Lines go to the chat log as ordinary /say text.
    /// </summary>
    public class AmbientChatter : MonoBehaviour
    {
        private const float RescanSeconds = 15f;

        private readonly List<NPC> talkers = new List<NPC>();
        private readonly Dictionary<NPC, float> nextAllowed = new Dictionary<NPC, float>();
        private float nextRescan;
        private float nextLine;
        private string scannedZone;

        private void Update()
        {
            if (!Plugin.EnableAmbient.Value || !WorldContext.PlayerReady || GameData.InCombat)
            {
                return;
            }
            float now = Time.time;
            string zone = WorldContext.Zone;
            if (zone != scannedZone || now >= nextRescan)
            {
                Rescan(zone, now);
            }
            if (now < nextLine)
            {
                return;
            }
            nextLine = now + RandomRange(Plugin.AmbientMinSeconds.Value, Plugin.AmbientMaxSeconds.Value);

            try
            {
                SpeakOnce(now);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ambient chatter failed: " + e);
            }
        }

        private void Rescan(string zone, float now)
        {
            if (zone != scannedZone)
            {
                scannedZone = zone;
                nextAllowed.Clear();
                Plugin.Memory.Save();
                SimVoices.ForgetDestroyed();
                // Give the player a moment to arrive before anyone starts talking.
                nextLine = now + Plugin.AmbientMinSeconds.Value;
                Plugin.Log.LogInfo("Zone is '" + zone + "'. Use [ambient.zone." + zone + "] in dialogue.txt for lines here.");
            }
            nextRescan = now + RescanSeconds;
            talkers.Clear();
            foreach (NPCDialogManager manager in FindObjectsOfType<NPCDialogManager>())
            {
                NPC npc = manager.GetComponent<NPC>();
                if (npc != null && !npc.SimPlayer && !string.IsNullOrEmpty(npc.NPCName))
                {
                    talkers.Add(npc);
                }
            }
        }

        private void SpeakOnce(float now)
        {
            Vector3 playerPos = GameData.PlayerControl.transform.position;
            float radius = Plugin.AmbientRadius.Value;
            var nearby = new List<NPC>();
            foreach (NPC npc in talkers)
            {
                if (npc == null || !npc.isActiveAndEnabled || !npc.CheckLiving() || npc.CurrentAggroTarget != null)
                {
                    continue;
                }
                if (nextAllowed.TryGetValue(npc, out float allowedAt) && now < allowedAt)
                {
                    continue;
                }
                if (WorldContext.IsHostileToPlayer(npc))
                {
                    continue;
                }
                if (Vector3.Distance(npc.transform.position, playerPos) <= radius)
                {
                    nearby.Add(npc);
                }
            }
            if (nearby.Count == 0)
            {
                return;
            }

            NPC speaker = nearby[Plugin.Rng.Next(nearby.Count)];
            bool close = Vector3.Distance(speaker.transform.position, playerPos) <= radius * 0.4f;
            string line = PickLine(speaker, close);
            if (line == null)
            {
                return;
            }
            nextAllowed[speaker] = now + Plugin.PerNpcCooldownSeconds.Value;
            line = WorldContext.Fill(Speech.TakeAnimation(line, out _), speaker);
            // Lines written as *does something* are actions, shown as emotes rather than speech.
            if (line.StartsWith("*") && line.EndsWith("*") && line.Length > 2)
            {
                string action = line.Substring(1, line.Length - 2).Trim();
                if (!action.EndsWith(".") && !action.EndsWith("!") && !action.EndsWith("?")) action += ".";
                Speech.Emote(speaker.NPCName, action);
                return;
            }
            UpdateSocialLog.LogAdd(new ChatLogLine(speaker.NPCName + " says: " + line, ChatLogLine.LogType.Say));
        }

        private static string PickLine(NPC npc, bool closeToPlayer)
        {
            DialogueBank bank = Plugin.Bank;
            System.Random rng = Plugin.Rng;
            string name = npc.NPCName;

            // Characters with their own lines nearly always use them.
            if (bank.Has("npc." + name + ".ambient") && rng.NextDouble() < 0.85)
            {
                return bank.PickFirst(rng, "npc." + name + ".ambient");
            }
            if (closeToPlayer && WorldContext.PlayerHurt && bank.Has("ambient.player_hurt") && rng.NextDouble() < 0.6)
            {
                return bank.PickFirst(rng, "ambient.player_hurt");
            }
            if (closeToPlayer && bank.Has("ambient.to_player") && rng.NextDouble() < 0.3)
            {
                return bank.PickFirst(rng, "ambient.to_player");
            }

            string role = WorldContext.RoleOf(npc).ToString().ToLowerInvariant();
            string time = WorldContext.TimeOfDay;
            string zone = WorldContext.Zone;

            // Sometimes they do something instead of saying something.
            if (rng.NextDouble() < 0.4)
            {
                string action = bank.PickAny(rng, new[] { "action." + role, "action." + role + "." + time, "action.zone." + zone, "action" });
                if (action != null) return "*" + action.TrimEnd('.') + "*";
            }
            return bank.PickAny(rng, new[]
            {
                "ambient.zone." + zone,
                "ambient.zone." + zone + "." + time,
                "ambient." + role,
                "ambient." + role + "." + time,
                "ambient.time." + time,
                "ambient",
            });
        }

        private static float RandomRange(float min, float max)
        {
            return max <= min ? min : UnityEngine.Random.Range(min, max);
        }
    }
}
