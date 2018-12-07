﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Vintagestory.API;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace Vintagestory.GameContent
{
    public class EntityBehaviorFloatUpWhenStuck : EntityBehavior
    {
        bool onlyWhenDead;

        int counter = 0;
        bool stuckInBlock;
        Vec3d tmpPos = new Vec3d();


        public EntityBehaviorFloatUpWhenStuck(Entity entity) : base(entity)
        {

        }

        public override void Initialize(EntityProperties properties, JsonObject attributes)
        {
            base.Initialize(properties, attributes);

            onlyWhenDead = attributes["onlyWhenDead"].AsBool(false);
        }


        public override void OnGameTick(float deltaTime)
        {
            if (counter++ > 10 || stuckInBlock)
            {
                if (onlyWhenDead && entity.Alive) return;

                stuckInBlock = false;
                counter = 0;
                entity.Properties.Habitat = EnumHabitat.Land;
                if (!entity.Swimming)
                {
                    tmpPos.Set(entity.LocalPos.X, entity.LocalPos.Y, entity.LocalPos.Z);
                    Cuboidd collbox = entity.World.CollisionTester.GetCollidingCollisionBox(entity.World.BlockAccessor, entity.CollisionBox, tmpPos, false);

                    if (collbox != null)
                    {
                        PushoutOfCollisionbox(deltaTime, collbox);
                        stuckInBlock = true;
                    }
                }
            }
        }




        private void PushoutOfCollisionbox(float dt, Cuboidd collBox)
        {
            double posX = entity.LocalPos.X;
            double posY = entity.LocalPos.Y;
            double posZ = entity.LocalPos.Z;
            /// North: Negative Z
            /// East: Positive X
            /// South: Positive Z
            /// West: Negative X

            double[] distByFacing = new double[]
            {
                posZ - collBox.Z1, // N
                collBox.X2 - posX, // E
                collBox.Z2 - posZ, // S
                posX - collBox.X1, // W
                collBox.Y2 - posY, // U
                99 // D
            };

            BlockFacing pushDir = BlockFacing.UP;
            double shortestDist = 99;
            for (int i = 0; i < distByFacing.Length; i++)
            {
                BlockFacing face = BlockFacing.ALLFACES[i];
                if (distByFacing[i] < shortestDist && !entity.World.CollisionTester.IsColliding(entity.World.BlockAccessor, entity.CollisionBox, tmpPos.Set(posX + face.Normali.X, posY, posZ + face.Normali.Z)))
                {
                    shortestDist = distByFacing[i];
                    pushDir = face;
                }
            }

            dt = Math.Min(dt, 0.1f);

            entity.LocalPos.X += pushDir.Normali.X * dt;
            entity.LocalPos.Y += pushDir.Normali.Y * dt;
            entity.LocalPos.Z += pushDir.Normali.Z * dt;

            entity.LocalPos.Motion.X = pushDir.Normali.X * dt;
            entity.LocalPos.Motion.Y = pushDir.Normali.Y * dt * 2;
            entity.LocalPos.Motion.Z = pushDir.Normali.Z * dt;

            entity.Properties.Habitat = EnumHabitat.Air;
        }


        public override string PropertyName()
        {
            return "floatupwhenstuck";
        }
    }
}
