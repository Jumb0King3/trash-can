using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

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
        private GuiDialogTrashCan dialog;

        public BlockEntityTrashCan()
        {
            // Created here so FromTreeAttributes (which can run before Initialize) has somewhere to load into.
            inventory = new InventoryGeneric(SlotCount, null, null, null, null);
        }

        /// <summary>Block entity packet the Delete button sends. Above the game's own ids (1000 = open, 1001 = close).</summary>
        public const int DeletePacketId = 1100;

        /// <summary>Whether anyone has it open. Set by the server and synced to every player, who see the lid open.</summary>
        private bool lidOpen;
        private bool animatorReady;
        private bool animatorFailed;

        /// <summary>The block's animation helper, fetched by reflection (its types use Dictionary, which mod source can't reference).</summary>
        private object AnimUtil
        {
            get
            {
                BEBehaviorAnimatable beh = GetBehavior<BEBehaviorAnimatable>();
                return beh == null ? null : GetMember(beh, "animUtil");
            }
        }

        private static object GetMember(object target, string name)
        {
            if (target == null) return null;
            System.Type t = target.GetType();
            System.Reflection.BindingFlags f = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            System.Reflection.FieldInfo fi = t.GetField(name, f);
            if (fi != null) return fi.GetValue(target);
            System.Reflection.PropertyInfo pi = t.GetProperty(name, f);
            if (pi != null) return pi.GetValue(target, null);
            return null;
        }

        private static void SetMember(object target, string name, object value)
        {
            System.Type t = target.GetType();
            System.Reflection.BindingFlags f = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;
            System.Reflection.FieldInfo fi = t.GetField(name, f);
            if (fi != null) { fi.SetValue(target, value); return; }
            System.Reflection.PropertyInfo pi = t.GetProperty(name, f);
            if (pi != null && pi.CanWrite) pi.SetValue(target, value, null);
        }

        /// <summary>Calls the first public method with this name whose parameters accept the given arguments.</summary>
        private static object CallMethod(object target, string name, params object[] args)
        {
            foreach (System.Reflection.MethodInfo m in target.GetType().GetMethods())
            {
                if (m.Name != name) continue;
                System.Reflection.ParameterInfo[] ps = m.GetParameters();
                if (ps.Length != args.Length) continue;
                bool ok = true;
                for (int i = 0; i < ps.Length; i++)
                {
                    if (args[i] != null && !ps[i].ParameterType.IsAssignableFrom(args[i].GetType())) { ok = false; break; }
                }
                if (ok) return m.Invoke(target, args);
            }
            throw new System.MissingMethodException(target.GetType().Name, name);
        }

        public override void Initialize(ICoreAPI api)
        {
            base.Initialize(api);

            inventory.LateInitialize("trashcan-" + Pos.X + "/" + Pos.Y + "/" + Pos.Z, api);
            inventory.Pos = Pos;

            if (api.Side == EnumAppSide.Server)
            {
                // Any change to the slots is saved with the world and sent to every player's screen.
                inventory.SlotModified += OnSlotModified;
                RegisterGameTickListener(OnServerTick, 500);
            }
            else
            {
                ApplyLidState();
            }
        }

        // ---------------- Lid animation (client) ----------------

        /// <summary>Loads the shape with its "lidopen" animation into the game's block animator.</summary>
        private void SetupAnimator()
        {
            if (animatorReady || animatorFailed) return;
            ICoreClientAPI capi = Api as ICoreClientAPI;
            object util = AnimUtil;
            if (capi == null || util == null || Block == null) return;

            try
            {
                System.Type shapeType = FindType("Vintagestory.API.Common.Shape");
                object loaded = null;
                if (shapeType != null)
                {
                    System.Reflection.MethodInfo tryGet = shapeType.GetMethod("TryGet", new System.Type[] { typeof(ICoreAPI), typeof(AssetLocation) });
                    if (tryGet != null) loaded = tryGet.Invoke(null, new object[] { Api, new AssetLocation("trashcan", "shapes/block/trashcan.json") });
                }
                if (loaded == null)
                {
                    animatorFailed = true;
                    Api.Logger.Warning("[trashcan] Could not load the trash can shape for the lid animation");
                    return;
                }
                object texSource = GetBlockTextureSource(capi.Tesselator, Block);
                if (texSource == null)
                {
                    animatorFailed = true;
                    Api.Logger.Warning("[trashcan] Could not get the trash can textures for the lid animation");
                    return;
                }
                CallMethod(util, "InitializeAnimator", "trashcan-lid-" + Block.Code.ToString(), loaded, texSource, new Vec3f(0, 0, 0));
                animatorReady = true;
            }
            catch (Exception e)
            {
                animatorFailed = true;
                Api.Logger.Warning("[trashcan] Lid animation unavailable: {0}", (e.InnerException ?? e).Message);
                Api.Logger.Warning("[trashcan] Lid animation details: {0}", (e.InnerException ?? e).ToString());
                Api.Logger.Warning("[trashcan] InitializeAnimator overloads: {0}", DescribeMethods(util, "InitializeAnimator"));
            }
        }

        /// <summary>
        /// Tesselator.GetTextureSource(Block) by reflection: one of its other overloads takes a Dictionary,
        /// and naming the method at all makes the mod compiler fail (CS0012).
        /// </summary>
        private static object GetBlockTextureSource(object tesselator, Block block)
        {
            foreach (System.Reflection.MethodInfo m in tesselator.GetType().GetMethods())
            {
                if (m.Name != "GetTextureSource") continue;
                System.Reflection.ParameterInfo[] ps = m.GetParameters();
                if (ps.Length == 0 || !ps[0].ParameterType.IsAssignableFrom(typeof(Block))) continue;
                object[] args = new object[ps.Length];
                args[0] = block;
                for (int i = 1; i < ps.Length; i++)
                {
                    args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue
                        : (ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null);
                }
                return m.Invoke(tesselator, args);
            }
            return null;
        }

        private static string DescribeMethods(object target, string name)
        {
            string result = "";
            foreach (System.Reflection.MethodInfo m in target.GetType().GetMethods())
            {
                if (m.Name != name) continue;
                string ps = "";
                foreach (System.Reflection.ParameterInfo p in m.GetParameters())
                {
                    ps += (ps.Length > 0 ? ", " : "") + p.ParameterType.Name + " " + p.Name;
                }
                result += " | " + m.ReturnType.Name + " " + name + "(" + ps + ")";
            }
            return result;
        }

        private static System.Type FindType(string fullName)
        {
            foreach (System.Reflection.Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                System.Type t = asm.GetType(fullName);
                if (t != null) return t;
            }
            return null;
        }

        /// <summary>Opens or closes the lid to match lidOpen.</summary>
        private void ApplyLidState()
        {
            if (Api == null || Api.Side != EnumAppSide.Client) return;
            // Only set the animator up the first time the lid actually needs to move.
            if (!lidOpen && !animatorReady) return;
            SetupAnimator();
            object util = AnimUtil;
            if (!animatorReady || util == null) return;

            try
            {
                if (lidOpen)
                {
                    System.Type metaType = FindType("Vintagestory.API.Common.AnimationMetaData");
                    object meta = Activator.CreateInstance(metaType);
                    SetMember(meta, "Animation", "lidopen");
                    SetMember(meta, "Code", "lidopen");
                    SetMember(meta, "AnimationSpeed", 1.8f);
                    SetMember(meta, "EaseOutSpeed", 6f);
                    SetMember(meta, "EaseInSpeed", 15f);
                    CallMethod(util, "StartAnimation", meta);
                }
                else
                {
                    CallMethod(util, "StopAnimation", "lidopen");
                }
            }
            catch (Exception e)
            {
                Api.Logger.Warning("[trashcan] Lid animation failed: {0}", (e.InnerException ?? e).Message);
            }
        }

        // ---------------- Lid state (server, synced) ----------------

        private bool AnyoneHasItOpen()
        {
            foreach (IPlayer player in Api.World.AllOnlinePlayers)
            {
                if (player != null && inventory.HasOpened(player)) return true;
            }
            return false;
        }

        /// <summary>Server: if whether anyone has it open changed, tell every player so their lid moves.</summary>
        private void UpdateLidState()
        {
            if (Api == null || Api.Side != EnumAppSide.Server || inventory == null) return;
            bool open = AnyoneHasItOpen();
            if (open == lidOpen) return;
            lidOpen = open;
            MarkDirty(false);
            PlayLidSound(open);
        }

        /// <summary>Server: the game's chest open/close sound at the trash can, for everyone nearby.</summary>
        private void PlayLidSound(bool open)
        {
            try
            {
                AssetLocation sound = new AssetLocation("game", open ? "sounds/block/chestopen" : "sounds/block/chestclose");
                // By reflection: PlaySoundAt has many overloads and the mod compiler is fussy about some of them.
                CallMethod(Api.World, "PlaySoundAt", sound, Pos.X + 0.5, Pos.InternalY + 0.5, Pos.Z + 0.5, null, true, 16f, 1f);
            }
            catch (Exception e)
            {
                Api.Logger.Warning("[trashcan] Could not play the lid sound: {0}", (e.InnerException ?? e).Message);
            }
        }

        private void OnSlotModified(int slotId)
        {
            MarkDirty(false);
        }

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            base.ToTreeAttributes(tree);
            tree.SetBool("lidOpen", lidOpen);
            inventory.ToTreeAttributes(tree.GetOrAddTreeAttribute("inventory"));
        }

        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldForResolving)
        {
            base.FromTreeAttributes(tree, worldForResolving);
            ITreeAttribute invTree = tree.GetTreeAttribute("inventory");
            if (invTree != null) inventory.FromTreeAttributes(invTree);
            bool open = tree.GetBool("lidOpen");
            if (Api != null && Api.Side == EnumAppSide.Client)
            {
                if (open != lidOpen)
                {
                    lidOpen = open;
                    ApplyLidState();
                }
            }
            else
            {
                lidOpen = open;
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

            BlockPos pos = Pos;
            dialog = new GuiDialogTrashCan(capi, Lang.Get("trashcan:dialog-title"), inventory, pos, Columns,
                () => capi.Network.SendBlockEntityPacket(pos, DeletePacketId, null));
            dialog.OnClosed += () =>
            {
                dialog = null;
                capi.Network.SendPacketClient(inventory.Close(byPlayer));
                capi.Network.SendBlockEntityPacket(pos, (int)EnumBlockEntityPacketId.Close, null);
            };
            dialog.TryOpen();
            lidOpen = true;
            ApplyLidState();
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
                UpdateLidState();
                return;
            }

            if (packetid == DeletePacketId)
            {
                // Only someone who has it open (and may use it) can empty it.
                if (!MayUse(player) || !inventory.HasOpened(player)) return;
                DeleteContents(player);
                return;
            }

            if (packetid == (int)EnumBlockEntityPacketId.Close)
            {
                if (player.InventoryManager != null) player.InventoryManager.CloseInventory(inventory);
                UpdateLidState();
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
            // Keeps the lid right, e.g. when a player with it open disconnects.
            if (inventory == null) return;
            UpdateLidState();
        }

        /// <summary>Server: deletes everything in the trash can and tells the player who pressed Delete.</summary>
        private void DeleteContents(IPlayer byPlayer)
        {
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

            IServerPlayer splayer = byPlayer as IServerPlayer;
            if (stacks == 0)
            {
                if (splayer != null) splayer.SendMessage(GlobalConstants.GeneralChatGroup, Lang.Get("trashcan:nothing-to-delete"), EnumChatType.Notification);
                return;
            }

            MarkDirty(true);
            if (splayer != null) splayer.SendMessage(GlobalConstants.GeneralChatGroup, Lang.Get("trashcan:deleted", items, stacks), EnumChatType.Notification);
            Api.Logger.Debug("[trashcan] {0} deleted {1} item(s) in {2} stack(s) at {3}", byPlayer.PlayerName, items, stacks, Pos);
        }

        // ---------------- Cleanup ----------------

        public override void OnBlockBroken(IPlayer byPlayer = null)
        {
            if (Api != null && Api.Side == EnumAppSide.Server && inventory != null)
            {
                inventory.DropAll(Pos.ToVec3d().Add(0.5, 0.5, 0.5));
            }
            base.OnBlockBroken(byPlayer);
        }

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

    /// <summary>
    /// The trash can window: 16 slots and a Delete button underneath. The button needs two clicks
    /// (the first one arms it for 3 seconds) so nothing is deleted by accident.
    /// </summary>
    public class GuiDialogTrashCan : GuiDialog
    {
        private readonly string title;
        private readonly InventoryBase inventory;
        private readonly BlockPos pos;
        private readonly int columns;
        private readonly Action onDelete;
        private long armedUntilMs = -1;

        public GuiDialogTrashCan(ICoreClientAPI capi, string title, InventoryBase inventory, BlockPos pos, int columns, Action onDelete) : base(capi)
        {
            this.title = title;
            this.inventory = inventory;
            this.pos = pos;
            this.columns = columns;
            this.onDelete = onDelete;
        }

        public override string ToggleKeyCombinationCode { get { return null; } }
        public override bool PrefersUngrabbedMouse { get { return true; } }

        public override void OnGuiOpened()
        {
            base.OnGuiOpened();
            armedUntilMs = -1;
            Compose();
        }

        private bool Armed { get { return armedUntilMs > 0 && capi.World.ElapsedMilliseconds < armedUntilMs; } }

        private void Compose()
        {
            int rows = (inventory.Count + columns - 1) / columns;
            ElementBounds grid = ElementStdBounds.SlotGrid(EnumDialogArea.None, 0, 30, columns, rows);
            double gridWidth = columns * 51 - 3;
            ElementBounds button = ElementBounds.Fixed(0, 30 + rows * 51 + 8, gridWidth, 30);

            ElementBounds bg = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bg.BothSizing = ElementSizing.FitToChildren;

            ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog
                .WithAlignment(EnumDialogArea.RightMiddle)
                .WithFixedAlignmentOffset(-GuiStyle.DialogToScreenPadding, 0);

            int[] slots = new int[inventory.Count];
            for (int i = 0; i < slots.Length; i++) slots[i] = i;

            SingleComposer = capi.Gui
                .CreateCompo("trashcan-" + pos.X + "-" + pos.Y + "-" + pos.Z, dialogBounds)
                .AddShadedDialogBG(bg)
                .AddDialogTitleBar(title, () => TryClose())
                .BeginChildElements(bg)
                    .AddItemSlotGrid(inventory, SendInvPacket, columns, slots, grid, "slots")
                    .AddSmallButton(Lang.Get(Armed ? "trashcan:delete-confirm" : "trashcan:delete-button"), OnDeleteClicked, button, EnumButtonStyle.Normal, "delete")
                .EndChildElements()
                .Compose();
        }

        /// <summary>Moving items in the slots: sent to the trash can on the server.</summary>
        private void SendInvPacket(object packet)
        {
            capi.Network.SendBlockEntityPacket(pos.X, pos.InternalY, pos.Z, packet);
        }

        private bool OnDeleteClicked()
        {
            if (Armed)
            {
                armedUntilMs = -1;
                if (onDelete != null) onDelete();
            }
            else
            {
                armedUntilMs = capi.World.ElapsedMilliseconds + 3000;
            }
            Compose();
            return true;
        }

        public override void OnRenderGUI(float deltaTime)
        {
            // The "click again" state runs out after 3 seconds.
            if (armedUntilMs > 0 && !Armed)
            {
                armedUntilMs = -1;
                Compose();
            }
            base.OnRenderGUI(deltaTime);
        }
    }
}
