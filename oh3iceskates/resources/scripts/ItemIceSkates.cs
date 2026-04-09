using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace oh3iceskates
{
    public class ItemIceSkates : Item
    {
        public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handling)
        {
            if (byEntity is EntityPlayer entityPlayer)
            {
                // Grab the player's wearable inventory
                IInventory characterInv = entityPlayer.Player.InventoryManager.GetOwnInventory("character");

                if (characterInv != null)
                {
                    // Target the exact slot used for footwear
                    ItemSlot footSlot = characterInv[(int)EnumCharacterDressType.Foot];

                    if (footSlot != null)
                    {
                        // Swap the skates in our hand with whatever is in the footwear slot (even if it's empty)
                        ItemStack currentShoes = footSlot.Itemstack;
                        footSlot.Itemstack = slot.Itemstack;
                        slot.Itemstack = currentShoes;

                        // Mark both slots as dirty so the UI redraws and the server syncs
                        footSlot.MarkDirty();
                        slot.MarkDirty();

                        // Tell the engine we successfully handled the interaction
                        handling = EnumHandHandling.PreventDefaultAction;
                        return;
                    }
                }
            }

            // Fallback to default behavior if something goes wrong
            base.OnHeldInteractStart(slot, byEntity, blockSel, entitySel, firstEvent, ref handling);
        }
    }
}