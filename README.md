# Living NPCs: an Erenshor mod

A [BepInEx 5](https://github.com/BepInEx/BepInEx) plugin that makes Erenshor's sim players and NPCs feel more alive.

## Talk to anyone (AI chat)

With [Ollama](https://ollama.com) installed, you can talk to sims and NPCs in your own words and
they answer in character, generated on your own PC (free, private, no internet needed once set up).

**One-time setup:** install Ollama from ollama.com. That's it. When the game starts, the mod
launches Ollama in the background if it isn't running, downloads the model the first time
(`llama3.2:3b`, about 2 GB), and tells you in chat when sims are ready to talk.
It closes Ollama again when you quit.

**How to talk:**
- **Target a sim and type** (normal /say). They answer in their persona's voice, aware of their
  mood, the zone, the time, and your shared history ("we wiped twice in Rottenfoot").
- **Say a nearby sim's name** in a message and that sim answers.
- **Ask a question out loud** and someone nearby might answer.
- **/p message** to your group: a groupmate answers (or the one you named).
- **/w Name message**: whisper any sim, even one in another zone.
- **Target an NPC and type**: they answer in character, based on their real in-game dialogue.
  Quest keywords still go to the game, so quests work exactly as before.

Messages about grouping, invites and trading are left to the game's own sims, so the game's group,
invite and trade handling isn't affected.
Typing quirks still apply: a lowercase chill sim stays lowercase, a hype sim stays loud.

**In game:** `/ai` shows status, `/ai off` and `/ai on` toggle it, `/ai model llama3.1:8b` switches model.
On a strong graphics card, an 8B model (`llama3.1:8b`, `qwen2.5:7b`) gives noticeably better replies.

How characters are described to the AI, including a summary of Erenshor's lore drawn from the
game itself, lives in `ai.txt`.

## A living world

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
This mod gives each generated sim one of 12 personas:

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

## Install

1. Install BepInEx 5 (x64) into your Erenshor folder and run the game once so it creates `BepInEx/plugins`.
2. Put `LivingNPCs.dll` in `Erenshor/BepInEx/plugins/LivingNPCs/`.
3. Launch the game. The mod writes its files to `Erenshor/BepInEx/config/`:
   - `njaysimmons.erenshor.livingnpcs.cfg`: settings (chatter frequency, radius, toggles)
   - `LivingNPCs/ai.txt`: how sims and NPCs are described to the AI, and the lore it knows
   - `LivingNPCs/life.txt`: actions, reactions, banter, memories and emotes
   - `LivingNPCs/sims.txt`: sim player personas
   - `LivingNPCs/dialogue.txt`: every line NPCs can say
   - `LivingNPCs/memory/<character>.tsv`: which NPCs each character has met
   - `LivingNPCs/memory/<character>.sims.tsv`: shared history with each sim, and sim friendships

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
- `Ollama.cs` talks to the local Ollama server on a background thread (and can start it and pull
  the model); `AIChat.cs` patches `TypeText.CheckInput` to see what you type, decides who should
  answer, builds their prompt and prints the reply through the game's chat.
- `NpcMemory.cs` keeps the per-character meeting counts and last-seen times.
