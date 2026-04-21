using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Datastructures;

namespace oh3iceskates
{
    // 1. Separate Physics Controller
    public class IceSkatesPhysics
    {
        public bool IsActive { get; set; }
        public float SkateSpeedBonus { get; set; } = 1.0f;
        public float SkateHandling { get; set; } = 0.5f;

        // NEW: Skate Bite deviation threshold (in degrees)
        public float SkateBite { get; set; } = 5f;

        // NEW: Skate Acceleration
        public float SkateAcceleration { get; set; } = 2f;

        // NEW: Skate Friction
        public float SkateFriction { get; set; } = 0.995f;

        // NEW: Skate Spring (Momentum return multiplier)
        public float SkateSpring { get; set; } = 0.5f;

        // Cached from behavior to prevent string lookups in the 0ms tick
        public float PlayerWalkSpeed { get; set; } = 1.0f;
        public IceSkatesConfig Config { get; set; }

        private Entity entity;
        private EntityPlayer entityPlayer;
        private bool? isLocalClient;

        // Variables used to bypass vanilla engine friction
        private double lastVx = 0;
        private double lastVz = 0;
        private bool wasActive = false;

        // Slalom / Carve Boost Tracking
        private double lastWishAngle = 0;
        private int lastTurnDirection = 0;
        private double boostTimerMs = 0;
        private bool hasLastWishAngle = false;

        // Spring / Momentum Dump Tracking
        private double speedBeforeBraking = 0.0;
        private bool wasSneaking = false;

        public IceSkatesPhysics(Entity entity)
        {
            this.entity = entity;
            this.entityPlayer = entity as EntityPlayer;
        }

