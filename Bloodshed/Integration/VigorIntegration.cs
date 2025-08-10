using System;
using System.Reflection;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.API.Common.Entities;

namespace Bloodshed.Integration
{
    /// <summary>
    /// Network packet for server-side stamina consumption
    /// </summary>
    [ProtoContract]
    public class StaminaConsumptionPacket
    {
        [ProtoMember(1)]
        public float Amount { get; set; }
    }
    
    /// <summary>
    /// Network packet for server-side continuous drain start/stop
    /// </summary>
    [ProtoContract]
    public class StaminaDrainPacket
    {
        [ProtoMember(1)]
        public string ActionId { get; set; }
        
        [ProtoMember(2)]
        public float AmountPerSecond { get; set; }
        
        [ProtoMember(3)]
        public bool IsStarting { get; set; } // true = start, false = stop
    }
    
    /// <summary>
    /// Integration with Vigor API for stamina consumption
    /// </summary>
    public class VigorIntegrationSystem : ModSystem
    {
        private const string NETWORK_CHANNEL = "bloodshed:vigor";
        
        #region Shared
        // Cache API references by context (client/server) to reduce lookup overhead and logging
        private static object _clientApiCache = null;
        private static object _serverApiCache = null;
        private static bool _apiLookupAttempted = false;
        
        /// <summary>
        /// Register the network channel in the shared Start method
        /// </summary>
        public override void Start(ICoreAPI api)
        {
            base.Start(api);
            
            // Only register the channel and message types here (shared between client/server)
            api.Network.RegisterChannel(NETWORK_CHANNEL)
                .RegisterMessageType<StaminaConsumptionPacket>()
                .RegisterMessageType<StaminaDrainPacket>();
            
            api.Logger.Event("[Bloodshed:VigorIntegration] Base network channel registered with all packet types");
        }
        
        /// <summary>
        /// Check if the Vigor mod is enabled
        /// </summary>
        public static bool IsVigorEnabled(ICoreAPI api)
        {
            return api.ModLoader.IsModEnabled("vigor");
        }
        
        /// <summary>
        /// Gets the appropriate Vigor API instance based on the current context
        /// Uses cached references after first successful lookup
        /// </summary>
        public static dynamic GetVigorAPI(ICoreAPI api)
        {
            // Early return if Vigor isn't enabled
            if (!IsVigorEnabled(api))
                return null;
            
            // Return cached API reference if available
            if (api.Side == EnumAppSide.Client && _clientApiCache != null)
            {
                return _clientApiCache;
            }
            else if (api.Side == EnumAppSide.Server && _serverApiCache != null)
            {
                return _serverApiCache;
            }
            
            // Only log once per session that we're looking up the API
            bool firstLookup = !_apiLookupAttempted;
            _apiLookupAttempted = true;
            
            if (firstLookup)
            {
                api.Logger.Event("[Bloodshed:VigorIntegration] First-time Vigor API lookup");
            }
            
            try
            {
                // Get the VigorModSystem using string-based approach
                var vigorModSystem = api.ModLoader.GetModSystem("Vigor.VigorModSystem");
                if (vigorModSystem == null)
                {
                    if (firstLookup) api.Logger.Warning("[Bloodshed:VigorIntegration] VigorModSystem not found via GetModSystem(\"Vigor.VigorModSystem\")");
                    return null;
                }
                
                // Log the type only on first lookup
                if (firstLookup) api.Logger.Debug("[Bloodshed:VigorIntegration] Found VigorModSystem, type: {0}", vigorModSystem.GetType().FullName);
                
                // Determine which API instance to use based on the context
                string apiPropertyName;
                
                if (api.Side == EnumAppSide.Server)
                {
                    apiPropertyName = "ServerAPI";
                    if (firstLookup) api.Logger.Debug("[Bloodshed:VigorIntegration] Server context detected, using ServerAPI");
                }
                else if (api.Side == EnumAppSide.Client)
                {
                    apiPropertyName = "ClientAPI";
                    if (firstLookup) api.Logger.Debug("[Bloodshed:VigorIntegration] Client context detected, using ClientAPI");
                }
                else
                {
                    // Fallback to general API if side is unknown (should never happen)
                    apiPropertyName = "API";
                    if (firstLookup) api.Logger.Warning("[Bloodshed:VigorIntegration] Unknown API side, using general API (not recommended)");
                }
                
                // Get the appropriate API property via reflection
                var apiProperty = vigorModSystem.GetType().GetProperty(apiPropertyName);
                if (apiProperty == null)
                {
                    if (firstLookup) api.Logger.Warning("[Bloodshed:VigorIntegration] {0} property not found on VigorModSystem", apiPropertyName);
                    return null;
                }
                
                // Get the API instance
                var vigorApi = apiProperty.GetValue(vigorModSystem);
                if (vigorApi == null)
                {
                    if (firstLookup) api.Logger.Warning("[Bloodshed:VigorIntegration] {0} property exists but value is null", apiPropertyName);
                    return null;
                }
                
                // Cache the API reference by context
                if (api.Side == EnumAppSide.Client)
                {
                    _clientApiCache = vigorApi;
                    if (firstLookup) api.Logger.Event("[Bloodshed:VigorIntegration] Successfully cached client-side API reference");
                }
                else if (api.Side == EnumAppSide.Server)
                {
                    _serverApiCache = vigorApi;
                    if (firstLookup) api.Logger.Event("[Bloodshed:VigorIntegration] Successfully cached server-side API reference");
                }
                
                return vigorApi;
            }
            catch (Exception ex)
            {
                api.Logger.Error("[Bloodshed:VigorIntegration] Error getting Vigor API: {0}", ex.ToString());
                return null;
            }
        }
        #endregion
        
