using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace oh3iceskates
{
    public class IceSkatesModSystem : ModSystem
    {
        // Instance-level config.
        public IceSkatesConfig LoadedConfig { get; private set; }

        private IServerNetworkChannel serverChannel;
        private IClientNetworkChannel clientChannel;
        private ICoreServerAPI serverApi;
        private ICoreClientAPI clientApi;

        // Use a pre-allocated fallback to prevent massive garbage generation 
        // if queried by the physics tick before the server packet arrives.
        private static readonly IceSkatesConfig fallbackConfig = new IceSkatesConfig();

        public override void Start(ICoreAPI api)
        {
            base.Start(api);

            // Register custom behavior and items
            api.RegisterEntityBehaviorClass("iceskater", typeof(EntityBehaviorIceSkater));
            api.RegisterItemClass("ItemIceSkates", typeof(ItemIceSkates));

            // Universal Network Registration
            api.Network.RegisterChannel("iceskates")
                .RegisterMessageType<IceSkatesConfig>();
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            serverApi = api;
            serverChannel = api.Network.GetChannel("iceskates");

            // 1. Server exclusively loads and generates the config file.
            LoadServerConfig(api);

            // 2. When a client joins, the server enforces its rules by sending the config to them.
            // Delegate used instead of an anonymous lambda to prevent closure allocations.
            api.Event.PlayerJoin += OnPlayerJoin;

            // 3. Register Admin Chat Command
            api.ChatCommands.Create("skates")
                .WithDescription("Ice Skates configuration commands")
                .RequiresPrivilege(Privilege.controlserver)
                .BeginSubCommand("reload")
                    .WithDescription("Reloads the Oh3IceSkatesConfig.json file and broadcasts it to all players")
                    .HandleWith(OnReloadCommand)
                .EndSubCommand();
        }

        private void OnPlayerJoin(IServerPlayer player)
        {
            serverChannel.SendPacket(LoadedConfig, player);
            serverApi.Logger.VerboseDebug("Sent authoritative Ice Skates config to joining player {0}.", player.PlayerName);
        }

        private TextCommandResult OnReloadCommand(TextCommandCallingArgs args)
        {
            LoadServerConfig(serverApi);
            serverChannel.BroadcastPacket(LoadedConfig);
            return TextCommandResult.Success("Ice Skates configuration successfully reloaded and broadcasted to all connected players.");
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            clientApi = api;
            clientChannel = api.Network.GetChannel("iceskates");

            // 3. Client passively listens for the server's rules. 
            // We NO LONGER load or generate a local JSON file for the client here.
            clientChannel.SetMessageHandler<IceSkatesConfig>(OnConfigReceived);
        }

        private void OnConfigReceived(IceSkatesConfig packet)
        {
            LoadedConfig = packet;
            clientApi.Logger.Event("Received authoritative Ice Skates config from the server.");
        }

        // Changed to require ICoreServerAPI. Clients are no longer permitted to run this.
        private void LoadServerConfig(ICoreServerAPI api)
        {
            try
            {
                LoadedConfig = api.LoadModConfig<IceSkatesConfig>("Oh3IceSkatesConfig.json");

                // If the file didn't exist, create it and save the defaults
                if (LoadedConfig == null)
                {
                    LoadedConfig = new IceSkatesConfig();
                    api.StoreModConfig(LoadedConfig, "Oh3IceSkatesConfig.json");
                }
            }
            catch (Exception e)
            {
                api.Logger.Error("Could not load Ice Skates config! Loading default settings instead. Error: {0}", e);
                LoadedConfig = new IceSkatesConfig();
            }
        }

        /// <summary>
        /// Retrieves the synced authoritative config. 
        /// If the network packet hasn't arrived yet on the client, it safely falls back to the defaults.
        /// </summary>
        public IceSkatesConfig GetConfigForPlayer(string playerUid)
        {
            // O(1) fallback check. Never instantiate new reference types here since 
            // behaviors poll this property during the high-speed physics tick!
            return LoadedConfig ?? fallbackConfig;
        }
    }
}