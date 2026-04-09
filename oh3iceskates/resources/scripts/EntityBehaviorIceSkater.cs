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

        private Entity entity;

        // Variables used to bypass vanilla engine friction
        private double lastVx = 0;
        private double lastVz = 0;
        private bool wasActive = false;

        // Slalom / Carve Boost Tracking
        private double lastWishAngle = 0;
        private int lastTurnDirection = 0;
        private double boostTimerMs = 0;
        private bool hasLastWishAngle = false;

        public IceSkatesPhysics(Entity entity)
        {
            this.entity = entity;
        }

        // Called every physics tick by the centralized ModSystem
        public void OnPhysicsTick(float dt)
        {
            if (!IsActive)
            {
                wasActive = false;
                return;
            }

            if (!(entity is EntityPlayer entityPlayer)) return;

            // --- MULTIPLAYER FIX ---
            // Player physics is client-authoritative. 
            // The Server shouldn't override motion manually (causes rubber-banding).
            // The Client shouldn't simulate motion for remote players (causes stuttering).
            if (entity.World.Side == EnumAppSide.Server) return;

            if (entity.World.Api is ICoreClientAPI capi && entityPlayer.Player.ClientId != capi.World.Player.ClientId) return;
            // -----------------------

            // Grab the globally synced config. This is a direct memory reference 
            // to the static object, which is O(1) and extremely fast.
            IceSkatesConfig cfg = IceSkatesModSystem.Config;
            double currentFrictionMult = cfg?.SkatesFriction ?? 0.995;
            double currentMaxSpeed = cfg?.MaxSpeed ?? 0.15;
            double currentAcceleration = cfg?.Acceleration ?? 2.5;

            // NEW: Fetch BiteHandling from config (default to 0.8)
            double currentBiteHandling = cfg?.BiteHandling ?? 0.8;

            // Slalom specific config fetching
            double currentSlalomBoost = cfg?.SlalomBoost ?? 0.2;
            double currentSlalomWindow = cfg?.SlalomWindow ?? 300.0;
            double currentSpeedCeiling = cfg?.SpeedCeiling ?? 0.30;
            double currentSlalomSweetspot = cfg?.SlalomSweetspot ?? 0.2;
            bool debugMode = cfg?.DebugMode ?? false;

            // Pre-calculate the exponent needed to stretch the bell curve's 80% threshold 
            // across the configured Sweetspot percentage of the window.
            double safeSweetspot = Math.Max(0.01, Math.Min(0.99, currentSlalomSweetspot));
            double curveExponent = Math.Log(0.8) / Math.Log(Math.Cos(safeSweetspot * Math.PI / 2.0));

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
                        if (progress <= 0.0 || progress >= 1.0)
                        {
                            currentCurve = 0.0;
                        }
                        else
                        {
                            // A sine wave naturally makes a bell shape. 
                            // By raising it to an exponent, we can dynamically widen or narrow the peak!
                            currentCurve = Math.Pow(Math.Sin(progress * Math.PI), curveExponent);
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

                // 2. Apply Custom Configured Friction
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

                if (entityPlayer.Controls.TriesToMove)
                {
                    Vec3f viewVec = entity.Pos.GetViewVector();
                    double viewLen = Math.Sqrt(viewVec.X * viewVec.X + viewVec.Z * viewVec.Z);

                    if (viewLen > 0.001)
                    {
                        double camFwdX = viewVec.X / viewLen;
                        double camFwdZ = viewVec.Z / viewLen;

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

                            // --- SLALOM / CARVE BOOST LOGIC ---
                            double currentWishAngle = Math.Atan2(wishX, wishZ);
                            if (hasLastWishAngle)
                            {
                                double deltaAngle = currentWishAngle - lastWishAngle;

                                // Normalize angle difference to handle wrapping around Pi
                                while (deltaAngle <= -Math.PI) deltaAngle += Math.PI * 2;
                                while (deltaAngle > Math.PI) deltaAngle -= Math.PI * 2;

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

                                    // --- NEW: Skate Bite Logic ---
                                    double appliedHandling = SkateHandling;

                                    // Use the dot product as a highly efficient way to check angle deviation
                                    // Cosine of the bite angle in radians
                                    double biteThresholdDot = Math.Cos(SkateBite * (Math.PI / 180.0));

                                    // If dot product >= threshold, the deviation is smaller than or equal to the SkateBite angle
                                    if (dot >= biteThresholdDot)
                                    {
                                        appliedHandling = Math.Max(appliedHandling, currentBiteHandling);
                                    }

                                    // Apply SkateHandling (0.0 to 1.0)
                                    // 1.0 = Perfect turn conservation
                                    // 0.5 = 50% momentum turns, 50% slips along original path (skidding)
                                    double handling = Math.Max(0.0, Math.Min(1.0, appliedHandling));

                                    vx = (vx * (1.0 - handling)) + (idealVx * handling);
                                    vz = (vz * (1.0 - handling)) + (idealVz * handling);
                                }
                            }

                            // 5. Gradual Acceleration
                            // Coasting top speed is modified by skateSpeedBonus AND the player's current walkspeed stat
                            float playerWalkSpeed = entity.Stats.GetBlended("walkspeed");
                            double topSpeed = currentMaxSpeed * SkateSpeedBonus * playerWalkSpeed;

                            // Allow the player to temporarily accelerate towards a higher ceiling if boosting
                            double boostedTopSpeed = topSpeed;
                            if (boostTimerMs > 0)
                            {
                                // The player's walk speed stat is a multiplier (1.0). We multiply it by 
                                // the vanilla engine's base walk speed (0.09 blocks/tick) to safely add it.
                                double slalomCeiling = currentSpeedCeiling + (0.09 * playerWalkSpeed);

                                // If their coasting top speed is naturally higher, we don't punish them for slaloming
                                if (slalomCeiling > topSpeed)
                                {
                                    // Smoothly scale the ceiling using the bell curve!
                                    boostedTopSpeed = topSpeed + (slalomCeiling - topSpeed) * currentCurve;
                                }
                            }

                            double accelStep = currentAcceleration * dt;
                            accelStep *= boostMultiplier; // Acceleration is smoothly boosted by the bell curve

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
                }
                else
                {
                    // Reset stroke tracking if the player lets go of the movement keys
                    hasLastWishAngle = false;
                }

                // 6. Dynamic Maximum Speed Cap.
                float baseWalkSpeed = entity.Stats.GetBlended("walkspeed");
                double topSpeedLimit = currentMaxSpeed * SkateSpeedBonus * baseWalkSpeed;

                double boostedLimit = topSpeedLimit;
                if (boostTimerMs > 0)
                {
                    double slalomLimit = currentSpeedCeiling + (0.09 * baseWalkSpeed);
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

                if (wasActive)
                {
                    if (entity.CollidedHorizontally)
                    {
                        // Stop our custom momentum if we smack into a wall mid-air
                        lastVx = entity.Pos.Motion.X;
                        lastVz = entity.Pos.Motion.Z;
                    }
                    else
                    {
                        // Apply the exact same config friction while flying through the air
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
        private IceSkatesConfig config;
        private double damageAccumulator = 0;
        private bool onIceWithSkates = false;
        private float skateSpeedBonus = 0;
        private float skateHandling = 0;
        private float skateBite = 5f; // NEW

        private Vec3d lastPos = new Vec3d();
        private EntityPlayer entityPlayer;
        private IInventory characterInv;
        private BlockPos tmpPos = new BlockPos();

        private IceSkatesPhysics physicsController;

        // Tracks the server's last known state to prevent network spam
        private bool lastServerWearingSkates = false;
        private float lastServerSkateSpeedBonus = -1f;
        private float lastServerSkateHandling = -1f;
        private float lastServerSkateBite = -1f; // NEW

        public EntityBehaviorIceSkater(Entity entity) : base(entity)
        {
        }

        public override string PropertyName() => "iceskater";

        public override void Initialize(EntityProperties properties, JsonObject attributes)
        {
            base.Initialize(properties, attributes);
            config = IceSkatesModSystem.Config;
            entityPlayer = entity as EntityPlayer;
            lastPos.Set(entity.Pos.X, entity.Pos.Y, entity.Pos.Z);

            physicsController = new IceSkatesPhysics(entity);

            // Register this physics controller to the central Mod System
            entity.World.Api.ModLoader.GetModSystem<IceSkatesModSystem>()?.ActivePhysicsControllers.Add(physicsController);
        }

        public override void OnGameTick(float deltaTime)
        {
            base.OnGameTick(deltaTime);

            if (entityPlayer?.Player == null) return;

            if (!entity.Alive || entity.State != EnumEntityState.Active)
            {
                SetSkatingActive(false, 0, 0, 5f);
                return;
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
                float serverSkateBite = 5f; // NEW

                if (characterInv != null)
                {
                    footSlot = characterInv[(int)EnumCharacterDressType.Foot];
                    serverWearingSkates = footSlot != null && !footSlot.Empty &&
                                          footSlot.Itemstack.Collectible.Attributes?["isSkates"]?.AsBool(false) == true;

                    if (serverWearingSkates)
                    {
                        serverSkateSpeedBonus = GetSkateSpeedBonus(footSlot.Itemstack);
                        serverSkateHandling = GetSkateHandling(footSlot.Itemstack);
                        serverSkateBite = GetSkateBite(footSlot.Itemstack); // NEW
                    }
                }

                // Sync the properties dynamically to the client ONLY if they changed.
                // Setting WatchedAttributes every tick forces network syncs and triggers 
                // UI redraws on the client, which causes severe inventory and hotbar lag.
                if (serverWearingSkates != lastServerWearingSkates ||
                    serverSkateSpeedBonus != lastServerSkateSpeedBonus ||
                    serverSkateHandling != lastServerSkateHandling ||
                    serverSkateBite != lastServerSkateBite)
                {
                    entity.WatchedAttributes.SetBool("wearingSkates", serverWearingSkates);
                    entity.WatchedAttributes.SetFloat("skateSpeedBonus", serverSkateSpeedBonus);
                    entity.WatchedAttributes.SetFloat("skateHandling", serverSkateHandling);
                    entity.WatchedAttributes.SetFloat("skateBite", serverSkateBite); // NEW

                    lastServerWearingSkates = serverWearingSkates;
                    lastServerSkateSpeedBonus = serverSkateSpeedBonus;
                    lastServerSkateHandling = serverSkateHandling;
                    lastServerSkateBite = serverSkateBite; // NEW
                }
            }

            // Client and server grab synced values
            bool wearingSkates = entity.WatchedAttributes.GetBool("wearingSkates", false);
            float currentItemBonus = entity.WatchedAttributes.GetFloat("skateSpeedBonus", 1.2f);
            float currentItemHandling = entity.WatchedAttributes.GetFloat("skateHandling", 0.5f);
            float currentItemBite = entity.WatchedAttributes.GetFloat("skateBite", 5f); // NEW

            // Check slightly further down (0.2 instead of 0.05) to prevent micro-bounces 
            // from making the game think we left the ice and rapidly toggling the stats.
            tmpPos.Set((int)Math.Floor(entity.Pos.X), (int)Math.Floor(entity.Pos.Y - 0.2), (int)Math.Floor(entity.Pos.Z));
            Block blockBelow = entity.World.BlockAccessor.GetBlock(tmpPos);

            bool isIce = blockBelow?.BlockMaterial == EnumBlockMaterial.Ice;

            // If the player jumped or fell while on ice, keep the physics active while they are mid-air
            bool isJumpingFromIce = onIceWithSkates && !entity.OnGround;

            if (wearingSkates && (isIce || isJumpingFromIce))
            {
                SetSkatingActive(true, currentItemBonus, currentItemHandling, currentItemBite);

                if (entity.World.Side == EnumAppSide.Server && entityPlayer.Controls.TriesToMove && entity.OnGround)
                {
                    if (config?.BlocksPerDamage > 0 && footSlot != null)
                    {
                        damageAccumulator += distTraveled;

                        // Batch durability damage into chunks of 5 to prevent constant 
                        // inventory sync packets, which cause severe hotbar and UI lag.
                        double damageThreshold = config.BlocksPerDamage * 5.0;
                        if (damageAccumulator >= damageThreshold)
                        {
                            int damageAmount = (int)(damageAccumulator / config.BlocksPerDamage);
                            DamageSkates(footSlot, damageAmount);
                            damageAccumulator -= damageAmount * config.BlocksPerDamage;
                        }
                    }
                }
            }
            else
            {
                SetSkatingActive(false, 0, 0, 5f);
            }
        }

        private void SetSkatingActive(bool active, float bonus, float handling, float bite)
        {
            if (physicsController != null)
            {
                physicsController.IsActive = active;
                physicsController.SkateSpeedBonus = bonus;
                physicsController.SkateHandling = handling;
                physicsController.SkateBite = bite; // NEW
            }

            if (active)
            {
                if (!onIceWithSkates || bonus != skateSpeedBonus || handling != skateHandling || bite != skateBite)
                {
                    skateSpeedBonus = bonus;
                    skateHandling = handling;
                    skateBite = bite; // NEW
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

        // NEW: Fetch skate bite attribute from item
        private float GetSkateBite(ItemStack stack)
        {
            return stack?.Collectible?.Attributes?["skateBite"]?.AsFloat(5f) ?? 5f;
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
            entity.World.Api.ModLoader.GetModSystem<IceSkatesModSystem>()?.ActivePhysicsControllers.Remove(physicsController);
        }
    }
}