        #region Server
        private ICoreServerAPI sapi;
        private IServerNetworkChannel serverChannel;
        
        public override void StartServerSide(ICoreServerAPI api)
        {
            base.StartServerSide(api);
            
            // Don't cache the API reference to ensure we always get the correct instance
            this.sapi = api;
            
            // Get the channel and register the server-side message handlers
            serverChannel = api.Network.GetChannel(NETWORK_CHANNEL);
            serverChannel.SetMessageHandler<StaminaConsumptionPacket>(OnServerStaminaRequest);
            serverChannel.SetMessageHandler<StaminaDrainPacket>(OnServerStaminaDrainRequest);
            
            api.Logger.Event("[Bloodshed:VigorIntegration] Server network handlers registered for stamina consumption and drain");
        }
        
        /// <summary>
        /// Server-side handler for stamina consumption requests
        /// </summary>
        private void OnServerStaminaRequest(IServerPlayer fromPlayer, StaminaConsumptionPacket packet)
        {
            sapi.Logger.Event("[Bloodshed:VigorIntegration] SERVER HANDLER: Received stamina consumption request from {0}: {1} stamina", 
                fromPlayer.PlayerName, packet.Amount);
            
            // Execute the actual stamina consumption on the server side
            bool success = ConsumeStaminaOnServer(fromPlayer.Entity as EntityPlayer, packet.Amount);
            sapi.Logger.Event("[Bloodshed:VigorIntegration] SERVER HANDLER: Stamina consumption result: {0}", success ? "SUCCESS" : "FAILED");
        }
        
        /// <summary>
        /// Server-side handler for continuous stamina drain requests
        /// </summary>
        private void OnServerStaminaDrainRequest(IServerPlayer fromPlayer, StaminaDrainPacket packet)
        {
            var player = fromPlayer.Entity as EntityPlayer;
            if (player == null) return;
            
            sapi.Logger.Event("[Bloodshed:VigorIntegration] SERVER HANDLER: Received {0} drain request from {1} for action '{2}' at rate {3}/sec", 
                packet.IsStarting ? "START" : "STOP", fromPlayer.PlayerName, packet.ActionId, packet.AmountPerSecond);
            
            // Get server-side API (direct access)
            var api = VigorIntegrationSystem.GetVigorAPI(sapi);
            if (api == null)
            {
                sapi.Logger.Warning("[Bloodshed:VigorIntegration] SERVER DRAIN: Could not get Vigor API, ignoring drain request");
                return;
            }
            
            try
            {
                if (packet.IsStarting)
                {
                    // Start drain on server directly
                    bool success = api.StartStaminaDrain(player, packet.ActionId, packet.AmountPerSecond);
                    sapi.Logger.Event("[Bloodshed:VigorIntegration] SERVER DRAIN: Started drain '{0}' result: {1}", 
                        packet.ActionId, success ? "SUCCESS" : "FAILED");
                }
                else
                {
                    // Stop drain on server directly
                    api.StopStaminaDrain(player, packet.ActionId);
                    sapi.Logger.Event("[Bloodshed:VigorIntegration] SERVER DRAIN: Stopped drain '{0}'", packet.ActionId);
                }
            }
            catch (Exception ex)
            {
                sapi.Logger.Error("[Bloodshed:VigorIntegration] SERVER DRAIN: Error handling drain request: {0}", ex.ToString());
            }
        }
        
