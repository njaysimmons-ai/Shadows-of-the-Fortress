using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace LivingNPCs
{
    /// <summary>
    /// The Living NPCs settings window, opened with /ai (or Ctrl+F9): which AI models sims talk and think
    /// with, their tone, and how they guide you. Every choice is saved to the config straight away.
    /// </summary>
    internal class AiPanel : MonoBehaviour
    {
        private class Suggestion
        {
            public string Name, Note;
            public double Gb;
            public Suggestion(string name, double gb, string note) { Name = name; Gb = gb; Note = note; }
        }

        // Models worth trying, with roughly how much video memory each needs.
        private static readonly Suggestion[] Suggestions =
        {
            new Suggestion("qwen2.5:14b", 9.0, "smartest here, but cautious about romance and conflict"),
            new Suggestion("mistral-nemo", 7.1, "warm, playful roleplay; a great chat model"),
            new Suggestion("hermes3:8b", 4.7, "expressive, built for characters"),
            new Suggestion("dolphin3:8b", 4.9, "fewest refusals, edgiest; weaker at actions and memories"),
            new Suggestion("qwen2.5:7b", 4.7, "sharp and fast: the best thinking model"),
            new Suggestion("llama3.1:8b", 4.9, "solid all-rounder"),
            new Suggestion("llama3.2:3b", 2.0, "light, for weaker PCs"),
        };

        private static readonly string[] Tones = { "Normal", "Emotional", "Edgy" };
        private static readonly string[] ToneNotes =
        {
            "Friendly MMO chat.",
            "Open about crushes, love, jealousy and hurt.",
            "Also blunt, sarcastic, petty, and willing to swear.",
        };
        private static readonly string[] Styles = { "Walk", "WalkAndMist", "Mist" };
        private static readonly string[] StyleLabels = { "Sim walks you", "Walk + mist", "Mist only" };
        private static readonly string[] StyleNotes =
        {
            "The sim walks you there, through zone lines if needed.",
            "The sim walks you there and a mist trail marks the path.",
            "In this zone the sim conjures a mist trail instead of walking. Other zones: walked to the exit.",
        };
        private static readonly string[] Paces = { "Slow", "Normal", "Fast" };
        private static readonly float[] WaitDistances = { 8f, 14f, 25f, 0f };
        private static readonly string[] WaitLabels = { "Close (8m)", "Normal (14m)", "Far (25m)", "Never" };
        private static readonly string[] Colors = { "Blue", "Gold", "Green", "Violet", "White", "Red" };

        private static bool open;
        private static AiPanel instance;
        private Rect window = new Rect(0, 0, 560, 640);
        private bool placed;
        private Vector2 scroll;
        private int tab;
        private string customModel = "";
        private bool weSetTyping;

        private List<Ollama.ModelInfo> installed = new List<Ollama.ModelInfo>();
        private List<Ollama.ModelInfo> loaded = new List<Ollama.ModelInfo>();
        private bool listing;
        private bool reachable = true;
        private float nextList;

        private GUIStyle windowStyle, header, label, small, selected, normal, title;
        private Texture2D background, buttonOn, buttonOff, buttonHover;

        public static void Toggle()
        {
            open = !open;
            if (open && instance != null) instance.nextList = 0f;
        }

        public static void Close() => open = false;

        private void Awake() { instance = this; }

        private void Update()
        {
            try
            {
                if (Plugin.PanelKey.Value.IsDown()) Toggle();
                if (open && Time.unscaledTime >= nextList && !listing)
                {
                    listing = true;
                    nextList = Time.unscaledTime + 4f;
                    Ollama.ListModels((i, l) =>
                    {
                        listing = false;
                        reachable = i != null;
                        installed = i ?? new List<Ollama.ModelInfo>();
                        loaded = l ?? new List<Ollama.ModelInfo>();
                    });
                }
                // Typing in the panel's text box mustn't walk your character around.
                bool typing = open && GUIUtility.keyboardControl != 0;
                if (typing && !GameData.PlayerTyping) { GameData.PlayerTyping = true; weSetTyping = true; }
                else if (!typing && weSetTyping) { GameData.PlayerTyping = false; weSetTyping = false; }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("AI panel update failed: " + e.Message);
            }
        }

        private void OnGUI()
        {
            if (!open) return;
            try
            {
                Styles_();
                float scale = Mathf.Clamp(Screen.height / 1080f, 1f, 2.5f);
                Matrix4x4 before = GUI.matrix;
                GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));
                float w = Screen.width / scale, h = Screen.height / scale;
                if (!placed)
                {
                    placed = true;
                    window.height = Mathf.Min(680f, h - 40f);
                    window.x = (w - window.width) / 2f;
                    window.y = (h - window.height) / 2f;
                }
                window = GUI.Window(0x4C4E50, window, Draw, "", windowStyle);
                window.x = Mathf.Clamp(window.x, 0f, w - window.width);
                window.y = Mathf.Clamp(window.y, 0f, h - 40f);
                GUI.matrix = before;

                Event e = Event.current;
                if (e != null && e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
                {
                    open = false;
                    GUIUtility.keyboardControl = 0;
                    e.Use();
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("AI panel failed to draw: " + ex);
                open = false;
            }
        }

        // ---------------------------------------------------------------- drawing

        private void Draw(int id)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Living NPCs " + Plugin.Version, title);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("X", normal, GUILayout.Width(30), GUILayout.Height(26))) open = false;
            GUILayout.EndHorizontal();
            StatusLine();
            GUILayout.Space(6);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("AI models", tab == 0 ? selected : normal, GUILayout.Height(28))) tab = 0;
            if (GUILayout.Button("Personality", tab == 1 ? selected : normal, GUILayout.Height(28))) tab = 1;
            if (GUILayout.Button("Guiding", tab == 2 ? selected : normal, GUILayout.Height(28))) tab = 2;
            if (GUILayout.Button("People", tab == 3 ? selected : normal, GUILayout.Height(28))) tab = 3;
            GUILayout.EndHorizontal();
            GUILayout.Space(6);

            scroll = GUILayout.BeginScrollView(scroll);
            if (tab == 0) ModelsTab();
            else if (tab == 1) PersonalityTab();
            else if (tab == 2) GuidingTab();
            else PeopleTab();
            GUILayout.EndScrollView();

            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Test AI", normal, GUILayout.Height(28))) AIChat.Test();
            if (GUILayout.Button(AIChat.Debug ? "Debug: ON" : "Debug: off", AIChat.Debug ? selected : normal, GUILayout.Height(28))) AIChat.Debug = !AIChat.Debug;
            if (GUILayout.Button(Plugin.AiEnabled.Value ? "AI chat: ON" : "AI chat: OFF", Plugin.AiEnabled.Value ? selected : normal, GUILayout.Height(28)))
            {
                Plugin.AiEnabled.Value = !Plugin.AiEnabled.Value;
                if (Plugin.AiEnabled.Value) AIChat.Prepare(quiet: false);
            }
            if (GUILayout.Button("Close", normal, GUILayout.Height(28))) open = false;
            GUILayout.EndHorizontal();
            GUILayout.Label("Esc or /ai closes this. Ctrl+F9 opens it too. Drag the title to move it.", small);

            GUI.DragWindow(new Rect(0, 0, 10000, 34));
        }

        private void StatusLine()
        {
            string state;
            if (!Plugin.AiEnabled.Value) state = "<color=#B0B0B0>AI chat is off.</color> Sims use their written lines.";
            else if (AIChat.Ready) state = "<color=#7FD1AE>Connected.</color> Talking with <b>" + Plugin.AiModel.Value + "</b>" +
                                          (Plugin.TwoModels ? ", thinking with <b>" + Plugin.BrainModel + "</b>." : ".");
            else state = "<color=#F2C94C>Not ready:</color> " + (string.IsNullOrEmpty(Ollama.LastError) ? Ollama.Phase : Ollama.LastError);
            GUILayout.Label(state, label);
            if (loaded.Count > 0)
            {
                double total = loaded.Sum(m => m.Gb);
                GUILayout.Label("In video memory: " + string.Join(", ", loaded.Select(m => m.Name + " (" + m.Gb.ToString("0.0") + " GB)")) +
                                (loaded.Count > 1 ? "  = " + total.ToString("0.0") + " GB" : ""), small);
            }
            else if (!reachable) GUILayout.Label("Can't reach Ollama right now, so the model list may be empty.", small);
        }

        private void ModelsTab()
        {
            GUILayout.Label("Chat model", header);
            GUILayout.Label("Writes everything sims say to you.", small);
            ModelChoices(Plugin.AiModel.Value, name => AIChat.SetChatModel(name), allowSame: false);

            GUILayout.Space(10);
            GUILayout.Label("Thinking model", header);
            GUILayout.Label("Behind the scenes: decides what sims do when you ask (guide, join, buff...) and writes their memories. " +
                            "Pair a lively chat model with a sharp one here, e.g. dolphin3:8b + qwen2.5:7b. Both stay loaded, so add up their sizes.", small);
            string brain = (Plugin.AiThinkingModel.Value ?? "").Trim();
            ModelChoices(brain, name => AIChat.SetThinkingModel(name), allowSame: true);
            if (brain.Length > 0 && !Ollama.BrainOk)
                GUILayout.Label("<color=#F2C94C>The thinking model didn't load, so the chat model is doing its work.</color>", small);

            GUILayout.Space(10);
            GUILayout.Label("Another model", header);
            GUILayout.Label("Any name from ollama.com/library. It downloads the first time you pick it.", small);
            GUILayout.BeginHorizontal();
            customModel = GUILayout.TextField(customModel ?? "", 60, GUILayout.Height(26), GUILayout.MinWidth(180));
            string typed = (customModel ?? "").Trim().ToLowerInvariant();
            bool valid = Ollama.LooksLikeModel(typed);
            GUI.enabled = valid;
            if (GUILayout.Button("Use for chat", normal, GUILayout.Height(26))) { AIChat.SetChatModel(typed); GUIUtility.keyboardControl = 0; }
            if (GUILayout.Button("Use for thinking", normal, GUILayout.Height(26))) { AIChat.SetThinkingModel(typed); GUIUtility.keyboardControl = 0; }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
        }

        private void ModelChoices(string current, Action<string> pick, bool allowSame)
        {
            if (allowSame)
            {
                bool same = string.IsNullOrEmpty(current);
                if (GUILayout.Button((same ? "> " : "") + "Same as chat model", same ? selected : normal, GUILayout.Height(26)) && !same) pick("");
            }
            var names = new List<string>();
            foreach (Ollama.ModelInfo m in installed.OrderBy(m => m.Name)) names.Add(m.Name);
            foreach (Suggestion s in Suggestions) if (!names.Any(n => SameModel(n, s.Name))) names.Add(s.Name);
            if (!string.IsNullOrEmpty(current) && !names.Any(n => SameModel(n, current))) names.Insert(0, current);

            foreach (string name in names)
            {
                bool isCurrent = SameModel(name, current);
                bool have = installed.Any(m => SameModel(m.Name, name));
                Suggestion s = Suggestions.FirstOrDefault(x => SameModel(x.Name, name));
                double gb = have ? installed.First(m => SameModel(m.Name, name)).Gb : s?.Gb ?? 0;
                string sizeText = gb > 0 ? gb.ToString("0.0") + " GB" : "";
                string text = (isCurrent ? "> " : "") + "<b>" + name + "</b>  <color=#9AA0A6>" + sizeText +
                              (have ? "" : (reachable ? ", downloads when picked" : "")) + "</color>" +
                              (s != null ? "\n<size=11><color=#C9C2B0>" + s.Note + "</color></size>" : "");
                if (GUILayout.Button(text, isCurrent ? selected : normal, GUILayout.MinHeight(s != null ? 40 : 26)) && !isCurrent) pick(name);
            }
        }

        private static bool SameModel(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            string x = a.Contains(":") ? a : a + ":latest", y = b.Contains(":") ? b : b + ":latest";
            return string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
        }

        private void PersonalityTab()
        {
            GUILayout.Label("Tone", header);
            GUILayout.Label("How openly sims show their feelings toward you. Some models still hold back; mistral-nemo, hermes3 and dolphin3 go furthest.", small);
            int tone = Math.Max(0, Array.IndexOf(Tones, Plugin.AiTone.Value));
            int picked = Choice(tone, Tones);
            if (picked != tone) Plugin.AiTone.Value = Tones[picked];
            GUILayout.Label(ToneNotes[picked], small);

            GUILayout.Space(10);
            GUILayout.Label("Conversation", header);
            Toggle(Plugin.AiCatchRedirects, "Reroll replies that change the subject",
                "If a sim dodges you with \"anyway, let's focus on the next raid\", the AI is asked once more for a real answer.");
            Toggle(Plugin.AiActions, "Sims act on what they say", "Joining, guiding, following, buffing and so on when they agree to it.");
            Toggle(Plugin.EnableFeelings, "Evolving feelings", "Sims grow to love, admire, envy or resent you over time.");

            GUILayout.Space(10);
            GUILayout.Label("Who answers when you talk without a target", header);
            float radius = Plugin.AiNearbyRadius.Value;
            int at = radius <= 0f ? 0 : radius <= 4f ? 1 : radius <= 8f ? 2 : 3;
            int r = Choice(at, new[] { "Nobody", "Very close", "Nearby", "Anyone around" });
            if (r != at) Plugin.AiNearbyRadius.Value = new[] { 0f, 4f, 6f, 12f }[r];
        }

        private void GuidingTab()
        {
            GUILayout.Label("How sims show you the way", header);
            int style = Math.Max(0, Array.IndexOf(Styles, Plugin.GuideStyle.Value));
            int picked = Choice(style, StyleLabels);
            if (picked != style) Plugin.GuideStyle.Value = Styles[picked];
            GUILayout.Label(StyleNotes[picked], small);

            GUILayout.Space(10);
            GUILayout.Label("Pace", header);
            int pace = Math.Max(0, Array.IndexOf(Paces, Plugin.GuidePace.Value));
            int newPace = Choice(pace, Paces);
            if (newPace != pace) Plugin.GuidePace.Value = Paces[newPace];

            GUILayout.Space(10);
            GUILayout.Label("Wait when you fall behind", header);
            float wait = Plugin.GuideWaitDistance.Value;
            int waitIndex = wait <= 0f ? 3 : wait <= 10f ? 0 : wait <= 18f ? 1 : 2;
            int newWait = Choice(waitIndex, WaitLabels);
            if (newWait != waitIndex) Plugin.GuideWaitDistance.Value = WaitDistances[newWait];

            GUILayout.Space(10);
            Toggle(Plugin.GuideAcrossZones, "Guide across zones", "Sims lead you through zone lines to an NPC or zone somewhere else.");

            GUILayout.Space(10);
            GUILayout.Label("Mist colour (/find and guiding)", header);
            int color = Math.Max(0, Array.IndexOf(Colors, Plugin.MistColor.Value));
            int newColor = Choice(color, Colors);
            if (newColor != color) Plugin.MistColor.Value = Colors[newColor];

            GUILayout.Space(10);
            GUILayout.Label("/find Name leads you to anyone in this zone with the mist; /find lists who's here.", small);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Who's here?", normal, GUILayout.Height(26))) MistTrail.List();
            if (GUILayout.Button("Clear the mist", normal, GUILayout.Height(26))) MistTrail.Stop("The mist disperses.");
            GUILayout.EndHorizontal();
        }

        private string search = "";
        private string chosen;
        private int ageFilter, genderFilter, personaFilter;
        private bool showFilters = true;
        private static readonly string[] AgeFilters = { "Any age", "Kids (10-13)", "Teens (14-17)", "18-29", "30-44", "45-59", "60+" };
        private static readonly string[] GenderFilters = { "Anyone", "Men", "Women", "Nonbinary" };
        private static readonly string[] GenderValues = { null, "man", "woman", "nonbinary" };

        private void PeopleTab()
        {
            GUILayout.Label("People", header);
            GUILayout.Label("Search names, jobs, hobbies or places (try \"nurse\", \"guitar\", \"school\"), or filter below.", small);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Search:", label, GUILayout.Width(56));
            search = GUILayout.TextField(search ?? "", 40, GUILayout.Height(26));
            if (GUILayout.Button("Clear", normal, GUILayout.Width(60), GUILayout.Height(26)))
            {
                search = "";
                ageFilter = genderFilter = personaFilter = 0;
                GUIUtility.keyboardControl = 0;
            }
            if (GUILayout.Button(showFilters ? "Hide filters" : "Filters", normal, GUILayout.Width(96), GUILayout.Height(26))) showFilters = !showFilters;
            GUILayout.EndHorizontal();
            List<string> personaNames = new List<string> { "Any personality" };
            personaNames.AddRange(SimVoices.PersonaIds);
            if (personaFilter >= personaNames.Count) personaFilter = 0;
            if (showFilters)
            {
                GUILayout.Label("Age", small);
                ageFilter = GUILayout.SelectionGrid(ageFilter, AgeFilters, 4, normal, GUILayout.MinHeight(52));
                GUILayout.Label("Gender", small);
                genderFilter = GUILayout.SelectionGrid(genderFilter, GenderFilters, 4, normal, GUILayout.Height(26));
                GUILayout.Label("Personality (shows once you've seen them in game)", small);
                personaFilter = GUILayout.SelectionGrid(personaFilter, personaNames.ToArray(), 4, normal);
            }
            GUILayout.Space(4);

            if (chosen != null) Card(chosen);

            List<SimPlayerTracking> sims = GameData.SimMngr != null ? GameData.SimMngr.Sims : null;
            if (sims == null || sims.Count == 0)
            {
                GUILayout.Label("No sims loaded yet. Get into the world first.", small);
                return;
            }
            Friendships friends = Plugin.FriendsNow();
            var points = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in friends.All) if (pair.Key.StartsWith("sim:")) points[Friendships.NameOf(pair.Key)] = pair.Value.Points;

            string wanted = (search ?? "").Trim();
            string wantPersona = personaFilter > 0 ? personaNames[personaFilter] : null;
            string wantGender = GenderValues[genderFilter];
            string wantAge = ageFilter > 0 ? AgeFilters[ageFilter] : null;
            var list = sims.Where(t =>
                {
                    if (t == null || string.IsNullOrEmpty(t.SimName)) return false;
                    string persona = PersonaFor(t);
                    if (wantPersona != null && persona != wantPersona) return false;
                    RealLife.Person person = RealLife.PersonOf(t.SimName, persona);
                    if (wantGender != null && (person == null || person.Gender != wantGender)) return false;
                    if (wantAge != null && (person == null || person.AgeGroup != wantAge)) return false;
                    return wanted.Length == 0 || t.SimName.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0 ||
                           (persona ?? "").IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0 ||
                           (person != null && person.Summary.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0);
                })
                .OrderByDescending(t => points.TryGetValue(t.SimName, out float pt) ? pt : -1f)
                .ThenBy(t => t.SimName).Take(40).ToList();
            bool filtering = wanted.Length > 0 || wantPersona != null || wantGender != null || wantAge != null;
            GUILayout.Label(list.Count == 0 ? "Nobody matches." : !filtering ? "People you know best first. Click a name for details." : list.Count + (list.Count == 40 ? "+" : "") + " found:", small);
            foreach (SimPlayerTracking t in list)
            {
                float pt = points.TryGetValue(t.SimName, out float v) ? v : 0f;
                string persona = PersonaFor(t);
                RealLife.Person person = RealLife.PersonOf(t.SimName, persona);
                string text = "<b>" + t.SimName + "</b>  <color=#9AA0A6>lvl " + t.Level + " " + t.ClassName +
                              (persona != null ? ", " + persona : "") + (pt > 0f ? ", " + Friendships.TierNames[Friendships.TierOf(pt)] : "") + "</color>" +
                              (person != null ? "\n<size=11><color=#C9C2B0>" + person.Age + ", " + person.Gender + ", " + person.Life + "</color></size>" : "");
                if (GUILayout.Button(text, t.SimName == chosen ? selected : normal, GUILayout.MinHeight(40)))
                {
                    chosen = t.SimName == chosen ? null : t.SimName;
                    editing = false;
                    draft = null;
                    notes = null;
                    scroll = Vector2.zero;
                }
            }
        }

        private static string PersonaFor(SimPlayerTracking t)
        {
            string p = t.MyAvatar != null ? SimVoices.PersonaOf(t.MyAvatar) : null;
            return p ?? WorldIndex.PersonaOf(t.SimName);
        }

        private void Card(string name)
        {
            SimPlayerTracking t = FriendshipEvents.Tracking(name);
            if (t == null) { chosen = null; return; }
            string persona = PersonaFor(t);
            Friendships friends = Plugin.FriendsNow();
            Friendship f = null;
            string key = Friendships.Sim(name);
            foreach (var pair in friends.All) if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)) f = pair.Value;

            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label(name + "  <color=#9AA0A6><size=12>level " + t.Level + " " + t.ClassName +
                            (string.IsNullOrEmpty(t.CurScene) ? "" : ", in " + GetCommonTerms.GetZoneTerm(t.CurScene)) +
                            (FriendshipEvents.IsGuildmate(name) ? ", your guildmate" : "") + "</size></color>", header);
            if (persona != null)
                GUILayout.Label("<b>Personality:</b> " + persona + ". " + SimVoices.AboutOf(persona), label);
            else
                GUILayout.Label("<b>Personality:</b> " + (SimVoices.IsPremade(name) ? "one of the game's hand-made sims, with their own voice." : "not known yet; it shows once you've seen them."), label);
            RealLife.Person person = RealLife.PersonOf(name, persona);
            if (person != null && GUILayout.Button("<b>The real person:</b> " + person.Summary + (editing ? "" : "  <color=#9AA0A6><size=11>(click to edit)</size></color>"), label) && !editing)
            {
                editing = true;
                notes = null;
                draft = null;
            }
            if (f != null && f.Points > 0f)
            {
                GUILayout.Label("<b>Friendship:</b> " + Friendships.TierNames[Friendships.TierOf(f.Points)] + " (" + (int)f.Points + "), met " + Friendships.Ago(f.FirstMet) +
                                ", " + f.Memories.Count + " memories", label);
                Feelings.Sanitize(name, f);
                string feel = Feelings.Describe(f);
                if (!string.IsNullOrEmpty(feel)) GUILayout.Label("<b>Feelings:</b> " + feel.Replace("them", "you").Replace("they", "you"), label);
                if (!string.IsNullOrEmpty(f.Summary) && GUILayout.Button("<b>In short:</b> " + f.Summary, label) && !editing)
                {
                    editing = true;
                    notes = null;
                    draft = null;
                }
            }
            else GUILayout.Label("<b>Friendship:</b> you haven't really met yet.", label);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Memories in chat", normal, GUILayout.Height(26))) FriendCommands.ShowMemories(name);
            bool here = t.MyAvatar != null && t.CurScene == WorldContext.Zone;
            GUI.enabled = here;
            if (GUILayout.Button("Find with mist", normal, GUILayout.Height(26))) MistTrail.Begin(name);
            GUI.enabled = f != null && f.Conversation.Count > 0;
            if (GUILayout.Button("Clear recent chat", normal, GUILayout.Height(26))) AIChat.Forget(name);
            GUI.enabled = true;
            if (GUILayout.Button(editing ? "Done editing" : "Edit", editing ? selected : normal, GUILayout.Width(110), GUILayout.Height(26)))
            {
                editing = !editing;
                notes = null;
                draft = null;
            }
            if (GUILayout.Button("Close", normal, GUILayout.Width(60), GUILayout.Height(26))) { chosen = null; editing = false; }
            GUILayout.EndHorizontal();
            if (editing) Editor(name, persona, person);
            GUILayout.EndVertical();
            GUILayout.Space(6);
        }

        private bool editing;
        private string notes;
        private static readonly string[] FeelingLabels = { "Affection", "Attraction", "Respect", "Trust", "Envy", "Resentment" };

        /// <summary>Hands-on testing: set a sim's personality, friendship and feelings directly.</summary>
        private void Editor(string name, string persona, RealLife.Person person)
        {
            Friendships friends = Plugin.FriendsNow();
            string key = Friendships.Sim(name);
            Friendship f = friends.Get(key);
            bool minor = person != null && person.Minor;
            GUILayout.Space(6);
            GUILayout.Label("Edit " + name, header);
            GUILayout.Label("Changes save straight away. Feelings still drift slowly with time and with how you treat them.", small);
            BackgroundEditor(name, persona, person);

            GUILayout.Label("<b>Presets</b>", label);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Best friend", normal, GUILayout.Height(26))) Preset(f, 320f, 9, 0, 7, 9, 0, 0);
            GUI.enabled = !minor;
            if (GUILayout.Button("Crush", normal, GUILayout.Height(26))) Preset(f, 80f, 6, 5, 4, 5, 0, 0);
            if (GUILayout.Button("In love", normal, GUILayout.Height(26))) Preset(f, 200f, 8, 8, 6, 7, 0, 0);
            GUI.enabled = true;
            if (GUILayout.Button("Rival", normal, GUILayout.Height(26))) Preset(f, 40f, 2, 0, 6, 2, 7, 3);
            if (GUILayout.Button("Nemesis", normal, GUILayout.Height(26))) Preset(f, 0f, 0, 0, 2, 0, 4, 9);
            if (GUILayout.Button("Reset", normal, GUILayout.Height(26))) Preset(f, 0f, 0, 0, 0, 0, 0, 0);
            GUILayout.EndHorizontal();
            if (minor) GUILayout.Label("They're under 18, so attraction, Crush and In love are off.", small);

            GUILayout.Space(4);
            float max = Friendships.TierPoints[Friendships.TierPoints.Length - 1] * 1.2f;
            GUILayout.Label("<b>Friendship:</b> " + Friendships.TierNames[Friendships.TierOf(f.Points)] + " (" + (int)f.Points + ")", label);
            float points = GUILayout.HorizontalSlider(f.Points, 0f, max);
            if (Mathf.Abs(points - f.Points) > 0.5f) { f.Points = Mathf.Round(points); friends.MarkDirty(); }
            GUILayout.Space(8);

            for (int i = 0; i < Feelings.Names.Length; i++)
            {
                string feeling = Feelings.Names[i];
                float now = f.Feelings.TryGetValue(feeling, out float v) ? v : 0f;
                GUILayout.BeginHorizontal();
                GUILayout.Label(FeelingLabels[i] + ": " + now.ToString("0.0"), label, GUILayout.Width(150));
                GUI.enabled = !(minor && feeling == "attraction");
                float next = GUILayout.HorizontalSlider(now, 0f, 10f, GUILayout.MinWidth(200));
                GUI.enabled = true;
                GUILayout.EndHorizontal();
                if (Mathf.Abs(next - now) > 0.05f && !(minor && feeling == "attraction"))
                {
                    f.Feelings[feeling] = Mathf.Round(next * 10f) / 10f;
                    friends.MarkDirty();
                }
            }
            string stance = Feelings.Strongest(f);
            GUILayout.Label("Right now they'd come across as: " + (stance ?? "neutral"), small);

            GUILayout.Space(8);
            GUILayout.Label("<b>Personality</b> (typing style and lines fully switch the next time they spawn)", label);
            var ids = new List<string> { "Default" };
            ids.AddRange(SimVoices.PersonaIds);
            string overridden = WorldIndex.OverrideOf(name);
            int current = overridden == null ? 0 : Math.Max(0, ids.IndexOf(overridden));
            int picked = GUILayout.SelectionGrid(current, ids.ToArray(), 4, normal);
            if (picked != current)
            {
                WorldIndex.SetOverride(name, picked == 0 ? null : ids[picked]);
                RealLife.Reset();
            }
            if (overridden != null && persona != null) GUILayout.Label(SimVoices.AboutOf(persona), small);

            GUILayout.Space(8);
            GUILayout.Label("<b>Relationship notes</b> (what they think of your history together; the AI reads this)", label);
            if (notes == null) notes = f.Summary ?? "";
            notes = GUILayout.TextArea(notes, 600, GUILayout.MinHeight(60));
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Save notes", normal, GUILayout.Height(26)))
            {
                f.Summary = notes.Trim();
                friends.MarkDirty();
                GUIUtility.keyboardControl = 0;
            }
            if (GUILayout.Button("Undo", normal, GUILayout.Height(26))) { notes = null; GUIUtility.keyboardControl = 0; }
            GUILayout.EndHorizontal();
        }

        private Dictionary<string, string> draft;
        private string draftFor;

        /// <summary>Rewrite who they are in real life. Saved edits replace the generated background.</summary>
        private void BackgroundEditor(string name, string persona, RealLife.Person person)
        {
            if (person == null) return;
            if (draft == null || draftFor != name)
            {
                draftFor = name;
                draft = new Dictionary<string, string>
                {
                    ["age"] = person.Age.ToString(), ["gender"] = person.Gender, ["life"] = person.Life, ["place"] = person.Place,
                    ["hobbies"] = person.Hobbies ?? "", ["quirk"] = person.Quirk ?? "", ["extra"] = person.Extra ?? "",
                };
            }
            GUILayout.Space(4);
            GUILayout.Label("<b>The real person</b>" + (person.Edited ? "  <color=#9AA0A6>(your version)</color>" : ""), label);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Age", label, GUILayout.Width(110));
            draft["age"] = GUILayout.TextField(draft["age"], 2, GUILayout.Width(50), GUILayout.Height(24));
            GUILayout.Space(12);
            foreach (string g in new[] { "man", "woman", "nonbinary" })
            {
                if (GUILayout.Button(g, draft["gender"] == g ? selected : normal, GUILayout.Height(24))) draft["gender"] = g;
            }
            GUILayout.EndHorizontal();
            Field("School / job", "life", "a nurse who works long shifts");
            Field("Lives", "place", "in the suburbs, with a partner");
            Field("Into", "hobbies", "hiking and horror movies");
            Field("Quirk", "quirk", "always has snacks by the keyboard");
            GUILayout.Label("More about them (family, history, secrets... anything the AI should know)", small);
            draft["extra"] = GUILayout.TextArea(draft["extra"], 800, GUILayout.MinHeight(54));

            bool validAge = int.TryParse(draft["age"], out int age) && age >= 10 && age <= 99;
            if (!validAge) GUILayout.Label("<color=#F2C94C>Age must be a number from 10 to 99.</color>", small);
            else if (age < 18) GUILayout.Label("Under 18: they'll never flirt or develop romantic feelings.", small);
            GUILayout.BeginHorizontal();
            GUI.enabled = validAge;
            if (GUILayout.Button("Save background", normal, GUILayout.Height(26)))
            {
                RealLife.SetEdits(name, draft.ToDictionary(p => p.Key, p => p.Value.Trim()));
                Feelings.Sanitize(name, Plugin.FriendsNow().Get(Friendships.Sim(name)));
                draft = null;
                GUIUtility.keyboardControl = 0;
            }
            GUI.enabled = true;
            if (GUILayout.Button("Undo changes", normal, GUILayout.Height(26))) { draft = null; GUIUtility.keyboardControl = 0; }
            GUI.enabled = person.Edited;
            if (GUILayout.Button("Back to original", normal, GUILayout.Height(26)))
            {
                RealLife.SetEdits(name, null);
                draft = null;
                GUIUtility.keyboardControl = 0;
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            GUILayout.Space(6);
        }

        private void Field(string title, string key, string example)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(title, label, GUILayout.Width(110));
            draft[key] = GUILayout.TextField(draft[key] ?? "", 200, GUILayout.Height(24), GUILayout.MinWidth(250));
            GUILayout.EndHorizontal();
            if (string.IsNullOrEmpty(draft[key])) GUILayout.Label("e.g. " + example, small);
        }

        private static void Preset(Friendship f, float points, float affection, float attraction, float respect, float trust, float envy, float resentment)
        {
            f.Points = points;
            float[] values = { affection, attraction, respect, trust, envy, resentment };
            for (int i = 0; i < Feelings.Names.Length; i++) f.Feelings[Feelings.Names[i]] = values[i];
            Plugin.FriendsNow().MarkDirty();
        }

        private int Choice(int current, string[] options)
        {
            int result = current;
            GUILayout.BeginHorizontal();
            for (int i = 0; i < options.Length; i++)
            {
                if (GUILayout.Button(options[i], i == current ? selected : normal, GUILayout.Height(28))) result = i;
            }
            GUILayout.EndHorizontal();
            return result;
        }

        private void Toggle(BepInEx.Configuration.ConfigEntry<bool> entry, string text, string note)
        {
            bool on = entry.Value;
            if (GUILayout.Button((on ? "[x] " : "[  ] ") + text, on ? selected : normal, GUILayout.Height(28))) entry.Value = !on;
            GUILayout.Label(note, small);
        }

        // ---------------------------------------------------------------- looks

        private static Texture2D Solid(Color c)
        {
            var t = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        private void Styles_()
        {
            if (windowStyle != null && background != null) return;
            background = Solid(new Color(0.07f, 0.065f, 0.06f, 0.96f));
            buttonOff = Solid(new Color(0.18f, 0.16f, 0.14f, 1f));
            buttonHover = Solid(new Color(0.26f, 0.23f, 0.19f, 1f));
            buttonOn = Solid(new Color(0.45f, 0.34f, 0.12f, 1f));

            windowStyle = new GUIStyle(GUI.skin.window) { padding = new RectOffset(14, 14, 10, 12) };
            windowStyle.normal.background = background;
            windowStyle.onNormal.background = background;

            title = new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold, richText = true };
            title.normal.textColor = new Color(0.95f, 0.8f, 0.45f);
            header = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold, richText = true };
            header.normal.textColor = new Color(0.95f, 0.8f, 0.45f);
            label = new GUIStyle(GUI.skin.label) { fontSize = 13, wordWrap = true, richText = true };
            label.normal.textColor = new Color(0.92f, 0.9f, 0.85f);
            small = new GUIStyle(label) { fontSize = 11 };
            small.normal.textColor = new Color(0.7f, 0.68f, 0.63f);

            normal = new GUIStyle(GUI.skin.button) { fontSize = 13, richText = true, wordWrap = true, alignment = TextAnchor.MiddleLeft, padding = new RectOffset(10, 10, 5, 5) };
            normal.normal.background = buttonOff;
            normal.hover.background = buttonHover;
            normal.active.background = buttonOn;
            normal.onNormal.background = buttonOn;
            normal.normal.textColor = normal.hover.textColor = new Color(0.9f, 0.88f, 0.83f);
            normal.active.textColor = normal.onNormal.textColor = Color.white;
            selected = new GUIStyle(normal);
            selected.normal.background = buttonOn;
            selected.hover.background = buttonOn;
            selected.normal.textColor = selected.hover.textColor = Color.white;
        }
    }
}
