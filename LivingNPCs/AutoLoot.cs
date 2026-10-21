using System;
using UnityEngine;

namespace LivingNPCs
{
    /// <summary>
    /// Picks up items you already carry a stack of (and, optionally, gold) from corpses near you, without
    /// opening the loot window. Gear and new item types are left on the corpse for you to look at.
    /// </summary>
    internal class AutoLoot : MonoBehaviour
    {
        private float next;

        private void Update()
        {
            if (Time.time < next) return;
            next = Time.time + 0.75f;
            try
            {
                Sweep();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Auto-loot failed: " + e.Message);
                next = Time.time + 10f;
            }
        }

        private static void Sweep()
        {
            if (!Plugin.AutoLootStacks.Value && !Plugin.AutoLootGold.Value) return;
            PlayerControl pc = GameData.PlayerControl;
            Inventory inv = GameData.PlayerInv;
            if (pc == null || inv == null || pc.Myself == null || !pc.Myself.Alive) return;
            // The open loot window keeps its own copy of a corpse's items; leave everything alone until it closes.
            if (GameData.LootWindow != null && GameData.LootWindow.WindowParent != null && GameData.LootWindow.WindowParent.activeSelf) return;

            float radius = Plugin.AutoLootRadius.Value;
            Vector3 me = pc.transform.position;
            foreach (LootTable table in FindObjectsOfType<LootTable>())
            {
                if (table == null) continue;
                Character body = table.hostChar;
                if (body == null || body.Alive || body.GetComponent<SimPlayer>() != null || table.GetComponent<RotChest>() != null) continue;
                if (Vector3.Distance(me, table.transform.position) > radius) continue;

                bool took = false;
                if (Plugin.AutoLootStacks.Value)
                {
                    for (int i = table.ActualDrops.Count - 1; i >= 0; i--)
                    {
                        Item item = table.ActualDrops[i];
                        if (item == null || item == inv.Empty || !item.Stackable || !Carrying(inv, item)) continue;
                        if (!inv.AddItemToInv(item)) break; // the stack is full or the bags are; leave the rest
                        UpdateSocialLog.LogAdd(new ChatLogLine("Auto-looted: <color=yellow>" + item.ItemName + "</color>", ChatLogLine.LogType.SystemMessages, "yellow"));
                        table.ActualDrops.RemoveAt(i);
                        if (i < table.ActualDropsQual.Count) table.ActualDropsQual.RemoveAt(i);
                        took = true;
                    }
                }
                if (Plugin.AutoLootGold.Value && table.MyGold > 0)
                {
                    inv.Gold += table.MyGold;
                    UpdateSocialLog.LogAdd(new ChatLogLine("Found " + table.MyGold + " Gold!", ChatLogLine.LogType.SystemMessages));
                    table.MyGold = 0;
                    took = true;
                }
                if (took) Tidy(table, body);
            }
        }

        /// <summary>True if one of your bag slots already holds this item.</summary>
        private static bool Carrying(Inventory inv, Item item)
        {
            foreach (ItemIcon slot in inv.StoredSlots)
            {
                if (slot != null && !slot.TrashSlot && slot.MyItem == item) return true;
            }
            return false;
        }

        /// <summary>Once a corpse has nothing left, remove its loot sparkle like the game does after looting.</summary>
        private static void Tidy(LootTable table, Character body)
        {
            foreach (Item item in table.ActualDrops)
            {
                if (item != null && item != GameData.PlayerInv.Empty) return;
            }
            if (table.MyGold > 0) return;
            if (body.lootAlert != null) Destroy(body.lootAlert);
        }
    }
}