        /// <summary>
        /// Consume stamina on the server using the Vigor API
        /// </summary>
        public bool ConsumeStaminaOnServer(EntityPlayer player, float amount)
        {
            if (player == null)
            {
                sapi.Logger.Warning("[Bloodshed:VigorIntegration] SERVER CONSUME: Called with null player");
                return true; // Allow action if invalid context
            }
            
            sapi.Logger.Event("[Bloodshed:VigorIntegration] SERVER CONSUME: Processing stamina consumption for player entity {0}, amount {1}", player.EntityId, amount);
            
            var api = VigorIntegrationSystem.GetVigorAPI(sapi);
            if (api == null) 
            {
                sapi.Logger.Warning("[Bloodshed:VigorIntegration] SERVER CONSUME: Vigor API not found on server, allowing action");
                return true; // Allow action if Vigor not enabled
            }
            
            try
            {
                sapi.Logger.Event("[Bloodshed:VigorIntegration] SERVER CONSUME: About to call Vigor API ConsumeStamina for player entity {0}, amount {1}", player.EntityId, amount);
                bool result = api.ConsumeStamina(player, amount, false);
                sapi.Logger.Event("[Bloodshed:VigorIntegration] SERVER CONSUME: Vigor API consumption result: {0}", result ? "SUCCESS" : "FAILED");
                return result;
            }
            catch (Exception ex)
            {
                sapi.Logger.Error("[Bloodshed:VigorIntegration] SERVER CONSUME: Error calling Vigor API: {0}", ex.ToString());
                return true; // Allow action if API call fails
            }
        }
        #endregion
        
        #region Client
        private ICoreClientAPI capi;
        private IClientNetworkChannel clientChannel;
        
        public override void StartClientSide(ICoreClientAPI api)
        {
            base.StartClientSide(api);
            this.capi = api;
            
            // Get the channel for client-side sending
            clientChannel = api.Network.GetChannel(NETWORK_CHANNEL);
            
            api.Logger.Event("[Bloodshed:VigorIntegration] CLIENT: Network channel ready for stamina consumption requests");
        }
        
        /// <summary>
        /// Client-side method to request stamina consumption on the server
        /// </summary>
        public bool ConsumeStamina(EntityPlayer player, float amount)
        {
            if (capi == null)
            {
                return true; // Allow action if no client API
            }
            
            capi.Logger.Event("[Bloodshed:VigorIntegration] Attempting to consume {0} stamina for player {1}", amount, player.ToString());
            
            // Check if Vigor is enabled
            if (!IsVigorEnabled(capi))
            {
                capi.Logger.Warning("[Bloodshed:VigorIntegration] Vigor mod not enabled, allowing action");
                return true; // Allow action if Vigor not enabled
            }
            
            try
            {
                // Client side - send to server
                capi.Logger.Event("[Bloodshed:VigorIntegration] Client-side request, sending to server: {0} stamina", amount);
                clientChannel.SendPacket(new StaminaConsumptionPacket { Amount = amount });
                return true; // Allow action for now, server will handle the actual consumption
            }
            catch (Exception ex)
            {
                capi.Logger.Error("[Bloodshed:VigorIntegration] Error sending stamina consumption request: {0}", ex.ToString());
                return true; // Allow action if sending fails
            }
        }
        #endregion
        
    }
    
    /// <summary>
    /// Helper class to provide static access to the vigor integration from other parts of the mod
    /// </summary>
    public static class VigorIntegration
    {
        /// <summary>
        /// Gets the current stamina value for an entity
        /// </summary>
        /// <returns>Current stamina or -1 if Vigor isn't enabled</returns>
        public static float GetCurrentStamina(EntityPlayer player)
        {
            var api = VigorIntegrationSystem.GetVigorAPI(player.Api);
            if (api == null) return -1;
            
            try
            {
                return api.GetCurrentStamina(player);
            }
            catch (Exception ex) { 
                player.Api.Logger.Warning("[Bloodshed:VigorIntegration] Error getting current stamina: {0}", ex.ToString());
                return -1; 
            }
        }
        