        // Called every physics tick by the centralized ModSystem
        public void OnPhysicsTick(float dt)
        {
            if (!IsActive || entityPlayer == null)
            {
                wasActive = false;
                return;
            }

            // --- MULTIPLAYER FIX ---
            // Player physics is client-authoritative. 
            // The Server shouldn't override motion manually (causes rubber-banding).
            // The Client shouldn't simulate motion for remote players (causes stuttering).
            if (entity.World.Side == EnumAppSide.Server) return;

            // Safely resolve and cache the local client authority check after the world finishes loading
            if (isLocalClient == null)
            {
                var capi = entity.World.Api as ICoreClientAPI;
                if (capi?.World?.Player != null)
                {
                    isLocalClient = (entityPlayer.PlayerUID == capi.World.Player.PlayerUID);
                }
                else
                {
                    return; // Wait until player is fully loaded
                }
            }

            if (isLocalClient == false) return;
            // -----------------------

            // Grab the globally synced config. Fallback just in case config hasn't synced
            IceSkatesConfig cfg = Config ?? IceSkatesModSystem.Config;
            if (cfg == null) return;

            bool isSneaking = entityPlayer.Controls.Sneak;

            double currentMaxSpeed = cfg.MaxSpeed;
            double topSpeedLimit = currentMaxSpeed * SkateSpeedBonus * PlayerWalkSpeed;
            double currentAcceleration = SkateAcceleration;

            // Fetch BiteHandling from config (default to 0.8)
            double currentBiteHandling = cfg.BiteHandling;

            // Slalom specific config fetching
            double currentSlalomBoost = cfg.SlalomBoost;
            double currentSlalomWindow = cfg.SlalomWindow;
            double currentSpeedCeiling = cfg.SpeedCeiling;
            double currentSlalomSweetspot = cfg.SlalomSweetspot;

            // Fetch Brake Friction from config
            double currentBrakeFriction = cfg.BrakeFriction;

            bool debugMode = cfg.DebugMode;

            // Pre-calculate the exponent needed to stretch the bell curve's 80% threshold 
            // across the configured Sweetspot percentage of the window.
            double safeSweetspot = Math.Max(0.01, Math.Min(0.99, currentSlalomSweetspot));
            double curveExponent = Math.Log(0.8) / Math.Log(Math.Cos(safeSweetspot * GameMath.PI / 2.0));

            if (entity.OnGround)
            {
                // Calculate the bell curve for the slalom boost
                double currentCurve = 0.0;

                if (boostTimerMs > 0)
                {
                    boostTimerMs -= dt * 1000.0;
                    if (boostTimerMs < 0) boostTimerMs = 0;

                    if (boostTimerMs > 0 && currentSlalomWindow > 0)
                    {
                        double progress = boostTimerMs / currentSlalomWindow;
                        if (progress > 0.0 && progress < 1.0)
                        {
                            // A sine wave naturally makes a bell shape. 
                            // By raising it to an exponent, we can dynamically widen or narrow the peak!
                            currentCurve = Math.Pow(GameMath.Sin((float)(progress * GameMath.PI)), curveExponent);
                        }
                    }
                }

                double boostMultiplier = 1.0 + (currentSlalomBoost * currentCurve);

                double vx = entity.Pos.Motion.X;
                double vz = entity.Pos.Motion.Z;

                // 1. Bypass Vanilla Friction
                // If we collided with a wall, we must respect the engine's velocity (which zeroes out).
                // Otherwise, we load our stored velocity from the previous tick, erasing vanilla ground drag.
                if (entity.CollidedHorizontally || !wasActive)
                {
                    lastVx = vx;
                    lastVz = vz;
                    wasActive = true;
                }
                else
                {
                    vx = lastVx;
                    vz = lastVz;
                }

                // Track the speed right before friction is applied to monitor spring momentum
                double currentAbsSpeed = Math.Sqrt(vx * vx + vz * vz);

                if (isSneaking && !wasSneaking)
                {
                    speedBeforeBraking = currentAbsSpeed;
                }

                bool releasedCrouch = !isSneaking && wasSneaking;
                wasSneaking = isSneaking;

                // Lose stored momentum if essentially stationary
                if (currentAbsSpeed < 0.01)
                {
                    speedBeforeBraking = 0.0;
                }

                // 2. Apply Custom Configured Friction
                // Dynamic Brake Friction scaling
                double currentFrictionMult = SkateFriction;
                if (isSneaking)
                {
                    double speedRatio = 0.0;
                    if (speedBeforeBraking > 0)
                    {
                        speedRatio = Math.Min(1.0, Math.Max(0.0, currentAbsSpeed / speedBeforeBraking));
                    }

                    // Power curve: x^0.097
                    // This creates an aggressively compressed curve where exactly 80% of the value drop happens in the last 10% of the speed ratio.
                    // It shrinks incredibly slowly at the top end and plummets violently right before a standstill.
                    double curve = Math.Pow(speedRatio, 0.097);

                    // FIX: Dynamic floor relative to the globally configured BrakeFriction.
                    double brakeFloor = Math.Max(0.0, currentBrakeFriction - 0.1);
                    currentFrictionMult = brakeFloor + (currentBrakeFriction - brakeFloor) * curve;
                }

                // Config uses 1.0 for Frictionless, and 0.0 for Max Friction.
                double timeNormalizedFriction = 1.0;
                if (currentFrictionMult < 1.0)
                {
                    // Apply frame-rate independent percentage drop
                    double frictionFactor = Math.Max(0, currentFrictionMult);
                    timeNormalizedFriction = Math.Pow(frictionFactor, dt * 30.0);
                    vx *= timeNormalizedFriction;
                    vz *= timeNormalizedFriction;
                }

                // Record the momentum after friction but before any new acceleration.
                // This lets us safely decay excess speed without instantly braking the player.
                double speedBeforeAccelSq = vx * vx + vz * vz;

                // 3. Calculate Wish Direction (where the player wants to go)
                double wishX = 0;
                double wishZ = 0;
                double rawInputX = 0; // Pure camera + keys direction
                double rawInputZ = 0;

                if (entityPlayer.Controls.TriesToMove)
                {
                    // GC SPIKE FIX: We no longer call entity.Pos.GetViewVector().
                    // That method creates a 'new Vec3f()' every tick, allocating memory.
                    // Instead, we manually pull the Yaw and do the math ourselves allocation-free!
                    float yaw = entity.Pos.Yaw;
                    double camFwdX = GameMath.Sin(yaw);
                    double camFwdZ = GameMath.Cos(yaw);

                    double camRightX = -camFwdZ;
                    double camRightZ = camFwdX;

                    if (entityPlayer.Controls.Forward) { wishX += camFwdX; wishZ += camFwdZ; }
                    if (entityPlayer.Controls.Backward) { wishX -= camFwdX; wishZ -= camFwdZ; }
                    if (entityPlayer.Controls.Left) { wishX -= camRightX; wishZ -= camRightZ; }
                    if (entityPlayer.Controls.Right) { wishX += camRightX; wishZ += camRightZ; }

                    double wishLength = Math.Sqrt(wishX * wishX + wishZ * wishZ);

                    if (wishLength > 0.001)
                    {
                        wishX /= wishLength;
                        wishZ /= wishLength;

                        // Capture the unadulterated camera/movement vector before the 180-degree anti-lock modifies it
                        rawInputX = wishX;
                        rawInputZ = wishZ;

                        // --- SLALOM / CARVE BOOST LOGIC ---
                        double currentWishAngle = Math.Atan2(wishX, wishZ);
                        if (hasLastWishAngle)
                        {
                            double deltaAngle = currentWishAngle - lastWishAngle;

                            // Normalize angle difference to handle wrapping around Pi
                            while (deltaAngle <= -GameMath.PI) deltaAngle += GameMath.TWOPI;
                            while (deltaAngle > GameMath.PI) deltaAngle -= GameMath.TWOPI;

                            int currentTurnDirection = 0;
                            // 0.005 radians per tick acts as a gentle deadzone to ignore micro-jitters
                            if (deltaAngle > 0.005) currentTurnDirection = 1;
                            else if (deltaAngle < -0.005) currentTurnDirection = -1;

                            // The player must turn in the OPPOSITE direction of their last carve to get the bonus again!
                            if (currentTurnDirection != 0 && currentTurnDirection != lastTurnDirection)
                            {
                                lastTurnDirection = currentTurnDirection;
                                boostTimerMs = currentSlalomWindow;

                                // Recalculate curve and multiplier immediately since we just reset the timer
                                currentCurve = 0.0;
                                boostMultiplier = 1.0 + (currentSlalomBoost * currentCurve);
                            }
                        }
                        lastWishAngle = currentWishAngle;
                        hasLastWishAngle = true;
                        // ----------------------------------

                        // 4. Carving / Momentum Recycling (Strong Lerp)
                        // We bend the current velocity vector towards the desired wish direction, 
                        // preserving the magnitude entirely so no momentum is lost during turns.
                        double currentSpeed = Math.Sqrt(vx * vx + vz * vz);

                        // Only carve if we're actually moving enough
                        if (currentSpeed > 0.01)
                        {
                            double currentDirX = vx / currentSpeed;
                            double currentDirZ = vz / currentSpeed;

                            // Turn speed dictates how sharply the player corners.
                            // 5.0 * dt gives a responsive, tight curve.
                            double turnSpeed = 5.0 * dt;

                            // Prevent getting stuck dynamically on perfect 180-degree turns
                            double dot = currentDirX * wishX + currentDirZ * wishZ;
                            if (dot < -0.99)
                            {
                                wishX += currentDirZ * 0.1;
                                wishZ -= currentDirX * 0.1;
                                double wLen = Math.Sqrt(wishX * wishX + wishZ * wishZ);
                                wishX /= wLen;
                                wishZ /= wLen;
                            }

                            double newDirX = currentDirX + (wishX - currentDirX) * turnSpeed;
                            double newDirZ = currentDirZ + (wishZ - currentDirZ) * turnSpeed;

                            double newDirLen = Math.Sqrt(newDirX * newDirX + newDirZ * newDirZ);
                            if (newDirLen > 0.001)
                            {
                                newDirX /= newDirLen;
                                newDirZ /= newDirLen;

                                // Target ideal velocity if we had 100% handling
                                double idealVx = newDirX * currentSpeed;
                                double idealVz = newDirZ * currentSpeed;

                                // --- Skate Bite Logic ---
                                double appliedHandling = SkateHandling;

                                // Use the dot product as a highly efficient way to check angle deviation
                                // Cosine of the bite angle in radians
                                double biteThresholdDot = Math.Cos(SkateBite * (GameMath.PI / 180.0));

                                // If dot product >= threshold, the deviation is smaller than or equal to the SkateBite angle
                                if (dot >= biteThresholdDot)
                                {
                                    appliedHandling = Math.Max(appliedHandling, currentBiteHandling);
                                }

                                // Apply SkateHandling (0.0 to 1.0)
                                // 1.0 = Perfect turn conservation
                                // 0.5 = 50% momentum turns, 50% slips along original path (skidding)
                                double handling = GameMath.Clamp(appliedHandling, 0.0, 1.0);

                                vx = (vx * (1.0 - handling)) + (idealVx * handling);
                                vz = (vz * (1.0 - handling)) + (idealVz * handling);
                            }
                        }

                        // 5. Gradual Acceleration
                        // Allow the player to temporarily accelerate towards a higher ceiling if boosting
                        double boostedTopSpeed = topSpeedLimit;
                        if (boostTimerMs > 0)
                        {
                            // The player's walk speed stat is a multiplier (1.0). We multiply it by 
                            // the vanilla engine's base walk speed (0.09 blocks/tick) to safely add it.
                            double slalomCeiling = currentSpeedCeiling + (0.09 * PlayerWalkSpeed);

                            // If their coasting top speed is naturally higher, we don't punish them for slaloming
                            if (slalomCeiling > topSpeedLimit)
                            {
                                // Smoothly scale the ceiling using the bell curve!
                                boostedTopSpeed = topSpeedLimit + (slalomCeiling - topSpeedLimit) * currentCurve;
                            }
                        }

                        // Acceleration is smoothly boosted by the bell curve
                        double accelStep = currentAcceleration * dt * boostMultiplier;
                        double currentSpeedInWishDir = vx * wishX + vz * wishZ;

                        // Only accelerate if we haven't reached max speed in the desired direction
                        if (currentSpeedInWishDir < boostedTopSpeed)
                        {
                            double addSpeed = boostedTopSpeed - currentSpeedInWishDir;
                            double accelSpeed = accelStep * boostedTopSpeed;

                            // FIX: Friction Equilibrium Plateau
                            // If the player is actively accelerating but friction is dragging them down, 
                            // they can get trapped at an equilibrium speed lower than the topSpeed limit.
                            // Here we inject the exact amount of speed lost to friction back into the acceleration 
                            // force to guarantee they can physically reach the config speed cap.
                            if (timeNormalizedFriction < 1.0 && currentSpeedInWishDir > 0)
                            {
                                double frictionLoss = currentSpeedInWishDir * (1.0 / timeNormalizedFriction - 1.0);
                                accelSpeed += frictionLoss;
                            }

                            if (accelSpeed > addSpeed) accelSpeed = addSpeed;

                            vx += accelSpeed * wishX;
                            vz += accelSpeed * wishZ;
                        }
                    }
                }
                else
                {
                    // Reset stroke tracking if the player lets go of the movement keys
                    hasLastWishAngle = false;
                }

                // --- NEW: Skate Spring (Momentum Dump) ---
                if (releasedCrouch)
                {
                    double speedLost = Math.Max(0, speedBeforeBraking - currentAbsSpeed);
                    double dumpSpeed = speedLost * SkateSpring;

                    if (dumpSpeed > 0)
                    {
                        double dirX = rawInputX;
                        double dirZ = rawInputZ;

                        // Fallback if not actively trying to move: use current velocity direction or look direction
                        if (dirX == 0 && dirZ == 0)
                        {
                            if (currentAbsSpeed > 0.001)
                            {
                                dirX = vx / currentAbsSpeed;
                                dirZ = vz / currentAbsSpeed;
                            }
                            else
                            {
                                float yaw = entity.Pos.Yaw;
                                dirX = GameMath.Sin(yaw);
                                dirZ = GameMath.Cos(yaw);
                            }
                        }

                        // Snap the ENTIRE velocity vector to the raw input direction.
                        // This completely bypasses normal carving turn speed limits!
                        double newSpeed = currentAbsSpeed + dumpSpeed;
                        vx = dirX * newSpeed;
                        vz = dirZ * newSpeed;

                        // Update speedBeforeAccelSq so the new momentum isn't instantly crushed by the dynamic speed cap
                        speedBeforeAccelSq = vx * vx + vz * vz;
                    }

                    speedBeforeBraking = 0;
                }

                // 6. Dynamic Maximum Speed Cap.
                double boostedLimit = topSpeedLimit;

                if (boostTimerMs > 0)
                {
                    double slalomLimit = currentSpeedCeiling + (0.09 * PlayerWalkSpeed);
                    if (slalomLimit > topSpeedLimit)
                    {
                        // Bell curve the speed limit so it "breathes" smoothly with the acceleration!
                        boostedLimit = topSpeedLimit + (slalomLimit - topSpeedLimit) * currentCurve;
                    }
                }

                double boostedLimitSq = boostedLimit * boostedLimit;

                // The dynamic cap accommodates both our slalom ceiling AND any external momentum.
                // If the player drops the rhythm and loses the buff, dynamicCapSq smoothly catches 
                // them and lets friction organically decelerate them back down to the normal limit!
                double dynamicCapSq = Math.Max(boostedLimitSq, speedBeforeAccelSq);
                double absSpeedSq = vx * vx + vz * vz;

                if (absSpeedSq > dynamicCapSq)
                {
                    double capScale = Math.Sqrt(dynamicCapSq / absSpeedSq);
                    vx *= capScale;
                    vz *= capScale;
                }

                // Debug output
                if (debugMode)
                {
                    double currentSpeedCalc = Math.Sqrt(vx * vx + vz * vz);
                    double dynamicCapCalc = Math.Sqrt(dynamicCapSq);
                    Console.WriteLine($"[IceSkates] CurSpeed: {currentSpeedCalc:F4} | TopSpeed: {topSpeedLimit:F4} | DynCap: {dynamicCapCalc:F4} | SlalomLimit: {boostedLimit:F4} | Curve: {currentCurve:F2}");
                }

                // 7. Apply to entity and store state for the next tick
                entity.Pos.Motion.X = vx;
                entity.Pos.Motion.Z = vz;

                lastVx = vx;
                lastVz = vz;
            }
            else
            {
                // Mid-air logic: No vanilla air control, keep skating momentum & drag
                hasLastWishAngle = false; // Reset stroke rhythm so you can start carving the moment you land

                // Mid-air: Keep tracking sneak state to prevent improper landing boosts
                wasSneaking = isSneaking;
                speedBeforeBraking = 0; // Lose spring bonus if jumping

                if (wasActive)
                {
                    double airSpeedSq = lastVx * lastVx + lastVz * lastVz;

                    if (entity.CollidedHorizontally)
                    {
                        // Stop our custom momentum if we smack into a wall mid-air
                        lastVx = entity.Pos.Motion.X;
                        lastVz = entity.Pos.Motion.Z;
                    }
                    else if (airSpeedSq < 0.01) // 0.01 absolute speed squared (stationary breakpoint)
                    {
                        // At a stationary speed, restore normal jumping/air control by NOT overwriting motion
                        lastVx = entity.Pos.Motion.X;
                        lastVz = entity.Pos.Motion.Z;
                    }
                    else
                    {
                        // Apply dynamic friction while flying through the air
                        double currentFrictionMult = SkateFriction;
                        if (isSneaking)
                        {
                            double speedRatio = 0.0;
                            if (topSpeedLimit > 0)
                            {
                                speedRatio = Math.Min(1.0, Math.Max(0.0, Math.Sqrt(airSpeedSq) / topSpeedLimit));
                            }

                            // Power curve matching ground logic (80% drop in last 10%)
                            double curve = Math.Pow(speedRatio, 0.097);
                            double brakeFloor = Math.Max(0.0, currentBrakeFriction - 0.1);
                            currentFrictionMult = brakeFloor + (currentBrakeFriction - brakeFloor) * curve;
                        }

                        if (currentFrictionMult < 1.0)
                        {
                            double frictionFactor = Math.Max(0, currentFrictionMult);
                            double timeNormalizedFriction = Math.Pow(frictionFactor, dt * 30.0);
                            lastVx *= timeNormalizedFriction;
                            lastVz *= timeNormalizedFriction;
                        }

                        // Overwriting the engine's X and Z here strips away vanilla air-control
                        entity.Pos.Motion.X = lastVx;
                        entity.Pos.Motion.Z = lastVz;
                    }
                }
                else
                {
                    // Not skating prior to being airborne. Keep our tracker synced.
                    lastVx = entity.Pos.Motion.X;
                    lastVz = entity.Pos.Motion.Z;
                }
            }
        }
    }

