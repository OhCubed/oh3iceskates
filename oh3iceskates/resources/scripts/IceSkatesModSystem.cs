using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace oh3iceskates
{
    public class IceSkatesModSystem : ModSystem
    {
        // Client-side local config (fallback/local authority)
        public static IceSkatesConfig Config { get; private set; }

        // Server-side multiplayer tracking: Maps Player UID to their authoritative config
        public Dictionary<string, IceSkatesConfig> PlayerConfigs { get; private set; } = new Dictionary<string, IceSkatesConfig>();

        // Replaced HashSet with a List. Lists are much more cache-friendly and allow 
        // allocation-free iteration via 'for' loops, eliminating GC spikes.
        public List<IceSkatesPhysics> ActivePhysicsControllers = new List<IceSkatesPhysics>();

        private IServerNetworkChannel serverChannel;
        private IClientNetworkChannel clientChannel;

        public override void Start(ICoreAPI api)
        {
            base.Start(api);

            // Register custom behavior and items
            api.RegisterEntityBehaviorClass("iceskater", typeof(EntityBehaviorIceSkater));
            api.RegisterItemClass("ItemIceSkates", typeof(ItemIceSkates));

            // Register ONE global listener for all skating physics (0ms = every tick)
            api.Event.RegisterGameTickListener(OnGlobalPhysicsTick, 0);

            // Universal Network Registration: Ensure both sides know about this packet
            api.Network.RegisterChannel("iceskates")
                .RegisterMessageType<IceSkatesConfig>();
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            serverChannel = api.Network.GetChannel("iceskates");

            // Listen for the authoritative config from the client and store it per-player
            serverChannel.SetMessageHandler<IceSkatesConfig>((player, packet) =>
            {
                PlayerConfigs[player.PlayerUID] = packet;
                api.Logger.VerboseDebug("Received authoritative client config for Ice Skates from player {0}.", player.PlayerName);
            });

            // Prevent memory leaks by cleaning up the dictionary when a player leaves
            api.Event.PlayerDisconnect += (player) =>
            {
                PlayerConfigs.Remove(player.PlayerUID);
            };
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            // 1. Load the config (Client authoritative)
            LoadClientConfig(api);

            clientChannel = api.Network.GetChannel("iceskates");

            // Send config to the server once the client finishes loading the level
            api.Event.LevelFinalize += () =>
            {
                clientChannel.SendPacket(Config);
            };
        }

        private void LoadClientConfig(ICoreClientAPI api)
        {
            try
            {
                Config = api.LoadModConfig<IceSkatesConfig>("Oh3IceSkatesConfig.json");

                // If the file didn't exist, create it and save the defaults
                if (Config == null)
                {
                    Config = new IceSkatesConfig();
                    api.StoreModConfig(Config, "Oh3IceSkatesConfig.json");
                }
            }
            catch (Exception e)
            {
                api.Logger.Error("Could not load Ice Skates config! Loading default settings instead. Error: {0}", e);
                Config = new IceSkatesConfig();
            }
        }

        private void OnGlobalPhysicsTick(float dt)
        {
            // A reverse for-loop generates 0 memory allocations (no GC spikes).
            // It also allows elements to safely be removed from the list during the loop without throwing out-of-bounds errors.
            for (int i = ActivePhysicsControllers.Count - 1; i >= 0; i--)
            {
                ActivePhysicsControllers[i].OnPhysicsTick(dt);
            }
        }

        /// <summary>
        /// Retrieves the correct config based on the execution side and player.
        /// Call this from your behavior/physics class instead of accessing Config directly.
        /// </summary>
        public IceSkatesConfig GetConfigForPlayer(string playerUid)
        {
            if (PlayerConfigs.TryGetValue(playerUid, out var config))
            {
                return config;
            }

            // Fallback to local config if server dictionary misses, or if called on the client.
            return Config ?? new IceSkatesConfig();
        }
    }
}