        /// <summary>
        /// Gets the maximum stamina value for an entity
        /// </summary>
        /// <returns>Maximum stamina or -1 if Vigor isn't enabled</returns>
        public static float GetMaxStamina(EntityPlayer player)
        {
            var api = VigorIntegrationSystem.GetVigorAPI(player.Api);
            if (api == null) return -1;
            
            try
            {
                return api.GetMaxStamina(player);
            }
            catch (Exception ex)
            {
                player.Api.Logger.Warning("[Bloodshed:VigorIntegration] Error getting max stamina: {0}", ex.ToString());
                return -1;
            }
        }
        
        /// <summary>
        /// Checks if the player is exhausted (has no stamina)
        /// </summary>
        public static bool IsExhausted(EntityPlayer player)
        {
            var api = VigorIntegrationSystem.GetVigorAPI(player.Api);
            if (api == null) return false;
            
            try
            {
                return api.IsExhausted(player);
            }
            catch (Exception ex)
            {
                player.Api.Logger.Warning("[Bloodshed:VigorIntegration] Error checking if exhausted: {0}", ex.ToString());
                return false;
            }
        }
        
        /// <summary>
        /// Consumes stamina if available - client/server compatible method
        /// </summary>
        /// <param name="amount">Amount of stamina to consume</param>
        /// <returns>True if successful, false if not enough stamina or Vigor not enabled</returns>
        public static bool ConsumeStamina(EntityPlayer player, float amount)
        {
            // Debug logging
            player.Api.Logger.Event("[Bloodshed:VigorIntegration] Attempting to consume {0} stamina for player {1}", amount, player.ToString());
            
            if (!VigorIntegrationSystem.IsVigorEnabled(player.Api))
            {
                player.Api.Logger.Debug("[Bloodshed:VigorIntegration] Vigor not enabled, skipping stamina consumption");
                return true; // Allow action if Vigor not enabled
            }
            
            // Handle client side differently - send network request to server
            if (player.Api.Side == EnumAppSide.Client)
            {
                player.Api.Logger.Event("[Bloodshed:VigorIntegration] Client-side request, sending to server: {0} stamina", amount);
                
                // For client side, send a packet to the server
                var clientApi = player.Api as ICoreClientAPI;
                if (clientApi != null)
                {
                    var integrationSystem = clientApi.ModLoader.GetModSystem<VigorIntegrationSystem>();
                    if (integrationSystem != null)
                    {
                        return integrationSystem.ConsumeStamina(player, amount);
                    }
                }
                
                return true; // Allow action if packet send fails (client API unavailable)
            }
            
            // On server side, do the actual consumption
            var serverApi = player.Api as ICoreServerAPI;
            if (serverApi != null)
            {
                var integrationSystem = serverApi.ModLoader.GetModSystem<VigorIntegrationSystem>();
                if (integrationSystem != null)
                {
                    // Call the ModSystem's server-side stamina consumption
                    var serverPlayer = serverApi.World.PlayerByUid(player.PlayerUID);
                    if (serverPlayer != null)
                    {
                        return integrationSystem.ConsumeStaminaOnServer(player, amount);
                    }
                }
            }
            
            return true; // Allow action if something fails
        }
        
        /// <summary>
        /// Drains stamina continuously over time
        /// </summary>
        /// <param name="player">The player</param>
        /// <param name="amountPerSecond">Drain amount per second</param>
        /// <param name="deltaTime">Time elapsed since last frame</param>
        /// <returns>True if stamina was drained successfully</returns>
        public static bool DrainStamina(EntityPlayer player, float amountPerSecond, float deltaTime)
        {
            // Always allow stamina costs (remove config dependency for generic library)
            // Individual mods can add their own config checks before calling this method
            
            var api = VigorIntegrationSystem.GetVigorAPI(player.Api);
            if (api == null) return true; // Allow action if Vigor not enabled
            
            try
            {
                // Generic library - individual mods should handle their own stamina amount calculations
                // before calling this method
                
                return api.DrainStamina(player, amountPerSecond, deltaTime);
            }
            catch (Exception ex)
            {
                player.Api.Logger.Warning("[Bloodshed:VigorIntegration] Error draining stamina: {0}", ex.ToString());
                return true; // Allow action if API call fails
            }
        }
        
