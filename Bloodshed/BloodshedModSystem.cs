using Bloodshed.Behaviors;
using Bloodshed.Config;
using Bloodshed.Integration;
using Bloodshed.Systems;
using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace Bloodshed
{
    public class BloodshedModSystem : ModSystem
    {
        public string ModId => Mod.Info.ModID;
        public ICoreAPI Api { get; private set; }
        public BloodshedConfig Config { get; private set; }
        public static BloodshedModSystem Instance { get; private set; }
        
        public ILogger Logger => Mod.Logger;
        private FileWatcher _fileWatcher;

        internal ICoreClientAPI capi;
        internal DefenseSystem defenseSystem;
        internal VigorIntegrationSystem vigorIntegration;
        
        // Vigor detection
        public bool IsVigorPresent { get; private set; }

        public override void Start(ICoreAPI api)
        {
            Instance = this;
            Api = api;
            api.RegisterEntityBehaviorClass(ModId + ":stamina", typeof(EntityBehaviorStamina));

            defenseSystem = new DefenseSystem(api);
            
            // Initialize Vigor integration
            vigorIntegration = new VigorIntegrationSystem();
            vigorIntegration.StartPre(api);
            
            Logger.Event("[Bloodshed] Start() called - IsVigorPresent: " + IsVigorPresent);
            
            ReloadConfig(api);
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            Logger.Event("[Bloodshed] StartServerSide() called");
            api.Event.OnEntityLoaded += AddEntityBehaviors;
            api.Event.PlayerJoin += Event_PlayerJoin;
            Logger.Event("Loaded server side");
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            api.Event.LevelFinalize += Event_LevelFinalize;
            capi = api;

            Mod.Logger.Notification(Lang.Get($"{ModId}:clienthello"));
        }
        
        public override void AssetsFinalize(ICoreAPI api)
        {
            Logger.Event($"[Bloodshed] AssetsFinalize() called on {api.Side} side");
            // Detect Vigor mod presence after all mods are loaded
            try
            {
                var vigorMod = api.ModLoader.GetModSystem("Vigor.VigorModSystem");
                IsVigorPresent = vigorMod != null;
                if (IsVigorPresent)
                {
                    if (api.Side == EnumAppSide.Server)
                    {
                        vigorIntegration.StartServerSide(api as ICoreServerAPI);
                    }
                    else
                    {
                        vigorIntegration.StartClientSide(api as ICoreClientAPI);
                    }
                    Logger.Event($"Vigor detected - integration enabled ({api.Side} side)");
                }
                else
                {
                    Logger.Event("Vigor not detected - using Bloodshed stamina system");
                }
            }
            catch (Exception ex)
            {
                IsVigorPresent = false;
                Logger.Error($"Error detecting Vigor: {ex.Message}");
            }
        }

        private void AddEntityBehaviors(Entity entity)
        {
            if (entity is not EntityPlayer) return;
            if (entity.HasBehavior<EntityBehaviorStamina>()) return;
            entity.AddBehavior(new EntityBehaviorStamina(entity));
        }

        private void Event_LevelFinalize()
        {
            // Hook server events for damage and fatigue
            var ebh = capi.World.Player.Entity.GetBehavior<EntityBehaviorHealth>();
            if (ebh != null) ebh.onDamaged += (dmg, dmgSource) => defenseSystem.HandleDamaged(capi.World.Player, dmg, dmgSource);

            var ebs = capi.World.Player.Entity.GetBehavior<EntityBehaviorStamina>();
            if (ebs != null) ebs.OnFatigued += (ftg, ftgSource) => defenseSystem.HandleFatigued(capi.World.Player, ftg, ftgSource);
            capi.Logger.VerboseDebug("Done item defense stats");
        }

        private void Event_PlayerJoin(IServerPlayer byPlayer)
        {
            // Hook client events for damage and fatigue
            var ebh = byPlayer.Entity.GetBehavior<EntityBehaviorHealth>();
            if (ebh != null) ebh.onDamaged += (dmg, dmgSource) => defenseSystem.HandleDamaged(byPlayer, dmg, dmgSource);

            var ebs = byPlayer.Entity.GetBehavior<EntityBehaviorStamina>();
            if (ebs != null) ebs.OnFatigued += (ftg, ftgSource) => defenseSystem.HandleFatigued(byPlayer, ftg, ftgSource);
        }

        public void ReloadConfig(ICoreAPI api)
        {
            (_fileWatcher ??= new FileWatcher()).Queued = true;

            try
            {
                var _config = api.LoadModConfig<BloodshedConfig>($"{ModId}.json");
                if (_config == null)
                {
                    Mod.Logger.Warning("Missing config! Using default.");
                    Config = new BloodshedConfig();
                    //Config = api.Assets.Get(new AssetLocation("bloodshed:config/default.json")).ToObject<BloodshedConfig>();
                    api.StoreModConfig(Config, $"{ModId}.json");
                }
                else
                {
                    Config = _config;
                }
            }
            catch (Exception ex)
            {
                Mod.Logger.Error($"Could not load {ModId} config!");
                Mod.Logger.Error(ex);
            }

            api.Event.RegisterCallback(_ => _fileWatcher.Queued = false, 100);
        }

        public override void Dispose()
        {
            _fileWatcher?.Dispose();
            _fileWatcher = null;
        }
    }
}
