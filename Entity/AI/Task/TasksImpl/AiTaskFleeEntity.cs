﻿using System;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;

namespace Vintagestory.GameContent
{
    public class AiTaskFleeEntity : AiTaskBaseTargetable
    {
        Vec3d targetPos = new Vec3d();
        float moveSpeed = 0.02f;
        float seekingRange = 25f;
        float executionChance = 0.1f;
        float fleeingDistance = 31f;
        float minDayLight = -1f;
        float fleeDurationMs = 5000;
        bool cancelOnHurt = false;

        long fleeStartMs;
        bool stuck;
        
        bool lowStabilityAttracted;
        bool ignoreDeepDayLight;
        float tamingGenerations = 10f;
		bool cancelNow;
        public override bool AggressiveTargeting => false;


        public AiTaskFleeEntity(EntityAgent entity) : base(entity)
        {
        }

        public override void LoadConfig(JsonObject taskConfig, JsonObject aiConfig)
        {
            base.LoadConfig(taskConfig, aiConfig);

            tamingGenerations = taskConfig["tamingGenerations"].AsFloat(10f);
            moveSpeed = taskConfig["movespeed"].AsFloat(0.02f);
            seekingRange = taskConfig["seekingRange"].AsFloat(25);
            executionChance = taskConfig["executionChance"].AsFloat(0.1f);
            minDayLight = taskConfig["minDayLight"].AsFloat(-1f);
            cancelOnHurt = taskConfig["cancelOnHurt"].AsBool(false);
            ignoreDeepDayLight = taskConfig["ignoreDeepDayLight"].AsBool(false);
            fleeingDistance = taskConfig["fleeingDistance"].AsFloat(seekingRange + 15);
            fleeDurationMs = taskConfig["fleeDurationMs"].AsInt(9000);
            lowStabilityAttracted = entity.World.Config.GetString("temporalStability").ToBool(true) && entity.Properties.Attributes?["spawnCloserDuringLowStability"].AsBool() == true;
        }



        readonly Vec3d ownPos = new Vec3d();
        public override bool ShouldExecute()
        {
            soundChance = Math.Min(1.01f, soundChance + 1 / 500f);

            if (rand.NextDouble() > 2 * executionChance) return false;
            if (noEntityCodes && (attackedByEntity == null || !retaliateAttacks)) return false;

            if (whenInEmotionState != null && bhEmo?.IsInEmotionState(whenInEmotionState) != true) return false;
            if (whenNotInEmotionState != null && bhEmo?.IsInEmotionState(whenNotInEmotionState) == true) return false;

            // Double exec chance, but therefore halved here again to increase response speed for creature when aggressive
            if (whenInEmotionState == null && rand.NextDouble() > 0.5f) return false;

            // This code section controls drifter behavior - they retreat (flee slowly) from the player in the daytime, this is "switched off" below ground or at night, also switched off in temporal storms
            // Has to be checked every tick because the drifter attributes change during temporal storms  (grrr, this is a slow way to do it)
            if (minDayLight > 0 && !entity.Attributes.GetBool("ignoreDaylightFlee", false))
            {
                if (ignoreDeepDayLight && entity.ServerPos.Y < world.SeaLevel - 2) return false;

                float sunlight = entity.World.BlockAccessor.GetLightLevel((int)entity.ServerPos.X, (int)entity.ServerPos.Y, (int)entity.ServerPos.Z, EnumLightLevelType.TimeOfDaySunLight) / (float)entity.World.SunBrightness;
                if (sunlight < minDayLight) return false;
            }

            int generation = entity.WatchedAttributes.GetInt("generation", 0);
            float fearReductionFactor = (whenInEmotionState != null) ? 1 : Math.Max(0f, (tamingGenerations - generation) / tamingGenerations);

            ownPos.Set(entity.ServerPos);
            float hereRange = fearReductionFactor * seekingRange;

            entity.World.FrameProfiler.Mark("task-fleeentity-shouldexecute-init");

            if (lowStabilityAttracted)
            {
                targetEntity = (EntityAgent)partitionUtil.GetNearestInteractableEntity(ownPos, hereRange, (e) =>
                {
                    if (!IsTargetableEntity(e, hereRange)) return false;
                    if (!(e is EntityPlayer)) return true;
                    return e.WatchedAttributes.GetDouble("temporalStability", 1) > 0.25;
                });
            }
            else
            {
                targetEntity = (EntityAgent)partitionUtil.GetNearestInteractableEntity(ownPos, hereRange, (e) => IsTargetableEntity(e, hereRange));
            }
            entity.World.FrameProfiler.Mark("task-fleeentity-shouldexecute-entitysearch");


            if (targetEntity != null)
            {
                updateTargetPosFleeMode(targetPos);
                return true;
            }

            return false;
        }


        public override void StartExecute()
        {
            base.StartExecute();

            cancelNow = false;

            soundChance = Math.Max(0.025f, soundChance - 0.2f);

            float size = targetEntity.SelectionBox.XSize;

            pathTraverser.WalkTowards(targetPos, moveSpeed, size + 0.2f, OnGoalReached, OnStuck);

            fleeStartMs = entity.World.ElapsedMilliseconds;
            stuck = false;
        }


        public override bool ContinueExecute(float dt)
        {
            if (world.Rand.NextDouble() < 0.2)
            {
                updateTargetPosFleeMode(targetPos);
                pathTraverser.CurrentTarget.X = targetPos.X;
                pathTraverser.CurrentTarget.Y = targetPos.Y;
                pathTraverser.CurrentTarget.Z = targetPos.Z;
                pathTraverser.Retarget();
            }

            if (entity.ServerPos.SquareDistanceTo(targetEntity.ServerPos) > fleeingDistance * fleeingDistance)
            {
                return false;
            }

            if (world.Rand.NextDouble() < 0.25)
            {
                float sunlight = entity.World.BlockAccessor.GetLightLevel((int)entity.ServerPos.X, (int)entity.ServerPos.Y, (int)entity.ServerPos.Z, EnumLightLevelType.TimeOfDaySunLight) / (float)entity.World.SunBrightness;
                if ((ignoreDeepDayLight && entity.ServerPos.Y < world.SeaLevel - 2) || sunlight < minDayLight)
                {
                    if (!entity.Attributes.GetBool("ignoreDaylightFlee", false))
                    {
                        return false;
                    }
                }
            }

            return !stuck && targetEntity.Alive && (entity.World.ElapsedMilliseconds - fleeStartMs < fleeDurationMs) && !cancelNow && pathTraverser.Active;
        }


        

        


        public override void OnEntityHurt(DamageSource source, float damage)
        {
            base.OnEntityHurt(source, damage);

            if (cancelOnHurt) cancelNow = true;
        }


        public override void FinishExecute(bool cancelled)
        {
            pathTraverser.Stop();
            base.FinishExecute(cancelled);
        }


        private void OnStuck()
        {
            stuck = true;
        }

        private void OnGoalReached()
        {
            pathTraverser.Retarget();
        }
    }
}
