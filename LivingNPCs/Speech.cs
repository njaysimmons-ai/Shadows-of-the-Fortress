using System;
using System.Collections.Generic;
using UnityEngine;

namespace LivingNPCs
{
    /// <summary>Writes lines to the chat log in the same formats the game uses, and plays simple animations.</summary>
    public static class Speech
    {
        public const string EmoteColor = "#E8C77A";
        private const string PartyColor = "#00B2B7";
        private const string ShoutColor = "#FF9000";

        /// <summary>"Bob waves at you." Emotes are never run through typing quirks.</summary>
        public static void Emote(string who, string action)
        {
            if (string.IsNullOrEmpty(action)) return;
            UpdateSocialLog.LogAdd(new ChatLogLine(who + " " + action, ChatLogLine.LogType.NPCEmotes, EmoteColor));
        }

        public static void PlayerEmote(string text)
        {
            UpdateSocialLog.LogAdd(new ChatLogLine(text, ChatLogLine.LogType.NPCEmotes, EmoteColor));
        }

        public static void Say(SimPlayer sim, string text) =>
            Log(sim, " says: ", text, ChatLogLine.LogType.Say, "");

        public static void Party(SimPlayer sim, string text) =>
            Log(sim, " tells the group: ", text, ChatLogLine.LogType.Party, PartyColor);

        public static void Shout(SimPlayer sim, string text) =>
            Log(sim, " shouts: ", text, ChatLogLine.LogType.Shout, ShoutColor);

        public static void NpcSay(NPC npc, string text)
        {
            if (npc == null || string.IsNullOrEmpty(text)) return;
            UpdateSocialLog.LogAdd(new ChatLogLine(npc.NPCName + " says: " + text, ChatLogLine.LogType.Say));
        }

        private static void Log(SimPlayer sim, string verb, string text, ChatLogLine.LogType type, string color)
        {
            if (sim == null || string.IsNullOrEmpty(text)) return;
            // The game's own pass adds each sim's typos, caps, emojis and sign-offs.
            string typed = GameData.SimMngr != null ? GameData.SimMngr.PersonalizeString(text, sim) : text;
            UpdateSocialLog.LogAdd(new ChatLogLine(sim.name + verb + typed, type, color));
        }

        /// <summary>Lines may start with [dance], [sit], [flourish] or [kneel]; this strips the tag and plays it.</summary>
        public static string TakeAnimation(string line, out string animation)
        {
            animation = null;
            if (line != null && line.StartsWith("["))
            {
                int end = line.IndexOf(']');
                if (end > 0)
                {
                    animation = line.Substring(1, end - 1).Trim().ToLowerInvariant();
                    line = line.Substring(end + 1).TrimStart();
                }
            }
            return line;
        }

        public static void Animate(Character who, string animation)
        {
            Animator anim = who != null ? who.GetMyAnim() : null;
            if (anim == null || string.IsNullOrEmpty(animation)) return;
            switch (animation)
            {
                case "dance":
                    anim.SetBool("Dancing", true);
                    Scheduler.After(UnityEngine.Random.Range(8f, 16f), () => { if (anim != null) anim.SetBool("Dancing", false); });
                    break;
                case "sit":
                    anim.ResetTrigger("StandUp");
                    anim.SetTrigger("SitDown");
                    Scheduler.After(UnityEngine.Random.Range(10f, 25f), () => { if (anim != null) anim.SetTrigger("StandUp"); });
                    break;
                case "flourish":
                    anim.SetTrigger("SkillBuff");
                    break;
                case "kneel":
                    anim.SetTrigger("StartLoot");
                    Scheduler.After(2.5f, () => { if (anim != null) anim.SetTrigger("EndLoot"); });
                    break;
            }
        }

        /// <summary>Turns a character to look at a point, keeping it upright.</summary>
        public static void Face(Component who, Vector3 point)
        {
            if (who == null) return;
            Vector3 dir = point - who.transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.01f)
            {
                who.transform.rotation = Quaternion.LookRotation(dir);
            }
        }
    }

    /// <summary>Runs small delayed actions so reactions don't all land in the same frame.</summary>
    public class Scheduler : MonoBehaviour
    {
        private static readonly List<KeyValuePair<float, Action>> Pending = new List<KeyValuePair<float, Action>>();

        public static void After(float seconds, Action action)
        {
            Pending.Add(new KeyValuePair<float, Action>(Time.time + seconds, action));
        }

        private void Update()
        {
            float now = Time.time;
            for (int i = Pending.Count - 1; i >= 0; i--)
            {
                if (Pending[i].Key > now) continue;
                Action action = Pending[i].Value;
                Pending.RemoveAt(i);
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError("Delayed action failed: " + e);
                }
            }
        }
    }
}