    // 2. The Main Behavior
    public class EntityBehaviorIceSkater : EntityBehavior
    {
        private IceSkatesModSystem modSystem;
        private double damageAccumulator = 0;
        private bool onIceWithSkates = false;
        private float skateSpeedBonus = 0;
        private float skateHandling = 0;
        private float skateBite = 5f;
        private float skateAcceleration = 2f;
        private float skateFriction = 0.995f;
        private float skateSpring = 0.5f;

        private Vec3d lastPos = new Vec3d();
        private EntityPlayer entityPlayer;
        private IInventory characterInv;
        private BlockPos tmpPos = new BlockPos();

        private IceSkatesPhysics physicsController;

        // Tracks the server's last known state to prevent network spam
        private bool lastServerWearingSkates = false;
        private float lastServerSkateSpeedBonus = -1f;
        private float lastServerSkateHandling = -1f;
        private float lastServerSkateBite = -1f;
        private float lastServerSkateAcceleration = -1f;
        private float lastServerSkateFriction = -1f;
        private float lastServerSkateSpring = -1f;

        public EntityBehaviorIceSkater(Entity entity) : base(entity)
        {
        }

        public override string PropertyName() => "iceskater";

        public override void Initialize(EntityProperties properties, JsonObject attributes)
        {
            base.Initialize(properties, attributes);

            // Cache ModSystem once here instead of multiple times during runtime
            modSystem = entity.World.Api.ModLoader.GetModSystem<IceSkatesModSystem>();

            entityPlayer = entity as EntityPlayer;
            lastPos.Set(entity.Pos.X, entity.Pos.Y, entity.Pos.Z);

            physicsController = new IceSkatesPhysics(entity);

            // Using the List allocation free method established in IceSkatesModSystem.cs
            modSystem?.ActivePhysicsControllers.Add(physicsController);
        }

