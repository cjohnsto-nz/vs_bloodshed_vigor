using System;
using static Vintagestory.API.Common.EntityAgent;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Bloodshed.Behaviors;
using Bloodshed.Integration;

namespace Bloodshed.Systems
{
    internal class DefenseSystem
    {
        public static BloodshedModSystem Bloodshed => BloodshedModSystem.Instance;
        
        private readonly ICoreAPI api;

        public DefenseSystem(ICoreAPI api)
        {
            this.api = api;
        }

        public float HandleFatigued(IPlayer player, float fatigue, FatigueSource ftgSource)
        {
            fatigue = ApplyFatigueProtection(player, fatigue, ftgSource);
            
            return fatigue;
        }

        /// <summary>
        /// Placeholder method for fatigue protection.
        /// </summary>
        /// <param name="player"></param>
        /// <param name="fatigue"></param>
        /// <param name="ftgSource"></param>
        /// <returns></returns>
        public float ApplyFatigueProtection(IPlayer player, float fatigue, FatigueSource ftgSource)
        {
            return fatigue;
        }

        public float HandleDamaged(IPlayer player, float damage, DamageSource dmgSource)
        {
            damage = ApplyDamageProtection(player, damage, dmgSource);

            return damage;
        }

        public float ApplyDamageProtection(IPlayer player, float damage, DamageSource dmgSource)
        {
            // Get the relevant stats from the player's item
            ItemSlot activeSlot = player.Entity.RightHandItemSlot;
            var defAttr = activeSlot?.Itemstack?.ItemAttributes?["bloodshed-defense"];
            if (defAttr == null || !defAttr.Exists) return damage;

            var ebs = player.Entity.GetBehavior<EntityBehaviorStamina>();
            
            // Check if player is exhausted using the appropriate stamina system
            bool isExhausted;
            if (Bloodshed.IsVigorPresent)
            {
                // Use Vigor's stamina values when present
                isExhausted = VigorIntegration.GetCurrentStamina(player.Entity as EntityPlayer) <= 0;
            }
            else
            {
                // Use Bloodshed's stamina values
                isExhausted = ebs.Stamina <= 0;
            }
            
            if (isExhausted)
            {
                // Notify player that they're too exhausted to block effectively
                (player as IServerPlayer)?.SendMessage(GlobalConstants.DamageLogChatGroup, 
                    Lang.Get("Too exhausted to block - {0:0.#} damage taken from {1}", damage, 
                    activeSlot?.Itemstack?.GetName() ?? "weapon"), 
                    EnumChatType.Notification);
                return damage;
            }

            float flatdmgabsorb = defAttr["damageAbsorption"].AsFloat(2);

            double horizontalAngleProtectionRange = defAttr["horzProtectionRangeInDegrees"].AsFloat(120) / 2 * GameMath.DEG2RAD;

            double verticalAngleProtectionRange = defAttr["vertProtectionRangeInDegrees"].AsFloat(120) / 2 * GameMath.DEG2RAD;

            float unabsorbedDamage = damage;

            // Check if player is blocking and not aiming to decide protection type
            string usetype = player.Entity.Controls.RightMouseDown && player.Entity.Attributes.GetInt("aiming") != 1 ? "active" : "passive";

            float chance = defAttr["protectionChance"][usetype].AsFloat(0);

            // Get attack angle and decide which protection direction to use
            if (!dmgSource.GetAttackAngle(player.Entity.Pos.XYZ, out var attackYaw, out var attackPitch))
            {
                return damage;
            }

            // Check if attack is vertical or horizontal (vertical attacks are defined as > 65 degrees pitch)
            bool verticalAttack = Math.Abs(attackPitch) > 65 * GameMath.DEG2RAD;
            double playerYaw = player.Entity.Pos.Yaw;
            double playerPitch = player.Entity.Pos.Pitch;

            bool inProtectionRange;
            if (verticalAttack)
            {
                inProtectionRange = Math.Abs(GameMath.AngleRadDistance((float)playerPitch, (float)attackPitch)) < verticalAngleProtectionRange;
            }
            else
            {
                inProtectionRange = Math.Abs(GameMath.AngleRadDistance((float)playerYaw, (float)attackYaw)) < horizontalAngleProtectionRange;
            }

            // If attack lies in protection range absorb damage
            if (inProtectionRange)
            {
                float totaldmgabsorb = 0;
                var rndval = api.World.Rand.NextDouble();

                if (rndval < chance)
                {
                    totaldmgabsorb += flatdmgabsorb;
                }

                // Client notification
                (player as IServerPlayer)?.SendMessage(GlobalConstants.DamageLogChatGroup, Lang.Get("{0:0.#} of {1:0.#} damage blocked by {2} ({3} use)", Math.Min(totaldmgabsorb, damage), damage, activeSlot.Itemstack.GetName(), usetype), EnumChatType.Notification);
                damage = Math.Max(0, damage - totaldmgabsorb);

                // Decrease stamina
                float fatigue = unabsorbedDamage > 6 ? 30 : 15; // Heavy attacks fatigue more
                FatigueSource fs = new()
                {
                    Source = EnumFatigueSource.Defense,
                    SourceEntity = player.Entity,
                    CauseEntity = dmgSource.SourceEntity,
                    SourceBlock = dmgSource.SourceBlock,
                    SourcePos = dmgSource.GetSourcePosition()
                };

                fatigue = HandleFatigued(player, fatigue, fs);

                ebs.OnEntityFatigued(fs, ref fatigue);

                // Block sound
                string key = "blockSound" + (unabsorbedDamage > 6 ? "Heavy" : "Light");
                var loc = activeSlot.Itemstack.ItemAttributes["bloodshed-twohand"][key].AsString("held/shieldblock-wood-light");
                var sloc = AssetLocation.Create(loc, activeSlot.Itemstack.Collectible.Code.Domain).WithPathPrefixOnce("sounds/").WithPathAppendixOnce(".ogg");
                api.World.PlaySoundAt(sloc, player, null);

                // Sync block action to nearby entities
                if (rndval < chance) (api as ICoreServerAPI).Network.BroadcastEntityPacket(player.Entity.EntityId, (int)EntityServerPacketId.PlayPlayerAnim, SerializerUtil.Serialize("bloodshed item block"));

                // Damage item (server side)
                if (api.Side == EnumAppSide.Server)
                {
                    activeSlot.Itemstack.Collectible.DamageItem(api.World, dmgSource.SourceEntity, activeSlot, (int)Math.Round(damage));
                    activeSlot.MarkDirty();
                }
            }

            return damage;
        }
    }
}
