using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace TrashCan
{
    /// <summary>
    /// Trash Can: a block you open like a chest. Whatever is in it is deleted
    /// as soon as nobody has it open any more.
    /// </summary>
    public class TrashCanMod : ModSystem
    {
        public override bool ShouldLoad(EnumAppSide forSide)
        {
            return true;
        }

        public override void Start(ICoreAPI api)
        {
            api.RegisterBlockClass("JumbokingTrashCanBlock", typeof(BlockTrashCan));
            api.RegisterBlockEntityClass("JumbokingTrashCanEntity", typeof(BlockEntityTrashCan));
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            api.Logger.Notification("[trashcan] Server side started, trash cans hold {0} slots", BlockEntityTrashCan.SlotCount);
        }
    }

    /// <summary>Right-click opens the trash can.</summary>
    public class BlockTrashCan : Block
    {
        public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
        {
            BlockEntityTrashCan be = world.BlockAccessor.GetBlockEntity(blockSel.Position) as BlockEntityTrashCan;
            if (be != null)
            {
                be.OnPlayerRightClick(byPlayer);
                return true;
            }
            return base.OnBlockInteractStart(world, byPlayer, blockSel);
        }
    }

    /// <summary>
    /// Holds items only while someone has the trash can open. The server checks twice a second:
    /// once nobody has it open, everything inside is deleted and the last player who closed it
    /// is told how many items went.
    /// </summary>
    public class BlockEntityTrashCan : BlockEntity
    {
        /// <summary>Same on server and every client, so it isn't a config setting.</summary>
        public const int SlotCount = 16;

        private const int Columns = 4;

        private InventoryGeneric inventory;
        private GuiDialogBlockEntityInventory dialog;
        private string lastCloserUid;

        public override void Initialize(ICoreAPI api)
        {
            base.Initialize(api);

            if (inventory == null)
            {
                inventory = new InventoryGeneric(SlotCount, "trashcan-" + Pos.X + "/" + Pos.Y + "/" + Pos.Z, api);
                inventory.Pos = Pos;
            }

            if (api.Side == EnumAppSide.Server)
            {
                RegisterGameTickListener(OnServerTick, 500);
            }
        }

        // ---------------- Opening and closing ----------------

        public void OnPlayerRightClick(IPlayer byPlayer)
        {
            if (Api == null || Api.Side != EnumAppSide.Client) return;
            ICoreClientAPI capi = Api as ICoreClientAPI;
            if (capi == null) return;

            if (dialog != null)
            {
                dialog.TryClose();
                return;
            }

            dialog = new GuiDialogBlockEntityInventory(Lang.Get("trashcan:dialog-title"), inventory, Pos, Columns, capi);
            dialog.OnClosed += () =>
            {
                dialog = null;
                capi.Network.SendBlockEntityPacket(Pos, (int)EnumBlockEntityPacketId.Close, null);
            };
            dialog.TryOpen();
            capi.Network.SendPacketClient(inventory.Open(byPlayer));
            capi.Network.SendBlockEntityPacket(Pos, (int)EnumBlockEntityPacketId.Open, null);
        }

        public override void OnReceivedClientPacket(IPlayer player, int packetid, byte[] data)
        {
            if (player == null || inventory == null) return;

            if (packetid < (int)EnumBlockEntityPacketId.Open)
            {
                if (!MayUse(player)) return;
                inventory.InvNetworkUtil.HandleClientPacket(player, packetid, data);
                return;
            }

            if (packetid == (int)EnumBlockEntityPacketId.Open)
            {
                if (!MayUse(player)) return;
                if (player.InventoryManager != null) player.InventoryManager.OpenInventory(inventory);
                return;
            }

            if (packetid == (int)EnumBlockEntityPacketId.Close)
            {
                if (player.InventoryManager != null) player.InventoryManager.CloseInventory(inventory);
                lastCloserUid = player.PlayerUID;
            }
        }

        /// <summary>Land claims: only players allowed to use blocks here may open it.</summary>
        private bool MayUse(IPlayer player)
        {
            try
            {
                return Api.World.Claims.TryAccess(player, Pos, EnumBlockAccessFlags.Use);
            }
            catch (Exception)
            {
                return true;
            }
        }

        // ---------------- Deleting ----------------

        private void OnServerTick(float dt)
        {
            if (inventory == null || inventory.Empty) return;

            // Wait until nobody has it open.
            foreach (IPlayer player in Api.World.AllOnlinePlayers)
            {
                if (player != null && inventory.HasOpened(player)) return;
            }

            int items = 0;
            int stacks = 0;
            for (int i = 0; i < inventory.Count; i++)
            {
                ItemSlot slot = inventory[i];
                if (slot == null || slot.Empty) continue;
                items += slot.StackSize;
                stacks++;
                slot.Itemstack = null;
                slot.MarkDirty();
            }
            if (stacks == 0) return;

            MarkDirty();

            IServerPlayer closer = lastCloserUid != null ? Api.World.PlayerByUid(lastCloserUid) as IServerPlayer : null;
            if (closer != null)
            {
                closer.SendMessage(GlobalConstants.GeneralChatGroup, Lang.Get("trashcan:deleted", items, stacks), EnumChatType.Notification);
            }
            Api.Logger.Debug("[trashcan] Deleted {0} item(s) in {1} stack(s) at {2}", items, stacks, Pos);
        }

        // ---------------- Cleanup ----------------

        public override void OnBlockRemoved()
        {
            if (dialog != null) dialog.TryClose();
            base.OnBlockRemoved();
        }

        public override void OnBlockUnloaded()
        {
            if (dialog != null) dialog.TryClose();
            base.OnBlockUnloaded();
        }
    }
}
