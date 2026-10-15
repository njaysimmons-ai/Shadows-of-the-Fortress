using System;
using HarmonyLib;

namespace LivingNPCs
{
    /// <summary>
    /// Wraps NPCDialogManager.GenericHail, the method that runs when the player hails an NPC.
    /// The game queues its reply in the private ReturnString field and prints it a moment later,
    /// so we only have to rewrite that string before it is printed.
    /// </summary>
    [HarmonyPatch(typeof(NPCDialogManager), nameof(NPCDialogManager.GenericHail))]
    internal static class HailPatch
    {
        private const string HostileReply = "...";
        private const string BusyReply = "I'm busy right now...";

        private static readonly AccessTools.FieldRef<NPCDialogManager, string> ReturnString =
            AccessTools.FieldRefAccess<NPCDialogManager, string>("ReturnString");

        private static readonly AccessTools.FieldRef<NPCDialogManager, float> ReturnDelay =
            AccessTools.FieldRefAccess<NPCDialogManager, float>("returnDel");

        private struct Before
        {
            public string Reply;
            public float Delay;
        }

        private static void Prefix(NPCDialogManager __instance, out Before __state)
        {
            __state = new Before { Reply = ReturnString(__instance), Delay = ReturnDelay(__instance) };
        }

        private static void Postfix(NPCDialogManager __instance, Before __state)
        {
            try
            {
                Rewrite(__instance, __state);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Hail rewrite failed: " + e);
            }
        }

        private static void Rewrite(NPCDialogManager manager, Before before)
        {
            string reply = ReturnString(manager);
            bool queuedNewReply = ReturnDelay(manager) != before.Delay || !ReferenceEquals(reply, before.Reply);
            if (!Plugin.EnableGreetings.Value || !queuedNewReply || reply == null)
            {
                return;
            }

            NPC npc = manager.GetComponent<NPC>();
            if (npc == null || string.IsNullOrEmpty(npc.NPCName))
            {
                return;
            }
            Plugin.Memory.UseCharacter(WorldContext.PlayerName);

            DialogueBank bank = Plugin.Bank;
            Random rng = Plugin.Rng;
            string name = npc.NPCName;

            // Whoever you talk to looks at you, and sometimes does something as they answer.
            Speech.Face(npc, GameData.PlayerControl.transform.position);
            string role = WorldContext.RoleOf(npc).ToString().ToLowerInvariant();
            string mannerism = reply == HostileReply
                ? bank.PickFirst(rng, "npc." + name + ".action.hostile", "action.hail.hostile")
                : rng.NextDouble() < Plugin.HailActionChance.Value
                    ? bank.PickFirst(rng, "npc." + name + ".action.hail", "action.hail." + role, "action.hail")
                    : null;
            if (mannerism != null)
            {
                Speech.Emote(name, WorldContext.Fill(Speech.TakeAnimation(mannerism, out _), npc));
            }

            if (reply == HostileReply)
            {
                string cold = bank.PickFirst(rng, "npc." + name + ".hostile", "hostile");
                if (cold != null)
                {
                    ReturnString(manager) = WorldContext.Fill(cold, npc);
                }
                return;
            }
            if (reply == BusyReply)
            {
                string busy = bank.PickFirst(rng, "npc." + name + ".busy", "busy");
                if (busy != null)
                {
                    ReturnString(manager) = WorldContext.Fill(busy, npc);
                }
                return;
            }
            if (reply == manager.Rejection)
            {
                return;
            }

            NpcRecord past = Plugin.Memory.RecordHail(name);
            string greeting = PickGreeting(bank, rng, npc, past);
            if (greeting != null)
            {
                ReturnString(manager) = WorldContext.Fill(greeting, npc) + " " + reply;
            }
        }

        private static string PickGreeting(DialogueBank bank, Random rng, NPC npc, NpcRecord past)
        {
            string name = npc.NPCName;
            string role = WorldContext.RoleOf(npc).ToString().ToLowerInvariant();

            // The most specific circumstance wins: an injured player gets remarked on before anything else.
            if (WorldContext.PlayerHurt && rng.NextDouble() < 0.8)
            {
                string hurt = bank.PickFirst(rng, "npc." + name + ".hurt", "greet.hurt");
                if (hurt != null) return hurt;
            }

            string familiarity;
            if (past == null) familiarity = "first";
            else if ((DateTime.UtcNow - past.LastSeenUtc).TotalDays >= Plugin.AbsenceDays.Value) familiarity = "long_absence";
            else if (past.Hails >= Plugin.RegularAfter.Value) familiarity = "regular";
            else familiarity = "returning";

            string line = bank.PickFirst(rng,
                "npc." + name + ".greet." + familiarity,
                "npc." + name + ".greet",
                "greet." + role + "." + familiarity,
                "greet." + familiarity);

            // Now and then, colour the greeting with the hour or the player's calling.
            if (line != null && rng.NextDouble() < Plugin.FlavorChance.Value)
            {
                string flavor = bank.PickAny(rng, new[] { "flavor.time." + WorldContext.TimeOfDay, "flavor.class." + WorldContext.PlayerClass });
                if (flavor != null) line += " " + flavor;
            }
            return line;
        }
    }
}
