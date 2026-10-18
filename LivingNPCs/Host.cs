using System;
using HarmonyLib;
using UnityEngine;

namespace LivingNPCs
{
    /// <summary>
    /// Owns everything in the mod that runs every frame (timers, chatter, sim life, AI chat).
    ///
    /// In some newer Unity games the object BepInEx gives plugins is destroyed when a scene loads, which
    /// silently stops every Update() while Harmony patches keep working. So the mod runs on its own hidden
    /// object instead, and a watchdog on the game's chat box (which updates every frame) recreates it if it
    /// ever disappears.
    /// </summary>
    internal class Host : MonoBehaviour
    {
        private static GameObject root;
        private static int starts;

        public static void Ensure()
        {
            if (root != null) return;
            starts++;
            root = new GameObject("LivingNPCs.Host") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(root);
            root.AddComponent<Host>();
            root.AddComponent<Scheduler>();
            root.AddComponent<AmbientChatter>();
            root.AddComponent<SimLife>();
            root.AddComponent<AIChat>();
            root.AddComponent<MistTrail>();
            root.AddComponent<AiPanel>();
            if (starts == 1) Plugin.Log.LogInfo("Living NPCs: update loop started.");
            else Plugin.Log.LogWarning("Living NPCs: the update loop was destroyed by the game and has been restarted (" + starts + " starts).");
        }

        /// <summary>Time of the last frame the host ran, for /ai's health check.</summary>
        public static float LastBeat = -1f;
        public static int Starts => starts;

        private void Update()
        {
            LastBeat = Time.realtimeSinceStartup;
            if (Plugin.ReloadKey.Value.IsDown()) Plugin.Instance.ReloadFromKey();
        }

        private void OnApplicationQuit() => Plugin.SaveAll();

        private void OnApplicationPause(bool paused)
        {
            if (paused) Plugin.SaveAll();
        }

        /// <summary>The chat box updates every frame in game, so it's a reliable place to check the host is alive.</summary>
        [HarmonyPatch(typeof(TypeText), "Update")]
        private static class Watchdog
        {
            private static void Postfix()
            {
                try { Ensure(); }
                catch (Exception e) { Plugin.Log.LogError("Couldn't restart the update loop: " + e); }
            }
        }
    }
}
