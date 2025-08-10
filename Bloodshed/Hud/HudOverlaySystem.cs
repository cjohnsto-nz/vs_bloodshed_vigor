using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace Bloodshed.Hud
{
    public class HudOverlaySystem : ModSystem, IDisposable
    {
        ICoreClientAPI capi;
        StaminaBarRenderer renderer;
        private static BloodshedModSystem Core => BloodshedModSystem.Instance;

        public override bool ShouldLoad(EnumAppSide forSide)
        {
            return forSide == EnumAppSide.Client;
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;
            
            // Only register stamina bar if Vigor is not present
            if (!Core.IsVigorPresent)
            {
                renderer = new StaminaBarRenderer(api);
                api.Event.RegisterRenderer(renderer, EnumRenderStage.Ortho, $"{Core.ModId}:staminabar");
            }
            else
            {
                Core.Logger.Notification("Vigor detected - Bloodshed stamina UI suppressed");
            }
        }

        public override void Dispose()
        {
            renderer?.Dispose();
        }
    }
}
