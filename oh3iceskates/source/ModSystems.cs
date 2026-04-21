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
            serverChannel = api.Network.GetChannel("iceskates");

            // 1. Server exclusively loads and generates the config file.
            LoadServerConfig(api);

            // 2. When a client joins, the server enforces its rules by sending the config to them
            api.Event.PlayerJoin += (player) =>
            {
                serverChannel.SendPacket(LoadedConfig, player);
                api.Logger.VerboseDebug("Sent authoritative Ice Skates config to joining player {0}.", player.PlayerName);
            };
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            clientChannel = api.Network.GetChannel("iceskates");

            // 3. Client passively listens for the server's rules. 
            // We NO LONGER load or generate a local JSON file for the client here.
            clientChannel.SetMessageHandler<IceSkatesConfig>((packet) =>
            {
                LoadedConfig = packet;
                api.Logger.Event("Received authoritative Ice Skates config from the server.");
            });
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
            return LoadedConfig ?? new IceSkatesConfig();
        }
    }
}