        public override void OnGameTick(float deltaTime)
        {
            base.OnGameTick(deltaTime);

            if (entityPlayer?.Player == null) return;

            // Pull the correct configuration from the ModSystem Dictionary early
            IceSkatesConfig currentConfig = modSystem?.GetConfigForPlayer(entityPlayer.PlayerUID);

            if (!entity.Alive || entity.State != EnumEntityState.Active)
            {
                SetSkatingActive(false, 0, 0, 5f, 2f, 0.995f, 0.5f);
                return;
            }

            // Pass the slower-updating WalkSpeed stat and config down to the 0ms Physics class
            if (physicsController != null)
            {
                physicsController.PlayerWalkSpeed = entity.Stats.GetBlended("walkspeed");
                physicsController.Config = currentConfig;
            }

            double dx = entity.Pos.X - lastPos.X;
            double dz = entity.Pos.Z - lastPos.Z;
            double distTraveled = Math.Sqrt(dx * dx + dz * dz);

            // Cap to prevent massive jumps (like teleports) from dealing instant durability damage
            if (distTraveled > 10.0) distTraveled = 0;
            lastPos.Set(entity.Pos.X, entity.Pos.Y, entity.Pos.Z);

            ItemSlot footSlot = null;

            if (entity.World.Side == EnumAppSide.Server)
            {
                if (characterInv == null)
                {
                    characterInv = entityPlayer.Player.InventoryManager.GetOwnInventory("character");
                }

                bool serverWearingSkates = false;
                float serverSkateSpeedBonus = 1.2f;
                float serverSkateHandling = 0.5f;
                float serverSkateBite = 5f;
                float serverSkateAcceleration = 2f;
                float serverSkateFriction = 0.995f;
                float serverSkateSpring = 0.5f;

                if (characterInv != null)
                {
                    footSlot = characterInv[(int)EnumCharacterDressType.Foot];
                    serverWearingSkates = footSlot != null && !footSlot.Empty &&
                                          footSlot.Itemstack.Collectible.Attributes?["isSkates"]?.AsBool(false) == true;

                    if (serverWearingSkates)
                    {
                        serverSkateSpeedBonus = GetSkateSpeedBonus(footSlot.Itemstack);
                        serverSkateHandling = GetSkateHandling(footSlot.Itemstack);
                        serverSkateBite = GetSkateBite(footSlot.Itemstack);
                        serverSkateAcceleration = GetSkateAcceleration(footSlot.Itemstack, serverSkateAcceleration);
                        serverSkateFriction = GetSkateFriction(footSlot.Itemstack, serverSkateFriction);
                        serverSkateSpring = GetSkateSpring(footSlot.Itemstack, serverSkateSpring);
                    }
                }

                // Sync the properties dynamically to the client ONLY if they changed.
                // Setting WatchedAttributes every tick forces network syncs and triggers 
                // UI redraws on the client, which causes severe inventory and hotbar lag.
                if (serverWearingSkates != lastServerWearingSkates ||
                    serverSkateSpeedBonus != lastServerSkateSpeedBonus ||
                    serverSkateHandling != lastServerSkateHandling ||
                    serverSkateBite != lastServerSkateBite ||
                    serverSkateAcceleration != lastServerSkateAcceleration ||
                    serverSkateFriction != lastServerSkateFriction ||
                    serverSkateSpring != lastServerSkateSpring)
                {
                    entity.WatchedAttributes.SetBool("wearingSkates", serverWearingSkates);
                    entity.WatchedAttributes.SetFloat("skateSpeedBonus", serverSkateSpeedBonus);
                    entity.WatchedAttributes.SetFloat("skateHandling", serverSkateHandling);
                    entity.WatchedAttributes.SetFloat("skateBite", serverSkateBite);
                    entity.WatchedAttributes.SetFloat("skateAcceleration", serverSkateAcceleration);
                    entity.WatchedAttributes.SetFloat("skateFriction", serverSkateFriction);
                    entity.WatchedAttributes.SetFloat("skateSpring", serverSkateSpring);

                    lastServerWearingSkates = serverWearingSkates;
                    lastServerSkateSpeedBonus = serverSkateSpeedBonus;
                    lastServerSkateHandling = serverSkateHandling;
                    lastServerSkateBite = serverSkateBite;
                    lastServerSkateAcceleration = serverSkateAcceleration;
                    lastServerSkateFriction = serverSkateFriction;
                    lastServerSkateSpring = serverSkateSpring;
                }
            }

            // Client and server grab synced values
            bool wearingSkates = entity.WatchedAttributes.GetBool("wearingSkates", false);
            float currentItemBonus = entity.WatchedAttributes.GetFloat("skateSpeedBonus", 1.2f);
            float currentItemHandling = entity.WatchedAttributes.GetFloat("skateHandling", 0.5f);
            float currentItemBite = entity.WatchedAttributes.GetFloat("skateBite", 5f);
            float currentItemAcceleration = entity.WatchedAttributes.GetFloat("skateAcceleration", 2f);
            float currentItemFriction = entity.WatchedAttributes.GetFloat("skateFriction", 0.995f);
            float currentItemSpring = entity.WatchedAttributes.GetFloat("skateSpring", 0.5f);

            // Check slightly further down (0.2 instead of 0.05) to prevent micro-bounces 
            // from making the game think we left the ice and rapidly toggling the stats.
            tmpPos.Set((int)Math.Floor(entity.Pos.X), (int)Math.Floor(entity.Pos.Y - 0.2), (int)Math.Floor(entity.Pos.Z));
            Block blockBelow = entity.World.BlockAccessor.GetBlock(tmpPos);

            bool isIce = blockBelow?.BlockMaterial == EnumBlockMaterial.Ice;

            // If the player jumped or fell while on ice, keep the physics active while they are mid-air
            bool isJumpingFromIce = onIceWithSkates && !entity.OnGround;

            if (wearingSkates && (isIce || isJumpingFromIce))
            {
                SetSkatingActive(true, currentItemBonus, currentItemHandling, currentItemBite, currentItemAcceleration, currentItemFriction, currentItemSpring);

                if (entity.World.Side == EnumAppSide.Server && entityPlayer.Controls.TriesToMove && entity.OnGround)
                {
                    if (currentConfig?.BlocksPerDamage > 0 && footSlot != null)
                    {
                        damageAccumulator += distTraveled;

                        // Batch durability damage into chunks of 5 to prevent constant 
                        // inventory sync packets, which cause severe hotbar and UI lag.
                        double damageThreshold = currentConfig.BlocksPerDamage * 5.0;

                        if (damageAccumulator >= damageThreshold)
                        {
                            int damageAmount = (int)(damageAccumulator / currentConfig.BlocksPerDamage);
                            DamageSkates(footSlot, damageAmount);
                            damageAccumulator -= damageAmount * currentConfig.BlocksPerDamage;
                        }
                    }
                }
            }
            else
            {
                SetSkatingActive(false, 0, 0, 5f, 2f, 0.995f, 0.5f);
            }
        }

