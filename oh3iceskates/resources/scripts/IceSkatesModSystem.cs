using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace oh3iceskates
{
    public class IceSkatesModSystem : ModSystem
    {
        // Expose the config to other classes (read-only from the outside)
        public static IceSkatesConfig Config { get; private set; }

        // Tracks all physics controllers currently loaded in the world
        public HashSet<IceSkatesPhysics> ActivePhysicsControllers = new HashSet<IceSkatesPhysics>();

        private IServerNetworkChannel serverChannel;
        private IClientNetworkChannel clientChannel;

        public override void Start(ICoreAPI api)
        {
            base.Start(api);

            // Register our custom behavior and items
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
            // 1. Load the config (Only on the server!)
            LoadServerConfig(api);

            serverChannel = api.Network.GetChannel("iceskates");

            // Send config to players when they join
            api.Event.PlayerJoin += (byPlayer) =>
            {
                serverChannel.SendPacket(Config, byPlayer);
            };
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            // Initialize with defaults so the client doesn't crash before the server packet arrives!
            // Because we aren't calling api.LoadModConfig, no file is ever created on the client's PC.
            Config = new IceSkatesConfig();

            clientChannel = api.Network.GetChannel("iceskates");

            // 2. Listen for the authoritative config from the server
            clientChannel.SetMessageHandler<IceSkatesConfig>((packet) =>
            {
                Config = packet; // Overwrite the client's memory with the server's config
                api.Logger.Event("Received authoritative server config for Ice Skates.");
            });
        }

        private void LoadServerConfig(ICoreServerAPI api)
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
            foreach (var physics in ActivePhysicsControllers)
            {
                // The physics controller itself checks IsActive, so it will 
                // efficiently early-out if the player isn't actually on ice.
                physics.OnPhysicsTick(dt);
            }
        }
    }
}