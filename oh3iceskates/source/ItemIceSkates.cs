using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace oh3iceskates
{
    /// <summary>
    /// Represents the Ice Skates item. Handles equipping mechanics when players interact while holding the item.
    /// </summary>
    public class ItemIceSkates : Item
    {
        public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handling)
        {
            if (byEntity is EntityPlayer entityPlayer)
            {
                // Access the player's character inventory to interact with their wearable slots
                IInventory characterInv = entityPlayer.Player.InventoryManager.GetOwnInventory("character");

                if (characterInv != null)
                {
                    // Identify the dedicated footwear slot
                    ItemSlot footSlot = characterInv[(int)EnumCharacterDressType.Foot];

                    if (footSlot != null)
                    {
                        // Swap the held item with the footwear slot's contents
                        ItemStack currentShoes = footSlot.Itemstack;
                        footSlot.Itemstack = slot.Itemstack;
                        slot.Itemstack = currentShoes;

                        // Mark slots as dirty to trigger UI redraws and network synchronization
                        footSlot.MarkDirty();
                        slot.MarkDirty();

                        // Halt default interaction handling since the equip action is complete
                        handling = EnumHandHandling.PreventDefaultAction;
                        return;
                    }
                }
            }

            // Proceed with default behavior if the item could not be equipped
            base.OnHeldInteractStart(slot, byEntity, blockSel, entitySel, firstEvent, ref handling);
        }
    }
}