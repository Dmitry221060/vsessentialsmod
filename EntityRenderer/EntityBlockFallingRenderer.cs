﻿using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace Vintagestory.GameContent
{
    /// <summary>
    /// Handles rendering of falling blocks
    /// </summary>
    public class EntityBlockFallingRenderer : EntityRenderer, ITerrainMeshPool
    {
        protected EntityBlockFalling blockFallingEntity;
        protected MeshRef meshRef;
        protected Block block;
        protected int atlasTextureId;
        protected Matrixf ModelMat = new Matrixf();

        double accum;
        Vec3d prevPos = new Vec3d();
        Vec3d curPos = new Vec3d();
        internal bool DoRender;

        MeshData mesh = new MeshData(4, 3, false, true, true, true);

        public EntityBlockFallingRenderer(Entity entity, ICoreClientAPI api) : base(entity, api)
        {
            this.blockFallingEntity = (EntityBlockFalling)entity;
            this.block = blockFallingEntity.Block;

            entity.PhysicsUpdateWatcher = OnPhysicsTick;

            if (!blockFallingEntity.InitialBlockRemoved)
            {
                int posx = blockFallingEntity.blockEntityAttributes?.GetInt("posx", blockFallingEntity.initialPos.X) ?? blockFallingEntity.initialPos.X;
                int posy = blockFallingEntity.blockEntityAttributes?.GetInt("posy", blockFallingEntity.initialPos.Y) ?? blockFallingEntity.initialPos.Y;
                int posz = blockFallingEntity.blockEntityAttributes?.GetInt("posz", blockFallingEntity.initialPos.Z) ?? blockFallingEntity.initialPos.Z;
                
                BlockEntity be = api.World.BlockAccessor.GetBlockEntity(new BlockPos(posx, posy, posz));
                be?.OnTesselation(this, capi.Tesselator);

                if (mesh.VerticesCount > 0)
                {
                    mesh.CustomBytes = null;
                    mesh.CustomFloats = null;
                    mesh.CustomInts = null;
                    this.meshRef = capi.Render.UploadMesh(mesh);
                }
            }

            if (this.meshRef == null)
            {
                MeshData mesh = api.TesselatorManager.GetDefaultBlockMesh(block);
                this.meshRef = api.Render.UploadMesh(mesh);
            }
            
            int textureSubId = block.FirstTextureInventory.Baked.TextureSubId;
            this.atlasTextureId = api.BlockTextureAtlas.Positions[textureSubId].atlasTextureId;

            prevPos.Set(entity.Pos.X + entity.SelectionBox.X1, entity.Pos.Y + entity.SelectionBox.Y1, entity.Pos.Z + entity.SelectionBox.Z1);
        }


        public void OnPhysicsTick(float nextAccum, Vec3d prevPos)
        {
            this.accum = nextAccum;
            this.prevPos.Set(prevPos.X + entity.SelectionBox.X1, prevPos.Y + entity.SelectionBox.Y1, prevPos.Z + entity.SelectionBox.Z1);
        }

        public void AddMeshData(MeshData data, int lodlevel = 1)
        {
            if (data == null) return;
            mesh.AddMeshData(data);
        }

        public void AddMeshData(MeshData data, ColorMapData colormapdata, int lodlevel = 1)
        {
            if (data == null) return;
            mesh.AddMeshData(data);
        }
        public void AddMeshData(MeshData data, float[] tfMatrix, int lodLevel = 1)
        {
            if (data == null) return;
            mesh.AddMeshData(data);
        }

        public override void DoRender3DOpaque(float dt, bool isShadowPass)
        {
            // TODO: ADD
            if (isShadowPass) return;

            if (!DoRender || (!blockFallingEntity.InitialBlockRemoved && entity.World.BlockAccessor.GetBlock(blockFallingEntity.initialPos).Id != 0)) return;

            curPos.Set(entity.Pos.X + entity.SelectionBox.X1, entity.Pos.Y + entity.SelectionBox.Y1, entity.Pos.Z + entity.SelectionBox.Z1);

            RenderFallingBlockEntity();
        }

        private void RenderFallingBlockEntity()
        {
            IRenderAPI rapi = capi.Render;

            rapi.GlDisableCullFace();
            
            rapi.GlToggleBlend(true, EnumBlendMode.Standard);
            
            double alpha = accum / GlobalConstants.PhysicsFrameTime;

            float div = entity.Collided ? 4f : 1.5f;

            IStandardShaderProgram prog = rapi.PreparedStandardShader((int)entity.Pos.X, (int)(entity.Pos.Y + 0.2), (int)entity.Pos.Z);
            Vec3d camPos = capi.World.Player.Entity.CameraPos;
            prog.Tex2D = atlasTextureId;
            prog.ModelMatrix = ModelMat
                .Identity()
                .Translate(
                    prevPos.X * (1 - alpha) + curPos.X * alpha - camPos.X + GameMath.Sin(capi.InWorldEllapsedMilliseconds / 120f + 30) / 20f / div,
                    prevPos.Y * (1 - alpha) + curPos.Y * alpha - camPos.Y,
                    prevPos.Z * (1 - alpha) + curPos.Z * alpha - camPos.Z + GameMath.Cos(capi.InWorldEllapsedMilliseconds / 110f + 20) / 20f / div
                )
                .RotateX(GameMath.Sin(capi.InWorldEllapsedMilliseconds / 100f) / 15f / div)
                .RotateZ(GameMath.Cos(10 + capi.InWorldEllapsedMilliseconds / 90f) / 15f / div)
                .Values
            ;
            prog.ViewMatrix = rapi.CameraMatrixOriginf;
            prog.ProjectionMatrix = rapi.CurrentProjectionMatrix;
            rapi.RenderMesh(meshRef);
            prog.Stop();
        }

        public override void Dispose()
        {
            capi.Render.DeleteMesh(meshRef);
        }
    }
}
