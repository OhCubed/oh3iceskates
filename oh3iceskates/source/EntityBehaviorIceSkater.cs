using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Datastructures;

namespace oh3iceskates
{
    // Physics controller responsible for handling custom ice skating movement and momentum.
    public class IceSkatesPhysics
    {
        public bool IsActive { get; set; }
        public float SkateSpeedBonus { get; set; } = 1.0f;
        public float SkateHandling { get; set; } = 0.5f;

        // Skate bite deviation threshold (in degrees)
        public float SkateBite { get; set; } = 5f;

        // Base acceleration when skating
        public float SkateAcceleration { get; set; } = 2f;

        // Base friction applied when skating
        public float SkateFriction { get; set; } = 0.995f;

        // Multiplier for momentum return after crouching
        public float SkateSpring { get; set; } = 0.5f;

        // Cached from behavior to prevent string lookups in the 0ms tick
        public float PlayerWalkSpeed { get; set; } = 1.0f;
        public IceSkatesConfig Config { get; set; }

        private Entity entity;
        private EntityPlayer entityPlayer;

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

        private double lastSweetspot = -1.0;
        private double cachedCurveExponent = 1.0;

        public IceSkatesPhysics(Entity entity)
        {
            this.entity = entity;
            this.entityPlayer = entity as EntityPlayer;
        }

