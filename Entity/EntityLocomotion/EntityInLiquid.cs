﻿using System;
using Vintagestory.API;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Vintagestory.GameContent
{
    public class EntityInLiquid : EntityLocomotion
    {
        public long lastWaterJump = 0;
        public long lastPush = 0;

        protected float push;

        public override bool Applicable(Entity entity, EntityPos pos, EntityControls controls)
        {
            return entity.FeetInLiquid;
        }

        public override void DoApply(float dt, Entity entity, EntityPos pos, EntityControls controls)
        {
            if (entity.Swimming && entity.Alive)
            {
                HandleSwimming(dt, entity, pos, controls);
            }


            Block block = entity.World.BlockAccessor.GetBlock((int)pos.X, (int)pos.Y, (int)pos.Z, BlockLayersAccess.Fluid);
            if (block.PushVector != null)
            {
                // Fix for those unfair cases where there is downward flowing water in a 1 deep hole and you cant get out
                if (block.PushVector.Y >= 0 || !entity.World.BlockAccessor.IsSideSolid((int)pos.X, (int)pos.Y - 1, (int)pos.Z, BlockFacing.UP))
                {
                    pos.Motion.Add(block.PushVector);
                }
            }
            
            // http://fooplot.com/plot/kg6l1ikyx2
            /*float x = entity.Pos.Motion.Length();
            if (x > 0)
            {
                pos.Motion.Normalize();
                pos.Motion *= (float)Math.Log(x + 1) / 1.5f;

            }*/
        }

        protected virtual void HandleSwimming(float dt, Entity entity, EntityPos pos, EntityControls controls)
        {
            push = Math.Max(1f, push - 0.1f * dt * 60f);


            Block inblock = entity.World.BlockAccessor.GetBlock((int)pos.X, (int)pos.Y, (int)pos.Z, BlockLayersAccess.Fluid);
            Block aboveblock = entity.World.BlockAccessor.GetBlock((int)pos.X, (int)(pos.Y + 1), (int)pos.Z, BlockLayersAccess.Fluid);
            Block twoaboveblock = entity.World.BlockAccessor.GetBlock((int)pos.X, (int)(pos.Y + 2), (int)pos.Z, BlockLayersAccess.Fluid);
            float waterY = (int)pos.Y + inblock.LiquidLevel / 8f + (aboveblock.IsLiquid() ? 9 / 8f : 0) + (twoaboveblock.IsLiquid() ? 9 / 8f : 0);
            float bottomSubmergedness = waterY - (float)pos.Y;

            // 0 = at swim line
            // 1 = completely submerged
            float swimlineSubmergedness = GameMath.Clamp(bottomSubmergedness - ((float)entity.SwimmingOffsetY), 0, 1);
            swimlineSubmergedness = Math.Min(1, swimlineSubmergedness + 0.075f);


            double yMot;
            if (controls.Jump)
            {
                yMot = 0.005f * swimlineSubmergedness * dt * 60;
            }
            else
            {
                yMot = controls.FlyVector.Y * (1 + push) * 0.03f * swimlineSubmergedness;
            }

            if (entity.Properties.Habitat == EnumHabitat.Underwater && inblock.IsLiquid() && !aboveblock.IsLiquid())
            {
                float maxY = (int)pos.Y + inblock.LiquidLevel / 8f - entity.CollisionBox.Y2;
                if (pos.Y > maxY)
                {
                    yMot = -GameMath.Clamp(pos.Y - maxY, 0, 0.05);
                }
            }

            pos.Motion.Add(
                controls.FlyVector.X * (1 + push) * 0.03f,
                yMot,
                controls.FlyVector.Z * (1 + push) * 0.03f
            );
        }
    }
}
