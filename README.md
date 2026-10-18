# Living NPCs: an Erenshor mod

A [BepInEx 5](https://github.com/BepInEx/BepInEx) plugin that makes Erenshor's sim players and NPCs feel more alive.

## Lasting friendships

Every sim and NPC builds a long-term friendship with each of your characters, saved between sessions.

- **Tiers:** Stranger → Acquaintance → Friend → Good Friend → Close Friend → Best Friend.
  You get a message when you reach a new one, and sims tell you so in their own words.
- **What builds it:** fighting and leveling together, wiping together, talking (AI chat),
  hailing NPCs, and friendly emotes (/wave, /thank, /hug...). /taunt hurts it. Gains are capped
  per real day, so close friendships take weeks of real play, like real ones.
- **They remember.** Each character keeps a journal of you: how you met, wipes you survived
  together, the level you hit while grouped, the time you hugged them, and, with AI chat on,
  what you talked about (the AI notes the one thing worth remembering after each conversation).
- **Friends act like friends.** They greet you warmly, bring up memories ("remember when we wiped
  together in Rottenfoot?"), wave you over when they see you, whisper hello when you log in
  ("you're back! where've you been??"), and hug back properly. NPC friends greet you by name.
  Sims' in-game opinion of you rises too, so friends are keener to group and invite you to guilds.
- **AI chat remembers** (sims). Conversations are saved, and the AI knows your friendship level, how long
  you've known each other and their memories of you, so a best friend talks like one.
- **Thorough memories.** Each character keeps facts about you (your class, your goals, things you
  told them), plans you made together, shared events and milestones, plus a running summary of your
  relationship that the AI rewrites as it grows. Memories related to what you're saying are brought
  up automatically.
- **Guildmates remember more.** Sims in your guild keep twice the memories, warm up faster, hear
  guild news ("you and Mira cleared the Brake") and remember what was said in guild chat.
- **Feelings that change.** Sims develop affection, attraction, respect, trust, envy and resentment
  toward you from how you actually treat them: what you say (with AI chat), emotes, fights won,
  wipes, out-leveling them. Over time a sim can grow devoted, admire you, become smitten, fall in
  love, turn envious, resent you or come to hate you. Each persona feels things differently
  (a charmer falls fast, a braggart gets envious, a grump holds grudges), feelings fade slowly if
  nothing feeds them, and they change how the sim greets, reacts and talks to you.
  `EvolvingFeelings` and `FeelingsSpeed` in the config control it.
- `/friends` lists your closest bonds; `/memories Name` (or target someone and type `/memories`)
  shows what they remember and how they feel about you.

## Talk to anyone (AI chat)

With [Ollama](https://ollama.com) installed, you can talk to the sim players in your own words and
they answer in character, generated on your own PC (free, private, no internet needed once set up).
Only sims use the AI: NPCs keep their scripted dialogue, so the world feels like a live MMO where the
players are the unpredictable part.

**One-time setup:** install Ollama from ollama.com. That's it. When the game starts, the mod
launches Ollama in the background if it isn't running, downloads the model the first time
(`llama3.2:3b`, about 2 GB), and tells you in chat when sims are ready to talk.
It closes Ollama again when you quit.

**How to talk:**
- **Target a sim and type** (normal /say), or just **stand next to a sim** and type. They answer in their persona's voice, aware of their
  mood, the zone, the time, and your shared history ("we wiped twice in Rottenfoot").
- **Say a nearby sim's name** in a message and that sim answers.
- **Ask a question out loud** and someone nearby might answer.
- **/p message** to your group: a groupmate answers (or the one you named).
- **/w Name message**: whisper any sim, even one in another zone.

**Sims keep their word.** When a sim agrees to something, it actually happens:

| They say | They do |
|---|---|
| "sure, i'll join you" | join your group (as if you'd invited them) |
| "let's head to Rockshade, meet at the entrance" | walk off, travel there, wait at the entrance, whisper you when you arrive, then group up |
| "omw!" (from another zone) | travel to your zone and join you when they get there |
| "gotta run, cya" | leave your group |
| "follow me, i'll show you the bank" | walk you to the banker (or a vendor, an NPC by name, a rest spot, a hunting camp, the exit to a zone), waiting if you fall behind, then point it out |
| "it's over in the Brake, follow me" | if the NPC or zone is elsewhere: group up, lead you to the right zone exit, cross with you, and keep guiding zone by zone until you're there |
| "sure, i'll follow you" / "i'll wait here" | follow you around without grouping, or stay put |
| "here, have a buff" | walk into range and cast their real buffs on you ("oom, gimme a sec" if they're out of mana) |
| "i'll help with this one" | join for the fight, then say bye and leave once it's won |
| waves, dances, sits, cheers | do it, with the animation |

For NPCs in other zones the mod relies on what it has seen: every zone you visit has its NPCs and
exits recorded (`memory/world-index.tsv`), and routes combine that with the game's own zone
connections. So sims can take you to anyone in a zone you've been to, and to any zone by name.

Sims are told which NPCs and spots are in the zone and which buffs they actually have, so they only
offer what they can do. They never offer trades. If a fight breaks out mid-errand they fight first,
then carry on. If the AI agrees in words but forgets the tag ("sure, follow me!" to "take me to the bank"), the
mod works out the action from your request and their yes; it never acts on a refusal.
The AI marks what it agreed to with a hidden tag that the mod strips from chat and carries out
with the game's own mechanics. Turn it off with `SimsActOnWhatTheySay` in the settings.

Messages about grouping, invites and trading are left to the game's own sims, so the game's group,
invite and trade handling isn't affected.
Typing quirks still apply: a lowercase chill sim stays lowercase, a hype sim stays loud.

**The /ai panel.** Type `/ai` (or press Ctrl+F9) to open a settings window. Changes save straight away.

- **AI models:** pick the chat model (what sims say) and an optional thinking model. Installed models
  are listed with their sizes, and so are recommended ones you don't have yet: they download the first
  time you pick them. The panel also shows what's loaded in video memory right now.
- **Two models:** the thinking model works behind the scenes. It decides what a sim does when you ask
  for something (guide, join, buff...) and writes their memories. So you can chat with a lively model
  like `dolphin3:8b` or `mistral-nemo` and keep actions and memories sharp with `qwen2.5:7b`.
  Both stay loaded, so add their sizes together (about 10 GB for that pair).
- **Personality:** a Tone of Normal, Emotional (open about crushes, love, jealousy and hurt) or Edgy
  (also blunt, sarcastic, petty, swears). A reroll catches replies that dodge you with
  "anyway, let's focus on the next raid" and asks for a real answer instead.
- **People:** search by name, job, hobby or place, and filter by age group, gender and personality, to see their level and class, where they are,
  their personality, the real person behind them, your friendship, their feelings and the summary.
  From there you can show their memories, find them with the mist, or clear your recent chat.
  **Edit** lets you test relationships directly: presets (Best friend, Crush, In love, Rival,
  Nemesis, Reset), a friendship slider, a slider for each feeling, their personality, and the
  relationship notes the AI reads. Romantic options are off for sims under 18.
  Click their description (or Edit) to rewrite their background: age, gender, school or job, where
  they live, hobbies, quirk, and a free-text box for family, history or secrets. Your version is
  saved in `memory/people.tsv`; **Back to original** restores the generated one.
- **Guiding:** sims walk you there, walk you there with a mist trail, or (in the same zone) just
  conjure the mist. Also the pace, how far you can fall behind before they wait, whether they guide
  across zones, and the mist colour.

Chat commands still work: `/ai status`, `/ai on`, `/ai off`, `/ai test`, `/ai debug`,
`/ai model mistral-nemo` (chat model) and `/ai model brain qwen2.5:7b` (thinking model; `brain same` turns it off).
`/ai forget Name` clears your recent chat with a sim (their memories and feelings stay), which breaks them out of a rut.
They're also in the config as `Model`, `ThinkingModel`, `Tone`, `RerollChangedSubjects` and the `[Guiding]` section.

**If sims don't seem to use the AI:** type `/ai` (or `/ai status`). It says whether it's connected and what the last
problem was, and while it's setting up, exactly what it's doing (downloading the model with a
percentage, or loading it into video memory). `/ai test` sends a test message and shows the reply and how long it took. When you
enter the world the mod also says whether AI chat is on, and if a reply fails it tells you why.
It retries the connection every minute, so starting Ollama after the game is fine.
Details are also in `Erenshor/BepInEx/LogOutput.log`.

`/ai debug` shows, in grey, each sim's raw reply (including any hidden action tag) and what the mod
did with it, which is the quickest way to see why a sim didn't do something.

Still stuck? Double-click **Check AI.bat** in the mod folder (with the game closed). It checks each
step (Ollama installed, running, model downloaded, a real test reply), starts Ollama or downloads the
model with visible progress if needed, shows what the mod logged, and saves a report to your Desktop.

How characters are described to the AI, including a summary of Erenshor's lore drawn from the
game itself, lives in `ai.txt`.


**Real people behind the keyboard.** Every sim is played by someone with a real life: an age from
10 to 70 (mostly adults), a gender (usually matching their character, not always), school, work or
retirement, where they live, hobbies and a quirk ("41-year-old woman, a paramedic, living by a lake.
Into birdwatching and horror movies. Has a cat that walks across the keyboard."). It's built from the
sim's name, so it never changes, and the lists are in `ai.txt` under `[ai.people.*]`.
Players under 18 talk like kids or teens, never flirt, and never develop romantic feelings toward you;
if you go there they get weirded out, and the mod never pushes them past a refusal.
When you talk about something other than the game, the game details (zone lore, actions) are left
out and sims talk as themselves: their day, opinions and life. If one drags raids or loot into a
real-life chat anyway, the reply is rerolled.

Sims and NPCs now *do* things, not just say things. Actions appear in chat as emotes
("Kessa sits down to rest for a moment.") and play real animations: dancing, sitting,
kneeling, and a weapon flourish.

- **Idle life.** Sims near you stretch, meditate, practice forms, read, dance, rummage for loot,
  each in their persona's style. Two sims standing together wave, chat, clap each other on the
  shoulder, or glare at each other, depending on whether they're friends or rivals.
- **Reactions.** When a groupmate dies, others react in group chat ("{other}!! no!", or "lol {other}"
  from a rival). They react when you die, when the group wipes, when you level (nearby strangers
  cheer and clap too), when a groupmate levels, and after close fights.
- **Moods.** Deaths and wipes drag a sim's mood down; level-ups and wins lift it. Mood changes what
  they say and do, and fades back over several minutes.
- **Friendships and rivalries.** Every pair of sims has a relationship that starts from their
  personas (grumps and hype sims clash, wholesome sims get along with everyone) and grows as they
  fight beside each other. It shapes group banter, replies and idle interactions.
- **They remember you.** Each sim keeps a record of fights, deaths, wipes and level-ups with your
  character, and brings it up: "still recovering from that wipe in Rottenfoot",
  "we've done 42 fights together, crazy".
- **Emotes.** /wave, /bow, /cheer, /laugh, /salute, /thank, /hug, /point, /cry, /flex, /shrug, /clap,
  /nod, /sigh, /facepalm, /roar, /wink, /taunt, /comfort, /greet, /bye (type /emotes in game).
  Target a sim and it responds in character; target an NPC and they react too. Emote at nobody and
  nearby sims may notice. When you /dance, sims nearby may join in.
- **NPCs act too.** NPCs turn to face you when you talk to them, often with a mannerism ("wipes
  their hands on their apron"). Around town, vendors rearrange wares, guards pace, bankers stack coins.

All of it lives in `life.txt`, with persona-, mood- and relationship-specific variants.

## Sim players with personalities

Erenshor builds every generated sim from one blank template, so they all draw from the same
phrases and type the same way, and their small-talk shouts come from one pool per zone.
This mod gives each generated sim one of 22 personas:

| Persona | Sounds like |
|---|---|
| chill | lowercase, "ya sure", "gn all" |
| hype | "LETS GOOO", emojis, all caps sometimes |
| oldschool | "Back in my day we had to med for ten minutes between pulls." |
| roleplayer | "Well met! Will you join my company?", sometimes talks in the third person |
| tryhard | xp/hr, rotations, "that was NOT my fault" |
| grump | "of course", "this is why i solo" |
| newbie | lots of questions and typos, "is it normal to die this much lol" |
| jokester | "tactical death", friendly trash talk |
| wholesome | offers help, "<3" |
| lorehound | Brax, Sivakaya, the Wisp, Astra, Rockshade Hold |
| lootgoblin | "II!! mine mine mine" |
| terse | "k", "omw", "gz" |
| charmer | "hi cutie", "for you? always ;)" |
| mentor | tips for newer players, "proud of you" |
| drama | "you will NOT believe what just happened" |
| conspiracy | "nobody asks why the vendors never sleep." |
| pirate | "ahoy matey, join me crew" |
| shy | "um... could i join you? it's okay if not" |
| braggart | "that was lag.", "you want carried?" |
| explorer | hidden waterfalls, shortcuts, "onward" |
| sleepy | "*yawn* one more level then bed" |
| superstitious | lucky charms, "someone said easy, we're cursed" |

- **Stable.** The persona comes from the sim's name, so it stays the same every session.
- **Fits the game's temperament.** Nice sims never become grumps; mean sims never become wholesome.
- **Typing style.** Lowercase, caps, emojis, typo rate, third person and sign-offs are set by
  persona, with variation between sims who share one.
- **Mixed, not replaced.** Persona lines are mixed into the game's own phrase pools
  (about 60% by default, see `PersonaShare`), and each sim draws a random subset.
- **Own small talk.** Sims sometimes shout their own small talk instead of the zone's shared comments.
- The game's hand-made sims keep their voices unless you turn on `AlsoChangeHandmadeSims`.

All personas live in `sims.txt`, which explains its own format. Add personas, tweak how common
they are, or pin a specific sim with `[sim.Name]` and `@persona=grump`.

## NPCs

- **Greetings that know you.** When you hail an NPC, they open with a line that reacts to whether
  you've met before, whether you're a regular, whether you've been away for days, and whether
  you're badly hurt. Vendors, guards and bankers each have their own manner, and greetings sometimes
  remark on the time of day or your class.
- **NPCs remember you.** Each character has their own memory file, so NPCs know your paladin
  but not your new arcanist.
- **Overheard chatter.** Friendly NPCs near you speak up on their own: vendors hawk wares, guards
  grumble about the night watch, townsfolk react to the hour and the zone, and someone will tell you
  to see a healer if you walk by bleeding.
- **Hostile and busy NPCs** get varied replies instead of the same `...` and `I'm busy right now...`.

All the writing lives in one plain-text file you can edit and reload in game with **Ctrl+F8**.

## /find

`/find Name` makes a pale mist drift along the walking path from you to someone in the current zone,
and it keeps following them if they move. It works for NPCs and sims by name, or roles and places
(`/find vendor`, `/find banker`, `/find exit`). `/find` on its own lists who's here (or follows
your target), and `/find stop` dismisses the mist. It fades when you arrive or leave the zone.

## Bald hairstyle

Character creation's hairstyle arrows now include **Bald** after the last style (beards are
separate, so bald-and-bearded works). For characters you already have, type `/hair` to cycle
styles, `/hair prev` to go back, or `/bald` to shave it all off; it saves with the character.

## Install

Download the release zip, unzip it, and double-click **Install Living NPCs.bat**. The installer:

1. finds Erenshor in any of your Steam libraries (or lets you pick the folder),
2. installs BepInEx 5, the mod loader, if you don't already have it,
3. installs or updates the mod, offering to refresh your dialogue files (old ones kept as `.bak`),
4. optionally sets up AI chat: installs Ollama, picks a model to suit your graphics card
   (16 GB+ gets `qwen2.5:14b`, 10 GB+ `llama3.1:8b`, otherwise `llama3.2:3b`) and downloads it,
5. offers to launch the game.

**Uninstall Living NPCs.bat** removes the mod, and optionally its settings and memories, and
BepInEx if the installer was the one that added it.

The mod writes its files to `Erenshor/BepInEx/config/`:
   - `njaysimmons.erenshor.livingnpcs.cfg`: settings (chatter frequency, radius, toggles)
   - `LivingNPCs/ai.txt`: how sims are described to the AI, and the lore it knows
   - `LivingNPCs/life.txt`: actions, reactions, banter, memories and emotes
   - `LivingNPCs/sims.txt`: sim player personas
   - `LivingNPCs/dialogue.txt`: every line NPCs can say
   - `LivingNPCs/memory/<character>.tsv`: which NPCs each character has met
   - `LivingNPCs/memory/<character>.sims.tsv`: shared history with each sim, and sim-to-sim relationships
   - `LivingNPCs/memory/<character>.friends.tsv`: your friendships, memories and saved conversations

## Writing dialogue

`dialogue.txt` explains its own format at the top. Short version:

```
[greet.first]
Haven't seen you before, {class}.

[ambient.zone.Azure]
The harbour's busy today.

[npc.Prestigio Valusha.greet]
Ah, {player}. Your vault awaits, as faithful as ever.
```

Tokens: `{player}`, `{class}`, `{level}`, `{npc}`, `{zone}`, `{time}`.
Zone sections use the scene name; the BepInEx console prints it every time you change zones.

## Build

Requires the .NET SDK (6 or newer). Point `GameDir` at your Erenshor install:

```
cd LivingNPCs
dotnet build -c Release -p:GameDir="C:\Program Files (x86)\Steam\steamapps\common\Erenshor"
```

If `BepInEx/plugins` exists there, the build copies the DLL in for you.
`installer/package.sh <Managed folder>` builds and zips the DLL with the installer into `dist/`.
Game assemblies are referenced from your install and are never committed to this repo.

## How it works

- `HailPatch.cs` is a Harmony patch on `NPCDialogManager.GenericHail`. The game queues each reply in
  a private field and prints it a moment later; the patch rewrites that queued string.
- `AmbientChatter.cs` scans the zone for friendly, idle NPCs and has one near you speak every
  25 to 60 seconds, with a per-NPC cooldown so nobody repeats themselves.
- `SimVoices.cs` patches `SimPlayerMngr.LoadSimPlayerTemplate`, which runs as each sim spawns, to set
  its persona, and `SimPlayerLanguage.GetGeneric` to vary small-talk shouts.
- `SimLife.cs` watches the player, group and nearby sims twice a second for deaths, wipes,
  level-ups and fights ending, and runs idle actions and group banter. `SimBonds.cs` stores
  shared history, relationships and moods.
- `PlayerEmotes.cs` patches `TypeText.CheckCommands` to add emote commands, and
  `SimPlayerLanguage.GetTargetedHello` so greetings can bring up shared memories.
- `SimActions.cs` reads the AI's action tags and carries them out; `SimEscort.cs` steers sims for
  guiding, following and waiting by standing in for their out-of-combat routine
  (`SimPlayer.SoloPlayLogic` / `GroupedWithPlayerLogic`), handing back control whenever they're attacked.
- `WorldIndex.cs` records which zone each NPC is in and which zones connect, and finds routes.
- `Ollama.cs` talks to the local Ollama server on a background thread (and can start it and pull
  the model); `AIChat.cs` patches `TypeText.CheckInput` to see what you type, decides which sim should
  answer (never an NPC), builds their prompt and prints the reply through the game's chat.
- `NpcMemory.cs` keeps the per-character meeting counts and last-seen times.