        // Called every physics tick by the local behavior's tick listener
        public void OnPhysicsTick(float dt)
        {
            if (!IsActive || entityPlayer == null)
            {
                wasActive = false;
                return;
            }

            // Grab the globally synced config passed down from the behavior. Fallback just in case.
            IceSkatesConfig cfg = Config ?? new IceSkatesConfig();

            bool isSneaking = entityPlayer.Controls.Sneak;

            double currentMaxSpeed = cfg.MaxSpeed;
            double topSpeedLimit = currentMaxSpeed * SkateSpeedBonus * PlayerWalkSpeed;
            double currentAcceleration = SkateAcceleration;

            // Fetch BiteHandling from config (default to 0.8)
            double currentBiteHandling = cfg.BiteHandling;

            // Slalom-specific config variables
            double currentSlalomBoost = cfg.SlalomBoost;
            double currentSlalomWindow = cfg.SlalomWindow;
            double currentSpeedCeiling = cfg.SpeedCeiling;
            double currentSlalomSweetspot = cfg.SlalomSweetspot;

            // Fetch Brake Friction from config
            double currentBrakeFriction = cfg.BrakeFriction;

            bool debugMode = cfg.DebugMode;

            // Pre-calculate the exponent needed to stretch the bell curve's 80% threshold 
            // across the configured Sweetspot percentage of the window. We cache this to avoid 
            // expensive Math.Log and Math.Cos calls every single tick.
            if (currentSlalomSweetspot != lastSweetspot)
            {
                double safeSweetspot = Math.Max(0.01, Math.Min(0.99, currentSlalomSweetspot));
                cachedCurveExponent = Math.Log(0.8) / Math.Log(Math.Cos(safeSweetspot * GameMath.PI / 2.0));
                lastSweetspot = currentSlalomSweetspot;
            }

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
                            // By raising it to an exponent, we can dynamically widen or narrow the peak.
                            currentCurve = Math.Pow(GameMath.Sin((float)(progress * GameMath.PI)), cachedCurveExponent);
                        }
                    }
                }

                double boostMultiplier = 1.0 + (currentSlalomBoost * currentCurve);

                double vx = entity.Pos.Motion.X;
                double vz = entity.Pos.Motion.Z;

                // Bypass vanilla friction by restoring the velocity from the previous tick, 
                // unless a horizontal collision occurred.
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

                // Apply custom configured friction
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

                    // Establish a dynamic floor relative to the globally configured brake friction.
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

                // Calculate the wish direction (the direction the player wants to move)
                double wishX = 0;
                double wishZ = 0;
                double rawInputX = 0; // Pure camera + keys direction
                double rawInputZ = 0;

                if (entityPlayer.Controls.TriesToMove)
                {
                    // Calculate the forward and right vectors manually using Yaw to avoid memory allocations
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

                        // Slalom / Carve Boost Logic
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

                            // The player must turn in the OPPOSITE direction of their last carve to get the bonus again
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

                        // Carving and Momentum Recycling (Strong Lerp)
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

                                // Skate Bite Logic
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

                        // Apply gradual acceleration towards the top speed limit
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
                                // Smoothly scale the ceiling using the bell curve
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

                            // Compensate for friction loss during active acceleration to ensure the player 
                            // can reach the top speed limit without hitting an equilibrium plateau.
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

                // Skate Spring (Momentum Dump)
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

                // Enforce a dynamic maximum speed cap based on the slalom boost and spring momentum.
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
                // them and lets friction organically decelerate them back down to the normal limit.
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
                    entity.World.Api.Logger.Debug($"[IceSkates] CurSpeed: {currentSpeedCalc:F4} | TopSpeed: {topSpeedLimit:F4} | DynCap: {dynamicCapCalc:F4} | SlalomLimit: {boostedLimit:F4} | Curve: {currentCurve:F2}");
                }

                // Apply velocity to the entity and store the state for the next tick
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

    // Entity behavior that manages the activation and properties of the ice skating physics.
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
        private long? clientPhysicsTickId;
        private bool localPlayerChecked = false;

        // Caches item attributes to prevent expensive JSON parsing every tick
        private int lastFootItemId = -1;
        private bool cachedIsSkates = false;
        private float cachedSpeedBonus = 1.2f;
        private float cachedHandling = 0.5f;
        private float cachedBite = 5f;
        private float cachedAcceleration = 2f;
        private float cachedFriction = 0.995f;
        private float cachedSpring = 0.5f;

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

            // We no longer blindly register physics for every player here! 
            // We wait until OnGameTick can verify if this entity belongs to the local client.
        }

        public override void OnGameTick(float deltaTime)
        {
            base.OnGameTick(deltaTime);

            if (entityPlayer?.Player == null) return;

            // VINTAGE STORY CONVENTION: Wait until the client world is fully loaded to verify 
            // if this entity is the LOCAL player. Only the local player gets a 0ms physics controller.
            if (entity.World.Side == EnumAppSide.Client && !localPlayerChecked)
            {
                var capi = entity.World.Api as ICoreClientAPI;
                if (capi?.World?.Player != null)
                {
                    localPlayerChecked = true;
                    if (entityPlayer.PlayerUID == capi.World.Player.PlayerUID)
                    {
                        physicsController = new IceSkatesPhysics(entity);
                        clientPhysicsTickId = entity.World.RegisterGameTickListener(physicsController.OnPhysicsTick, 0);
                    }
                }
            }

            // Pull the correct configuration from the ModSystem Dictionary early
            IceSkatesConfig currentConfig = modSystem?.GetConfigForPlayer(entityPlayer.PlayerUID);

            if (!entity.Alive || entity.State != EnumEntityState.Active)
            {
                SetSkatingActive(false, 0, 0, 5f, 2f, 0.995f, 0.5f);
                return;
            }

            // Pass the slower-updating WalkSpeed stat and config down to the 0ms Physics class
            // This safely bypasses remote players because their physicsController remains null!
            if (physicsController != null)
            {
                physicsController.PlayerWalkSpeed = entity.Stats.GetBlended("walkspeed");
                physicsController.Config = currentConfig;
            }

            ItemSlot footSlot = null;
            double distTraveled = 0;

            if (entity.World.Side == EnumAppSide.Server)
            {
                // MOVED: Distance calculation is only used by the server for durability damage.
                // Calculating square roots and deltas on the client every tick was wasted CPU.
                double dx = entity.Pos.X - lastPos.X;
                double dz = entity.Pos.Z - lastPos.Z;
                distTraveled = Math.Sqrt(dx * dx + dz * dz);

                // Cap to prevent massive jumps (like teleports) from dealing instant durability damage
                if (distTraveled > 10.0) distTraveled = 0;
                lastPos.Set(entity.Pos.X, entity.Pos.Y, entity.Pos.Z);

                if (characterInv == null)
                {
                    characterInv = entityPlayer.Player.InventoryManager.GetOwnInventory("character");
                }

                if (characterInv != null)
                {
                    footSlot = characterInv[(int)EnumCharacterDressType.Foot];
                    int currentItemId = footSlot?.Itemstack?.Collectible?.Id ?? -1;

                    // Only parse the item's JSON attributes if the equipped footwear actually changes
                    if (currentItemId != lastFootItemId)
                    {
                        lastFootItemId = currentItemId;
                        if (footSlot != null && !footSlot.Empty)
                        {
                            var attributes = footSlot.Itemstack.Collectible.Attributes;
                            cachedIsSkates = attributes?["isSkates"]?.AsBool(false) ?? false;

                            if (cachedIsSkates)
                            {
                                cachedSpeedBonus = attributes?["skateSpeedBonus"]?.AsFloat(1.2f) ?? 1.2f;
                                cachedHandling = attributes?["skateHandling"]?.AsFloat(0.5f) ?? 0.5f;
                                cachedBite = attributes?["skateBite"]?.AsFloat(5f) ?? 5f;
                                cachedAcceleration = attributes?["skateAcceleration"]?.AsFloat(2f) ?? 2f;
                                cachedFriction = attributes?["skateFriction"]?.AsFloat(0.995f) ?? 0.995f;
                                cachedSpring = attributes?["skateSpring"]?.AsFloat(0.5f) ?? 0.5f;
                            }
                        }
                        else
                        {
                            cachedIsSkates = false;
                        }
                    }
                }

                // VINTAGE STORY CONVENTION: Combine related state into an ITreeAttribute!
                // Setting 7 independent attributes on the root WatchedAttributes generates 7 separate 
                // modification paths and massively clutters network traffic. Grouping them inside an 
                // ITreeAttribute and calling MarkPathDirty("iceskates") syncs everything in a single, lean packet.
                if (cachedIsSkates != lastServerWearingSkates ||
                    cachedSpeedBonus != lastServerSkateSpeedBonus ||
                    cachedHandling != lastServerSkateHandling ||
                    cachedBite != lastServerSkateBite ||
                    cachedAcceleration != lastServerSkateAcceleration ||
                    cachedFriction != lastServerSkateFriction ||
                    cachedSpring != lastServerSkateSpring)
                {
                    ITreeAttribute skateTree = entity.WatchedAttributes.GetOrAddTreeAttribute("iceskates");

                    skateTree.SetBool("wearingSkates", cachedIsSkates);
                    skateTree.SetFloat("skateSpeedBonus", cachedSpeedBonus);
                    skateTree.SetFloat("skateHandling", cachedHandling);
                    skateTree.SetFloat("skateBite", cachedBite);
                    skateTree.SetFloat("skateAcceleration", cachedAcceleration);
                    skateTree.SetFloat("skateFriction", cachedFriction);
                    skateTree.SetFloat("skateSpring", cachedSpring);

                    entity.WatchedAttributes.MarkPathDirty("iceskates");

                    lastServerWearingSkates = cachedIsSkates;
                    lastServerSkateSpeedBonus = cachedSpeedBonus;
                    lastServerSkateHandling = cachedHandling;
                    lastServerSkateBite = cachedBite;
                    lastServerSkateAcceleration = cachedAcceleration;
                    lastServerSkateFriction = cachedFriction;
                    lastServerSkateSpring = cachedSpring;
                }
            }

            // Client and server grab synced values from the unified tree
            ITreeAttribute currentSkateTree = entity.WatchedAttributes.GetTreeAttribute("iceskates");
            bool wearingSkates = currentSkateTree?.GetBool("wearingSkates", false) ?? false;
            float currentItemBonus = currentSkateTree?.GetFloat("skateSpeedBonus", 1.2f) ?? 1.2f;
            float currentItemHandling = currentSkateTree?.GetFloat("skateHandling", 0.5f) ?? 0.5f;
            float currentItemBite = currentSkateTree?.GetFloat("skateBite", 5f) ?? 5f;
            float currentItemAcceleration = currentSkateTree?.GetFloat("skateAcceleration", 2f) ?? 2f;
            float currentItemFriction = currentSkateTree?.GetFloat("skateFriction", 0.995f) ?? 0.995f;
            float currentItemSpring = currentSkateTree?.GetFloat("skateSpring", 0.5f) ?? 0.5f;

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

                        // Batch durability damage into chunks to prevent excessive inventory sync packets
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

        private void DamageSkates(ItemSlot slot, int amount)
        {
            slot.Itemstack.Collectible.DamageItem(entity.World, entity, slot, amount);
            slot.MarkDirty();
        }

        public override void OnEntityDespawn(EntityDespawnData reason)
        {
            base.OnEntityDespawn(reason);
            // VINTAGE STORY CONVENTION: Safely clean up our local physics tick listener!
            if (clientPhysicsTickId.HasValue)
            {
                entity.World.UnregisterGameTickListener(clientPhysicsTickId.Value);
            }
        }
    }
}