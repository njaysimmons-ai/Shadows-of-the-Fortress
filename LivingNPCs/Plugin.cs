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
        public const string Version = "0.8.2";

        internal static ManualLogSource Log;
        internal static DialogueBank Bank;
        internal static NpcMemory Memory;
        internal static DialogueBank Life = new DialogueBank();
        internal static SimBonds Bonds;
        internal static DialogueBank Ai = new DialogueBank();
        /// <summary>The ai.txt built into the mod, used for any section missing from the player's copy.</summary>
        internal static DialogueBank AiDefaults = new DialogueBank();
        internal static Friendships Friends;

        /// <summary>The current character's friendships, loading them if the character changed.</summary>
        internal static Friendships FriendsNow()
        {
            Friends.UseCharacter(WorldContext.PlayerName);
            return Friends;
        }
        internal static readonly System.Random Rng = new System.Random();

        internal static ConfigEntry<bool> EnableGreetings;
        internal static ConfigEntry<bool> EnableAmbient;
        internal static ConfigEntry<float> HailActionChance;
        internal static ConfigEntry<float> FlavorChance;
        internal static ConfigEntry<int> RegularAfter;
        internal static ConfigEntry<double> AbsenceDays;
        internal static ConfigEntry<float> AmbientRadius;
        internal static ConfigEntry<float> AmbientMinSeconds;
        internal static ConfigEntry<float> AmbientMaxSeconds;
        internal static ConfigEntry<float> PerNpcCooldownSeconds;
        internal static ConfigEntry<bool> EnableSimVoices;
        internal static ConfigEntry<bool> SimVoicesForPremade;
        internal static ConfigEntry<float> SimPersonaShare;
        internal static ConfigEntry<float> SimSmallTalkShare;
        internal static ConfigEntry<bool> LogSimPersonas;
        internal static ConfigEntry<bool> EnableLife;
        internal static ConfigEntry<bool> EnableEmotes;
        internal static ConfigEntry<float> IdleMinSeconds;
        internal static ConfigEntry<float> IdleMaxSeconds;
        internal static ConfigEntry<float> BanterMinSeconds;
        internal static ConfigEntry<float> BanterMaxSeconds;
        internal static ConfigEntry<float> ReactionChance;
        internal static ConfigEntry<float> PostFightChance;
        internal static ConfigEntry<float> MemoryChance;
        internal static ConfigEntry<bool> AiEnabled;
        internal static ConfigEntry<string> AiModel;
        internal static ConfigEntry<string> AiUrl;
        internal static ConfigEntry<bool> AiAutoStart;
        internal static ConfigEntry<bool> AiAutoDownload;
        internal static ConfigEntry<string> AiOllamaPath;
        internal static ConfigEntry<bool> AiStopOllamaOnQuit;
        internal static ConfigEntry<float> AiTimeoutSeconds;
        internal static ConfigEntry<float> AiOverhearChance;
        internal static ConfigEntry<float> AiNearbyRadius;
        internal static ConfigEntry<bool> AiActions;
        internal static ConfigEntry<string> AiThinkingModel;
        internal static ConfigEntry<string> AiTone;
        internal static ConfigEntry<bool> AiCatchRedirects;
        internal static ConfigEntry<string> GuideStyle;
        internal static ConfigEntry<string> GuidePace;
        internal static ConfigEntry<bool> GuideAcrossZones;
        internal static ConfigEntry<float> GuideWaitDistance;
        internal static ConfigEntry<string> MistColor;
        internal static ConfigEntry<KeyboardShortcut> PanelKey;
        internal static ConfigEntry<bool> EnableFriendships;
        internal static ConfigEntry<float> FriendshipSpeed;
        internal static ConfigEntry<float> FriendshipDailyCap;
        internal static ConfigEntry<bool> FriendshipAffectsGame;
        internal static ConfigEntry<bool> FriendshipAnnounce;
        internal static ConfigEntry<bool> EnableFeelings;
        internal static ConfigEntry<float> FeelingsSpeed;
        internal static ConfigEntry<bool> BaldOption;
        internal static ConfigEntry<KeyboardShortcut> ReloadKey;

        /// <summary>The model for behind-the-scenes work: the thinking model if one is set and working, else the chat model.</summary>
        internal static string BrainModel =>
            !string.IsNullOrWhiteSpace(AiThinkingModel.Value) && Ollama.BrainOk ? AiThinkingModel.Value.Trim() : AiModel.Value;

        internal static bool TwoModels => BrainModel != AiModel.Value;

        internal static float GuidePaceFactor => GuidePace.Value == "Slow" ? 0.55f : GuidePace.Value == "Fast" ? 1f : 0.85f;

        private string dialoguePath;
        private string simsPath;
        private string lifePath;
        private string aiPath;

        private void Awake()
        {
            Log = Logger;

            EnableGreetings = Config.Bind("Greetings", "Enabled", true, "NPCs open their replies with a greeting that reacts to you.");
            FlavorChance = Config.Bind("Greetings", "FlavorChance", 0.35f, "Chance (0-1) a greeting also remarks on the time of day or your class.");
            RegularAfter = Config.Bind("Greetings", "RegularAfterHails", 10, "After this many conversations an NPC treats you as a regular.");
            AbsenceDays = Config.Bind("Greetings", "AbsenceDays", 3.0, "Real-world days away before an NPC remarks that it has been a while.");

            HailActionChance = Config.Bind("Greetings", "ActionChance", 0.45f, "Chance (0-1) an NPC does something (wipes the counter, looks up from a book) as they answer you.");
            EnableAmbient = Config.Bind("Ambient", "Enabled", true, "Friendly NPCs near you speak up on their own.");
            AmbientRadius = Config.Bind("Ambient", "Radius", 15f, "How close (in metres) an NPC must be for you to overhear it.");
            AmbientMinSeconds = Config.Bind("Ambient", "MinSecondsBetweenLines", 25f, "Shortest gap between overheard lines.");
            AmbientMaxSeconds = Config.Bind("Ambient", "MaxSecondsBetweenLines", 60f, "Longest gap between overheard lines.");
            PerNpcCooldownSeconds = Config.Bind("Ambient", "PerNpcCooldownSeconds", 150f, "How long one NPC stays quiet after speaking.");

            EnableSimVoices = Config.Bind("Sim Players", "Enabled", true, "Give each generated sim player its own persona: typing style and extra lines.");
            SimVoicesForPremade = Config.Bind("Sim Players", "AlsoChangeHandmadeSims", false, "Also give personas to the game's hand-written sims (they already have their own voices).");
            SimPersonaShare = Config.Bind("Sim Players", "PersonaShare", 0.6f, "Roughly what fraction (0-1) of a sim's lines come from its persona rather than the game's shared pool.");
            SimSmallTalkShare = Config.Bind("Sim Players", "SmallTalkShare", 0.6f, "Chance (0-1) a sim's small-talk shout uses its own lines instead of the zone's shared comments.");
            LogSimPersonas = Config.Bind("Sim Players", "LogPersonas", true, "Write each sim's persona to the BepInEx log as it spawns.");

            EnableLife = Config.Bind("Living World", "Enabled", true, "Sims act on their own, react to deaths, wipes and level-ups, banter in groups, and remember your adventures.");
            EnableEmotes = Config.Bind("Living World", "EmoteCommands", true, "Adds /wave, /bow, /cheer and other emotes that sims and NPCs react to. Type /emotes for the list.");
            IdleMinSeconds = Config.Bind("Living World", "IdleActionMinSeconds", 15f, "Shortest gap between sims near you doing something on their own.");
            IdleMaxSeconds = Config.Bind("Living World", "IdleActionMaxSeconds", 40f, "Longest gap between sims near you doing something on their own.");
            BanterMinSeconds = Config.Bind("Living World", "GroupBanterMinSeconds", 90f, "Shortest gap between group chatter while out of combat.");
            BanterMaxSeconds = Config.Bind("Living World", "GroupBanterMaxSeconds", 200f, "Longest gap between group chatter while out of combat.");
            ReactionChance = Config.Bind("Living World", "ReactionChance", 0.6f, "Chance (0-1) each groupmate reacts when someone dies.");
            PostFightChance = Config.Bind("Living World", "PostFightChance", 0.3f, "Chance (0-1) someone comments after a fight.");
            MemoryChance = Config.Bind("Living World", "MemoryGreetingChance", 0.4f, "Chance (0-1) a sim's greeting mentions something you went through together.");

            AiEnabled = Config.Bind("AI Chat", "Enabled", true, "Talk to sim players in your own words, answered by a local AI (Ollama). NPCs stay scripted. Toggle in game with /ai on or /ai off.");
            AiModel = Config.Bind("AI Chat", "Model", "llama3.2:3b", "Ollama model to use. llama3.2:3b (2 GB) suits most PCs; try llama3.1:8b or qwen2.5:7b on a strong GPU.");
            AiUrl = Config.Bind("AI Chat", "OllamaUrl", "http://127.0.0.1:11434", "Where Ollama is listening.");
            AiAutoStart = Config.Bind("AI Chat", "StartOllamaAutomatically", true, "Launch Ollama in the background when the game starts, if it's installed and not already running.");
            AiAutoDownload = Config.Bind("AI Chat", "DownloadModelAutomatically", true, "Download the model on first use if it's missing.");
            AiOllamaPath = Config.Bind("AI Chat", "OllamaPath", "", "Full path to ollama.exe, if it isn't in the usual place.");
            AiStopOllamaOnQuit = Config.Bind("AI Chat", "StopOllamaOnQuit", true, "Close Ollama when you quit the game, if the mod started it.");
            AiTimeoutSeconds = Config.Bind("AI Chat", "ReplyTimeoutSeconds", 120f, "Give up on a reply after this long.");
            AiActions = Config.Bind("AI Chat", "SimsActOnWhatTheySay", true, "Sims do what they agree to in AI chat: join your group, meet you in a zone, come find you, leave, or emote.");
            AiNearbyRadius = Config.Bind("AI Chat", "NearbyAnswerRadius", 6f, "If you talk without targeting anyone, the closest sim within this many metres answers. 0 turns it off.");
            AiOverhearChance = Config.Bind("AI Chat", "OverhearChance", 0.35f, "Chance (0-1) a nearby sim answers a question you ask out loud without naming anyone.");
            AiThinkingModel = Config.Bind("AI Chat", "ThinkingModel", "",
                "Optional second model for the behind-the-scenes work: deciding what sims do (guide, join, buff...) and writing their memories. " +
                "Leave empty to use Model for everything. A sharp, small model such as qwen2.5:7b works well here, so Model can be a livelier one like dolphin3:8b.");
            AiTone = Config.Bind("AI Chat", "Tone", "Normal", new ConfigDescription(
                "How openly sims show feelings. Normal: friendly MMO chat. Emotional: open about crushes, love, jealousy and hurt. Edgy: also blunt, sarcastic, petty and willing to swear.",
                new AcceptableValueList<string>("Normal", "Emotional", "Edgy")));
            AiCatchRedirects = Config.Bind("AI Chat", "RerollChangedSubjects", true,
                "If a sim dodges what you said with a generic \"anyway, let's focus on the next raid\", ask the AI once more for a reply that actually answers you.");

            EnableFriendships = Config.Bind("Friendships", "Enabled", true, "Sims and NPCs build lasting friendships with each of your characters and remember what you've done and said together.");
            FriendshipSpeed = Config.Bind("Friendships", "Speed", 1f, "Multiplier on how fast friendships grow (and shrink).");
            FriendshipDailyCap = Config.Bind("Friendships", "DailyGainCap", 12f, "Most friendship one character can gain with you per real day, so bonds build over time.");
            FriendshipAffectsGame = Config.Bind("Friendships", "AffectsSimOpinion", true, "Raise a sim's in-game opinion of you as your friendship grows (the game uses it for grouping and guild invites).");
            EnableFeelings = Config.Bind("Friendships", "EvolvingFeelings", true, "Sims develop feelings about you over time (affection, attraction, respect, trust, envy, resentment) from what you say and do: they can fall for you, envy you or come to hate you.");
            FeelingsSpeed = Config.Bind("Friendships", "FeelingsSpeed", 1f, "Multiplier on how fast feelings change.");
            FriendshipAnnounce = Config.Bind("Friendships", "AnnounceNewTiers", true, "Show a message when you and someone become Friends, Good Friends, and so on.");

            GuideStyle = Config.Bind("Guiding", "Style", "Walk", new ConfigDescription(
                "How sims show you the way. Walk: the sim walks you there. WalkAndMist: the sim walks you there and a mist trail marks the path. Mist: in the same zone, the sim conjures a mist trail instead of walking (other zones still get walked to the exit).",
                new AcceptableValueList<string>("Walk", "WalkAndMist", "Mist")));
            GuidePace = Config.Bind("Guiding", "Pace", "Normal", new ConfigDescription("How fast a guiding sim moves.", new AcceptableValueList<string>("Slow", "Normal", "Fast")));
            GuideAcrossZones = Config.Bind("Guiding", "AcrossZones", true, "Sims may guide you through zone lines to an NPC or zone somewhere else.");
            GuideWaitDistance = Config.Bind("Guiding", "WaitIfYouFallBehind", 14f, "A guiding sim stops and waits when you're this many metres behind. 0 means never wait.");
            MistColor = Config.Bind("Guiding", "MistColor", "Blue", new ConfigDescription("Colour of the /find and guiding mist.",
                new AcceptableValueList<string>("Blue", "Gold", "Green", "Violet", "White", "Red")));
            PanelKey = Config.Bind("General", "PanelKey", new KeyboardShortcut(KeyCode.F9, KeyCode.LeftControl), "Opens the Living NPCs panel (the same as typing /ai).");

            BaldOption = Config.Bind("Appearance", "BaldHairstyle", true, "Adds a Bald hairstyle at the end of the character creation hair choices, and the /hair command.");

            ReloadKey = Config.Bind("General", "ReloadDialogueKey", new KeyboardShortcut(KeyCode.F8, KeyCode.LeftControl),
                "Reloads dialogue.txt without restarting the game.");

            string folder = Path.Combine(Paths.ConfigPath, "LivingNPCs");
            Directory.CreateDirectory(folder);
            dialoguePath = Path.Combine(folder, "dialogue.txt");
            simsPath = Path.Combine(folder, "sims.txt");
            lifePath = Path.Combine(folder, "life.txt");
            aiPath = Path.Combine(folder, "ai.txt");
            Memory = new NpcMemory(Path.Combine(folder, "memory"));
            Bonds = new SimBonds(Path.Combine(folder, "memory"));
            Friends = new Friendships(Path.Combine(folder, "memory"));
            WorldIndex.Load(Path.Combine(folder, "memory"));
            Friends.TierReached += FriendshipEvents.OnTierReached;
            Friendships.IsGuildmate = key => key.StartsWith("guild:") ||
                                             (key.StartsWith("sim:") && FriendshipEvents.IsGuildmate(Friendships.NameOf(key)));
            LoadDialogue();

            Instance = this;
            new Harmony(Guid).PatchAll(typeof(Plugin).Assembly);
            Host.Ensure();
            Log.LogInfo("Living NPCs " + Version + " loaded.");
        }

        internal static Plugin Instance;

        internal void ReloadFromKey()
        {
            LoadDialogue();
            UpdateSocialLog.LogAdd("Living NPCs: reloaded " + Bank.LineCount + " NPC lines and " + SimVoices.PersonaCount +
                                   " sim personas. Personas apply to sims as they next spawn.", "yellow");
        }

        internal static void SaveAll()
        {
            try
            {
                Memory.Save();
                Bonds.Save();
                Friends.Save();
                WorldIndex.Save();
            }
            catch (Exception e)
            {
                Log.LogError("Saving Living NPCs data failed: " + e);
            }
        }

        private void LoadDialogue()
        {
            Bank = LoadText(dialoguePath, "dialogue.txt") ?? Bank ?? new DialogueBank();
            Life = LoadText(lifePath, "life.txt") ?? Life;
            AiDefaults = LoadEmbedded("ai.txt") ?? AiDefaults;
            Ai = LoadText(aiPath, "ai.txt") ?? Ai;
            RepairAiFile();
            DialogueBank sims = LoadText(simsPath, "sims.txt");
            if (sims != null)
            {
                SimVoices.Load(sims);
                Log.LogInfo("Loaded " + SimVoices.PersonaCount + " sim personas.");
            }
        }

        private static DialogueBank LoadEmbedded(string resource)
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("LivingNPCs." + resource))
            {
                if (stream == null) return null;
                using (var reader = new StreamReader(stream)) return DialogueBank.Parse(reader);
            }
        }

        /// <summary>
        /// Version 0.6.0 shipped an ai.txt with its personality, lore and memory sections cut off. If the
        /// player's copy has no [ai.sim] section, keep it as a backup and put the full default back.
        /// </summary>
        private void RepairAiFile()
        {
            if (Ai.Has("ai.sim")) return;
            try
            {
                string backup = aiPath + ".damaged-backup";
                if (File.Exists(backup)) File.Delete(backup);
                if (File.Exists(aiPath)) File.Move(aiPath, backup);
                Ai = LoadText(aiPath, "ai.txt") ?? Ai;
                Log.LogWarning("ai.txt was missing its personality section; restored the default (old copy kept as ai.txt.damaged-backup).");
            }
            catch (Exception e)
            {
                Log.LogError("Couldn't repair ai.txt: " + e);
            }
        }

        /// <summary>Loads a text file from the config folder, first writing the built-in default if it's missing.</summary>
        private DialogueBank LoadText(string path, string resource)
        {
            try
            {
                if (!File.Exists(path))
                {
                    using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("LivingNPCs." + resource))
                    using (var reader = new StreamReader(stream))
                    {
                        File.WriteAllText(path, reader.ReadToEnd());
                    }
                    Log.LogInfo("Wrote default " + resource + " to " + path);
                }
                DialogueBank bank = DialogueBank.Load(path);
                Log.LogInfo("Loaded " + bank.LineCount + " lines from " + resource + ".");
                return bank;
            }
            catch (Exception e)
            {
                Log.LogError("Could not load " + path + ": " + e);
                return null;
            }
        }
    }
}
