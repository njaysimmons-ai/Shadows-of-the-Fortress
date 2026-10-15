# Living NPCs: an Erenshor mod

A [BepInEx 5](https://github.com/BepInEx/BepInEx) plugin that makes Erenshor's sim players and NPCs feel more alive.

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
   - `LivingNPCs/sims.txt`: sim player personas
   - `LivingNPCs/dialogue.txt`: every line NPCs can say
   - `LivingNPCs/memory/<character>.tsv`: who each character has met

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
- `NpcMemory.cs` keeps the per-character meeting counts and last-seen times.
