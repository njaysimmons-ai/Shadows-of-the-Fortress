# Living NPCs: an Erenshor mod

A [BepInEx 5](https://github.com/BepInEx/BepInEx) plugin that makes Erenshor's NPCs feel more alive.

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
- `NpcMemory.cs` keeps the per-character meeting counts and last-seen times.