        /// <summary>
        /// Starts a continuous stamina drain for a specific action
        /// </summary>
        /// <param name="player">The player</param>
        /// <param name="actionId">Unique identifier for the action causing the drain</param>
        /// <param name="amountPerSecond">Drain amount per second</param>
        /// <returns>True if the drain was started successfully</returns>
        public static bool StartStaminaDrain(EntityPlayer player, string actionId, float amountPerSecond)
        {
            // Always allow stamina costs (remove config dependency for generic library)
            // Individual mods can add their own config checks before calling this method
            
            // For debug logging only
            var api = VigorIntegrationSystem.GetVigorAPI(player.Api);
            if (api == null) return true; // Allow action if Vigor not enabled
            
            // Generic library - individual mods should handle their own stamina amount calculations
            // before calling this method
            
            try
            {
                // Use direct server network approach instead of API call
                if (player.Api.Side == EnumAppSide.Client)
                {
                    player.Api.Logger.Event("[Bloodshed:Climb] Sending direct START drain request to server for '{0}' at {1}/sec", 
                        actionId, amountPerSecond);
                    
                    // Get client network channel
                    var clientApi = player.Api as ICoreClientAPI;
                    if (clientApi != null)
                    {
                        var channel = clientApi.Network.GetChannel("bloodshed:vigor");
                        if (channel != null)
                        {
                            // Send direct network packet to server
                            channel.SendPacket(new StaminaDrainPacket {
                                ActionId = "bloodshed:" + actionId,
                                AmountPerSecond = amountPerSecond,
                                IsStarting = true
                            });
                            
                            return true; // Assume success (server will validate)
                        }
                    }
                }
                else if (player.Api.Side == EnumAppSide.Server)
                {
                    // On server, use API directly
                    return api.StartStaminaDrain(player, "bloodshed:" + actionId, amountPerSecond);
                }
                
                return true; // Default to allowing action
            }
            catch (Exception ex)
            {
                player.Api.Logger.Warning("[Bloodshed:VigorIntegration] Error starting stamina drain: {0}", ex.ToString());
                return true; // Allow action if API call fails
            }
        }
        
        /// <summary>
        /// Stops a continuous stamina drain for a specific action
        /// </summary>
        /// <param name="player">The player</param>
        /// <param name="actionId">Unique identifier for the action that was causing the drain</param>
        public static void StopStaminaDrain(EntityPlayer player, string actionId)
        {
            var api = VigorIntegrationSystem.GetVigorAPI(player.Api);
            if (api == null) return; // Do nothing if Vigor not enabled
            
            try
            {
                // Use direct server network approach instead of API call
                if (player.Api.Side == EnumAppSide.Client)
                {
                    player.Api.Logger.Event("[Bloodshed:Climb] Sending direct STOP drain request to server for '{0}'", actionId);
                    
                    // Get client network channel
                    var clientApi = player.Api as ICoreClientAPI;
                    if (clientApi != null)
                    {
                        var channel = clientApi.Network.GetChannel("bloodshed:vigor");
                        if (channel != null)
                        {
                            // Send direct network packet to server
                            channel.SendPacket(new StaminaDrainPacket {
                                ActionId = "bloodshed:" + actionId,
                                AmountPerSecond = 0, // Not used for stop
                                IsStarting = false
                            });
                            
                            return; // Done with client side handling
                        }
                    }
                }
                else if (player.Api.Side == EnumAppSide.Server)
                {
                    // On server, use API directly
                    api.StopStaminaDrain(player, "bloodshed:" + actionId);
                }
            }
            catch (Exception ex)
            {
                player.Api.Logger.Warning("[Bloodshed:VigorIntegration] Error stopping stamina drain: {0}", ex.ToString());
            }
        }
        
        /// <summary>
        /// Checks if the player can perform a stamina-consuming action
        /// </summary>
        /// <param name="player">The player</param>
        /// <returns>True if the player can perform the action (not exhausted)</returns>
        public static bool CanPerformStaminaAction(EntityPlayer player)
        {
            // Always allow stamina costs (remove config dependency for generic library)
            // Individual mods can add their own config checks before calling this method
            
            var api = VigorIntegrationSystem.GetVigorAPI(player.Api);
            if (api == null) return true; // Allow action if Vigor not enabled
            
            try
            {
                return api.CanPerformStaminaAction(player);
            }
            catch (Exception ex)
            {
                player.Api.Logger.Warning("[Bloodshed:VigorIntegration] Error checking if player can perform action: {0}", ex.ToString());
                return true; // Allow action if API call fails
            }
        }
    }
}
