using Bloodshed.Integration;
using Bloodshed.Systems;
using System;
using System.Linq;
using System.Numerics;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace Bloodshed.Behaviors
{
    public delegate float OnFatiguedDelegate(float fatigue, FatigueSource ftgSource);
    public class EntityBehaviorStamina : EntityBehavior
    {
        public static BloodshedModSystem Bloodshed => BloodshedModSystem.Instance;

        public event OnFatiguedDelegate OnFatigued = (ftg, ftgSource) => ftg;

        private float timeSinceLastUpdate;
        private static bool DebugMode => Bloodshed.Config.DebugMode; // Debug mode for logging
        private static float DefenseFatigue => Bloodshed.Config.DefenseStaminaCost; // Fatigue from blocking
        private static float SwimFatigue => Bloodshed.Config.SwimStaminaCost; // Fatigue from swimming
        private static float SprintFatigue => Bloodshed.Config.SprintStaminaCost; // Fatigue from running 
        private static float WalkSpeedReductionHalfStamina => Bloodshed.Config.WalkSpeedReductionHalfStamina; // Walk speed reduction when below 50% stamina
        private static float WalkSpeedReductionExhausted => Bloodshed.Config.WalkSpeedReductionExhausted; // Walk speed reduction when exhausted

        private ITreeAttribute StaminaTree
        {
            get
            {
                var tree = entity.WatchedAttributes.GetTreeAttribute(AttributeKey);
                if (tree == null)
                {
                    tree = new TreeAttribute();
                    entity.WatchedAttributes.SetAttribute(AttributeKey, tree);
                    entity.WatchedAttributes.MarkPathDirty(AttributeKey);
                }
                return tree;
            }
        }

        private static string AttributeKey => $"{BloodshedModSystem.Instance.ModId}:stamina";

        public bool Exhausted
        {
            get => StaminaTree?.GetBool("exhausted") ?? false;
            set
            {
                StaminaTree.SetBool("exhausted", value);
                entity.WatchedAttributes.MarkPathDirty(AttributeKey);
            }
        }

        public float Stamina
        {
            get => StaminaTree?.GetFloat("currentstamina") ?? 100;
            set 
            { 
                StaminaTree.SetFloat("currentstamina", value); 
                entity.WatchedAttributes.MarkPathDirty(AttributeKey); 
            }
        }

        public float MaxStamina
        {
            get => StaminaTree?.GetFloat("maxstamina") ?? 100;
            set
            {
                StaminaTree.SetFloat("maxstamina", value);
                entity.WatchedAttributes.MarkPathDirty(AttributeKey);
            }
        }

        public EntityBehaviorStamina(Entity entity) : base(entity) { }

        public override void Initialize(EntityProperties properties, JsonObject typeAttributes)
        {
            Bloodshed.Logger.Notification("initializing stamina behavior for {0}", entity.EntityId);
            
            var staminaTree = entity.WatchedAttributes.GetTreeAttribute(AttributeKey);

            if (staminaTree == null)
            {
                entity.WatchedAttributes.SetAttribute(AttributeKey, new TreeAttribute());

                Exhausted = typeAttributes["exhausted"].AsBool(false);
                MaxStamina = typeAttributes["maxstamina"].AsFloat(100);
                Stamina = typeAttributes["currentstamina"].AsFloat(MaxStamina);
                MarkDirty();
            }
            else
            {
                float maxStamina = staminaTree.GetFloat("maxstamina");
                if (maxStamina == 0)
                {
                    MaxStamina = typeAttributes["maxstamina"].AsFloat(100);
                    MarkDirty();
                }
            }

            timeSinceLastUpdate = (float)entity.World.Rand.NextDouble();   // Randomise which game tick these update, a starting server would otherwise start all loaded entities with the same zero timer
        }

        private bool ApplyFatigue(float fatigueAmount, EnumFatigueSource source)
        {
            if (fatigueAmount <= 0) return false;

            FatigueSource fs = new()
            {
                Source = source,
                SourceEntity = entity,
                CauseEntity = entity,
                SourceBlock = null,
                SourcePos = entity.Pos.XYZ
            };

            FatigueEntity(fatigueAmount, fs);

            return true;
        }

        private float CalculateElapsedMultiplier(float elapsedTime)
        {
            return elapsedTime * entity.Api.World.Calendar.SpeedOfTime * entity.Api.World.Calendar.CalendarSpeedMul;
        }

        public override void OnGameTick(float deltaTime)
        {
            if (entity.World.Side == EnumAppSide.Client) return;
            if (entity is not EntityPlayer plr) return; // Only players have the stamina behavior             
            if (plr.Player.WorldData.CurrentGameMode == EnumGameMode.Creative) return; // Only players in survival mode have stamina
            
            // Get stamina values from appropriate system (Vigor or Bloodshed)
            float stamina, maxStamina;
            if (Bloodshed.IsVigorPresent)
            {
                // Use Vigor's stamina values for penalties
                stamina = VigorIntegration.GetCurrentStamina(plr);
                maxStamina = VigorIntegration.GetMaxStamina(plr);
            }
            else
            {
                // Use Bloodshed's stamina values
                stamina = Stamina;
                maxStamina = MaxStamina;
            }

            timeSinceLastUpdate += deltaTime;

            // Check stamina 4 times a second
            if (timeSinceLastUpdate >= 0.25f)
            {
                if (entity.Alive)
                {
                    ItemSlot activeSlot = plr.ActiveHandItemSlot;
                    var defAttr = activeSlot?.Itemstack?.ItemAttributes?["bloodshed-defense"];

                    bool activelyFatiguing = false;

                    // --- Low stamina effects ---
                    if (stamina < maxStamina * 0.5f)
                    {
                        plr.Stats.Set("walkspeed", $"{Bloodshed.ModId}:walkspeed", -WalkSpeedReductionHalfStamina, true); // Player moves slower when low on stamina
                    }
                    else if (stamina < maxStamina * 0.1f && !Bloodshed.IsVigorPresent) // Don't apply exhaustion penalty if Vigor is present - Vigor handles this
                    {
                        plr.Stats.Set("walkspeed", $"{Bloodshed.ModId}:walkspeed", -WalkSpeedReductionExhausted, true); // Player moves even slower when exhausted
                    }
                    else
                    {
                        plr.Stats.Remove("walkspeed", $"{Bloodshed.ModId}:walkspeed"); // Player moves normally
                    }

                    // --- Fatiguing actions ---
                    // Only apply swimming and sprinting fatigue if Vigor is not present (Vigor handles these)
                    if (!Bloodshed.IsVigorPresent)
                    {
                        // Player swimming
                        if (plr.Swimming)
                        {
                            activelyFatiguing = ApplyFatigue(SwimFatigue * CalculateElapsedMultiplier(timeSinceLastUpdate), EnumFatigueSource.Swim);
                        }

                        // Player sprinting
                        if (plr.Controls.Sprint && (plr.Controls.Forward || plr.Controls.Left || plr.Controls.Right || plr.Controls.Backward))
                        {
                            activelyFatiguing = ApplyFatigue(SprintFatigue * CalculateElapsedMultiplier(timeSinceLastUpdate), EnumFatigueSource.Run);
                        }
                    }

                    // Player defensive posture
                    if (plr.Controls.RightMouseDown && (defAttr is not null && defAttr.Exists))
                    {
                        activelyFatiguing = ApplyFatigue(DefenseFatigue * CalculateElapsedMultiplier(timeSinceLastUpdate), EnumFatigueSource.Defense);
                    }

                    // --- Stamina regeneration ---
                    if (!activelyFatiguing)
                    {
                        RegenerateStamina(timeSinceLastUpdate);
                    }
                }

                // Set exhausted state using appropriate stamina system
                if (Bloodshed.IsVigorPresent)
                {
                    Exhausted = VigorIntegration.IsExhausted(plr);
                }
                else
                {
                    Exhausted = stamina <= 0; // Player is exhausted when stamina reaches 0
                }

                timeSinceLastUpdate = 0;
            }
        }

        public void RegenerateStamina(float elapsedTime)
        {
            var stamina = Stamina;  // higher performance to read this TreeAttribute only once
            var maxStamina = MaxStamina;

            if (stamina < maxStamina)
            {
                var staminaRegenSpeed = Bloodshed.Config?.StaminaRegenSpeed ?? 1f;
                var saturationCostPerStamina = Bloodshed.Config?.SaturationCostPerStamina ?? 0.5f;
                var staminaRegenPerGameSecond = 0.15f * staminaRegenSpeed;
                var multiplierPerGameSec = elapsedTime * entity.Api.World.Calendar.SpeedOfTime * entity.Api.World.Calendar.CalendarSpeedMul;

                var ebh = entity.GetBehavior<EntityBehaviorHunger>();

                if (ebh != null)
                {
                    // When below 25% satiety, stamina regen starts dropping
                    staminaRegenPerGameSecond = GameMath.Clamp(staminaRegenPerGameSecond * ebh.Saturation / ebh.MaxSaturation / 0.25f, 0, staminaRegenPerGameSecond);
                    if (DebugMode)
                    {
                        Bloodshed.Logger.Notification($"Consuming {(multiplierPerGameSec * staminaRegenPerGameSecond * saturationCostPerStamina)} saturation to regen stamina");
                    }
                    ebh.ConsumeSaturation(multiplierPerGameSec * staminaRegenPerGameSecond * saturationCostPerStamina);
                }

                Stamina = Math.Min(stamina + (multiplierPerGameSec * staminaRegenPerGameSecond), maxStamina);
            }
        }

        public void OnEntityFatigued(FatigueSource fatigueSource, ref float fatigue)
        {
            // Only fatigue server side and sync to client
            if (entity.World.Side == EnumAppSide.Client) return;

            if (entity is not EntityPlayer plr) return; // Only players have the stamina behavior             
            if (plr.Player.WorldData.CurrentGameMode == EnumGameMode.Creative) return; // Only players in survival mode have stamina

            if (OnFatigued != null)
            {
                foreach (OnFatiguedDelegate dele in OnFatigued.GetInvocationList().Cast<OnFatiguedDelegate>())
                {
                    fatigue = dele.Invoke(fatigue, fatigueSource);
                }
            }

            FatigueEntity(fatigue, fatigueSource);
        }

        public void FatigueEntity(float fatigue, FatigueSource ftgSource)
        {
            if (entity.World.Side == EnumAppSide.Client) return;
            if (entity is not EntityPlayer plr) return; // Only players have the stamina behavior
            if (!entity.Alive) return;
            if (fatigue <= 0) return;

            // Use Vigor integration if available, otherwise use Bloodshed's stamina system
            if (Bloodshed.IsVigorPresent)
            {
                // Scale fatigue for Vigor's stamina system (Vigor uses ~1500 stamina vs Bloodshed's 100)
                float scaledFatigue = fatigue * 1.5f;
                VigorIntegration.ConsumeStamina(plr, scaledFatigue);
                if (DebugMode)
                {
                    Bloodshed.Logger.Notification($"[Vigor Integration] {ftgSource.Source} consumed {scaledFatigue} stamina via Vigor (scaled from {fatigue})");
                }
            }
            else
            {
                // Original Bloodshed stamina logic
                var stamina = Stamina;  // higher performance to read this TreeAttribute only once
                var maxStamina = MaxStamina;
                Stamina = GameMath.Clamp(stamina - fatigue, 0, maxStamina);
                if (DebugMode)
                {
                    Bloodshed.Logger.Notification($"{ftgSource.Source} reduced stamina by: {fatigue}");
                    Bloodshed.Logger.Notification($"Stamina: {stamina}/{maxStamina}");
                }
            }

            if (fatigue > 5f)
            {
                // ToDo: Find suitable animation for stagger
                entity.AnimManager.StartAnimation("hurt");

                // ToDo: Find suitable sound for fatigue
                entity.PlayEntitySound("hurt");
            }
        }

        public override void GetInfoText(StringBuilder infotext)
        {
            var capi = entity.Api as ICoreClientAPI;
            if (capi?.World.Player?.WorldData?.CurrentGameMode == EnumGameMode.Creative)
            {
                infotext.AppendLine(Lang.Get($"[Bloodshed] Stamina: {Stamina}/{MaxStamina}"));
            }
        }

        public override string PropertyName()
        {
            return AttributeKey;
        }

        public void MarkDirty()
        {
            entity.WatchedAttributes.MarkPathDirty(AttributeKey);
        }
    }
}