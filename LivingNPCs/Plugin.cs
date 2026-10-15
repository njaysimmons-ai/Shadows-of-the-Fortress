using System;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace LivingNPCs
{
    [BepInPlugin(Guid, "Living NPCs", Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "njaysimmons.erenshor.livingnpcs";
        public const string Version = "0.1.0";

        internal static ManualLogSource Log;
        internal static DialogueBank Bank;
        internal static NpcMemory Memory;
        internal static readonly System.Random Rng = new System.Random();

        internal static ConfigEntry<bool> EnableGreetings;
        internal static ConfigEntry<bool> EnableAmbient;
        internal static ConfigEntry<float> FlavorChance;
        internal static ConfigEntry<int> RegularAfter;
        internal static ConfigEntry<double> AbsenceDays;
        internal static ConfigEntry<float> AmbientRadius;
        internal static ConfigEntry<float> AmbientMinSeconds;
        internal static ConfigEntry<float> AmbientMaxSeconds;
        internal static ConfigEntry<float> PerNpcCooldownSeconds;
        internal static ConfigEntry<KeyboardShortcut> ReloadKey;

        private string dialoguePath;

        private void Awake()
        {
            Log = Logger;

            EnableGreetings = Config.Bind("Greetings", "Enabled", true, "NPCs open their replies with a greeting that reacts to you.");
            FlavorChance = Config.Bind("Greetings", "FlavorChance", 0.35f, "Chance (0-1) a greeting also remarks on the time of day or your class.");
            RegularAfter = Config.Bind("Greetings", "RegularAfterHails", 10, "After this many conversations an NPC treats you as a regular.");
            AbsenceDays = Config.Bind("Greetings", "AbsenceDays", 3.0, "Real-world days away before an NPC remarks that it has been a while.");

            EnableAmbient = Config.Bind("Ambient", "Enabled", true, "Friendly NPCs near you speak up on their own.");
            AmbientRadius = Config.Bind("Ambient", "Radius", 15f, "How close (in metres) an NPC must be for you to overhear it.");
            AmbientMinSeconds = Config.Bind("Ambient", "MinSecondsBetweenLines", 25f, "Shortest gap between overheard lines.");
            AmbientMaxSeconds = Config.Bind("Ambient", "MaxSecondsBetweenLines", 60f, "Longest gap between overheard lines.");
            PerNpcCooldownSeconds = Config.Bind("Ambient", "PerNpcCooldownSeconds", 150f, "How long one NPC stays quiet after speaking.");

            ReloadKey = Config.Bind("General", "ReloadDialogueKey", new KeyboardShortcut(KeyCode.F8, KeyCode.LeftControl),
                "Reloads dialogue.txt without restarting the game.");

            string folder = Path.Combine(Paths.ConfigPath, "LivingNPCs");
            Directory.CreateDirectory(folder);
            dialoguePath = Path.Combine(folder, "dialogue.txt");
            Memory = new NpcMemory(Path.Combine(folder, "memory"));
            LoadDialogue();

            new Harmony(Guid).PatchAll(typeof(Plugin).Assembly);
            gameObject.AddComponent<AmbientChatter>();
            Log.LogInfo("Living NPCs " + Version + " loaded.");
        }

        private void Update()
        {
            if (ReloadKey.Value.IsDown())
            {
                LoadDialogue();
                UpdateSocialLog.LogAdd("Living NPCs: reloaded " + Bank.LineCount + " lines of dialogue.", "yellow");
            }
        }

        private void OnApplicationQuit()
        {
            Memory.Save();
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused) Memory.Save();
        }

        private void LoadDialogue()
        {
            try
            {
                if (!File.Exists(dialoguePath))
                {
                    using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("LivingNPCs.dialogue.txt"))
                    using (var reader = new StreamReader(stream))
                    {
                        File.WriteAllText(dialoguePath, reader.ReadToEnd());
                    }
                    Log.LogInfo("Wrote default dialogue to " + dialoguePath);
                }
                Bank = DialogueBank.Load(dialoguePath);
                Log.LogInfo("Loaded " + Bank.LineCount + " lines of dialogue.");
            }
            catch (Exception e)
            {
                Log.LogError("Could not load " + dialoguePath + ": " + e);
                Bank = Bank ?? new DialogueBank();
            }
        }
    }
}