        private void SetSkatingActive(bool active, float bonus, float handling, float bite, float acceleration, float friction, float spring)
        {
            if (physicsController != null)
            {
                physicsController.IsActive = active;
                physicsController.SkateSpeedBonus = bonus;
                physicsController.SkateHandling = handling;
                physicsController.SkateBite = bite;
                physicsController.SkateAcceleration = acceleration;
                physicsController.SkateFriction = friction;
                physicsController.SkateSpring = spring;
            }

            if (active)
            {
                if (!onIceWithSkates || bonus != skateSpeedBonus || handling != skateHandling || bite != skateBite || acceleration != skateAcceleration || friction != skateFriction || spring != skateSpring)
                {
                    skateSpeedBonus = bonus;
                    skateHandling = handling;
                    skateBite = bite;
                    skateAcceleration = acceleration;
                    skateFriction = friction;
                    skateSpring = spring;
                    onIceWithSkates = true;
                }
            }
            else if (onIceWithSkates)
            {
                onIceWithSkates = false;
            }
        }

        private float GetSkateSpeedBonus(ItemStack stack)
        {
            return stack?.Collectible?.Attributes?["skateSpeedBonus"]?.AsFloat(1.2f) ?? 1.2f;
        }

        private float GetSkateHandling(ItemStack stack)
        {
            return stack?.Collectible?.Attributes?["skateHandling"]?.AsFloat(0.5f) ?? 0.5f;
        }

        private float GetSkateBite(ItemStack stack)
        {
            return stack?.Collectible?.Attributes?["skateBite"]?.AsFloat(5f) ?? 5f;
        }

        private float GetSkateAcceleration(ItemStack stack, float defaultVal)
        {
            return stack?.Collectible?.Attributes?["skateAcceleration"]?.AsFloat(defaultVal) ?? defaultVal;
        }

        private float GetSkateFriction(ItemStack stack, float defaultVal)
        {
            return stack?.Collectible?.Attributes?["skateFriction"]?.AsFloat(defaultVal) ?? defaultVal;
        }

        private float GetSkateSpring(ItemStack stack, float defaultVal)
        {
            return stack?.Collectible?.Attributes?["skateSpring"]?.AsFloat(defaultVal) ?? defaultVal;
        }

        private void DamageSkates(ItemSlot slot, int amount)
        {
            slot.Itemstack.Collectible.DamageItem(entity.World, entity, slot, amount);
            slot.MarkDirty();
        }

        public override void OnEntityDespawn(EntityDespawnData reason)
        {
            base.OnEntityDespawn(reason);
            // Safely unregister from the central list to prevent memory leaks when players log off
            modSystem?.ActivePhysicsControllers.Remove(physicsController);
        }
    }
}