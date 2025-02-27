﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vintagestory.API;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;

namespace Vintagestory.GameContent
{
    public class MessageTexture
    {
        public LoadedTexture tex;
        public string message;
        public long receivedTime;
    }

    public delegate void OnEntityShapeTesselationDelegate(ref Shape entityShape, string shapePathForLogging);
    public delegate float OnGetFrostAlpha();
    
    public class EntityShapeRenderer : EntityRenderer, ITexPositionSource
    {
        protected LoadedTexture nameTagTexture = null;
        protected int renderRange = 999;
        protected bool showNameTagOnlyWhenTargeted = false;

        protected LoadedTexture debugTagTexture = null;

        protected MeshRef meshRefOpaque;

        protected Vec4f color = new Vec4f(1, 1, 1, 1);
        protected long lastDebugInfoChangeMs = 0;
        protected bool isSpectator;
        protected IClientPlayer player;
        public float bodyYawLerped = 0;

        public Vec3f OriginPos = new Vec3f();
        public float[] ModelMat = Mat4f.Create();
        protected float[] tmpMvMat = Mat4f.Create();
        protected Matrixf ItemModelMat = new Matrixf();

        public bool DoRenderHeldItem;
        public bool DisplayChatMessages;

        public int AddRenderFlags;
        public double WindWaveIntensity = 1f;
        public bool glitchFlicker;

        public bool frostable;
        public float frostAlpha;
        public float targetFrostAlpha;
        public OnGetFrostAlpha getFrostAlpha;
        public float frostAlphaAccum;

        protected List<MessageTexture> messageTextures = null;
        protected NameTagRendererDelegate nameTagRenderHandler;

        
        protected EntityAgent eagent;

        public CompositeShape OverrideCompositeShape;
        public Shape OverrideEntityShape;

        public bool glitchAffected;
        protected IInventory gearInv;

        ITexPositionSource defaultTexSource;

        protected bool shapeFresh;
        Vec4f lightrgbs;
        float intoxIntensity;


        /// <summary>
        /// This is called before entity.OnTesselation()
        /// </summary>
        public event OnEntityShapeTesselationDelegate OnTesselation;


        protected Dictionary<string, CompositeTexture> extraTexturesByTextureName
        {
            get
            {
                return ObjectCacheUtil.GetOrCreate(capi, "entityShapeExtraTexturesByName", () => new Dictionary<string, CompositeTexture>());
            }
        }

        protected Dictionary<AssetLocation, BakedCompositeTexture> extraTextureByLocation
        {
            get
            {
                return ObjectCacheUtil.GetOrCreate(capi, "entityShapeExtraTexturesByLoc", () => new Dictionary<AssetLocation, BakedCompositeTexture>());
            }
        }


        public Size2i AtlasSize { get { return capi.EntityTextureAtlas.Size; } }
        protected TextureAtlasPosition skinTexPos;
        public virtual TextureAtlasPosition this[string textureCode]
        {
            get
            {
                CompositeTexture cpt = null;
                if (extraTexturesByTextureName?.TryGetValue(textureCode, out cpt) == true)
                {
                    return capi.EntityTextureAtlas.Positions[cpt.Baked.TextureSubId];
                }

                return defaultTexSource[textureCode];
            }
        }


        public EntityShapeRenderer(Entity entity, ICoreClientAPI api) : base(entity, api)
        {
            eagent = entity as EntityAgent;

            DoRenderHeldItem = true;
            DisplayChatMessages = entity is EntityPlayer;
            
            // For players the player data is not there yet, so we load the thing later
            if (!(entity is EntityPlayer))
            {
                nameTagRenderHandler = api.ModLoader.GetModSystem<EntityNameTagRendererRegistry>().GetNameTagRenderer(entity);
            }

            glitchAffected = true;// entity.Properties.Attributes?["glitchAffected"].AsBool(false) ?? false;
            glitchFlicker = entity.Properties.Attributes?["glitchFlicker"].AsBool(false) ?? false;
            frostable = entity.Properties.Attributes?["frostable"].AsBool(true) ?? true;
            frostAlphaAccum = (float)api.World.Rand.NextDouble();

            entity.WatchedAttributes.OnModified.Add(new TreeModifiedListener() { path = "nametag", listener = OnNameChanged });
            OnNameChanged();
            api.Event.RegisterGameTickListener(UpdateDebugInfo, 250);
            OnDebugInfoChanged();

            if (DisplayChatMessages)
            {
                messageTextures = new List<MessageTexture>();
                api.Event.ChatMessage += OnChatMessage;
            }

            api.Event.ReloadShapes += MarkShapeModified;

            getFrostAlpha = () =>
            {
                var pos = entity.Pos.AsBlockPos;
                float temp = api.World.BlockAccessor.GetClimateAt(pos, EnumGetClimateMode.ForSuppliedDate_TemperatureOnly, api.World.Calendar.TotalDays).Temperature;
                float dist = 1 - GameMath.Clamp((api.World.BlockAccessor.GetDistanceToRainFall(pos, 5) - 2) / 3f, 0, 1f);
                return GameMath.Clamp((Math.Max(0, -temp) - 5) / 5f, 0, 1) * dist;
            };
        }

        public virtual void MarkShapeModified()
        {
            shapeFresh = false;

            /*if (entity.IsRendered) - tyron 19dec 2020: do we need this? if many slots are marked dirty at once, this will cause multiple tesselatins. We already tesselate in BeforeRender() anyway
            {
                TesselateShape();
            }*/
        }


        bool loaded = false;
        public override void OnEntityLoaded()
        {
            loaded = true;
            prevY = entity.Pos.Y;
            MarkShapeModified();
        }

        protected void OnChatMessage(int groupId, string message, EnumChatType chattype, string data)
        {
            if (data != null && data.Contains("from:") && entity.Pos.SquareDistanceTo(capi.World.Player.Entity.Pos.XYZ) < 20*20 && message.Length > 0)
            {
                int entityid;
                string[] parts = data.Split(new char[] { ',' }, 2);
                if (parts.Length < 2) return;

                string[] partone = parts[0].Split(new char[] { ':' }, 2);
                string[] parttwo = parts[1].Split(new char[] { ':' }, 2);
                if (partone[0] != "from") return;

                int.TryParse(partone[1], out entityid);
                if (entity.EntityId == entityid)
                {
                    message = parttwo[1];

                    // Crappy fix
                    message = message.Replace("&lt;", "<").Replace("&gt;", ">");

                    LoadedTexture tex = capi.Gui.TextTexture.GenTextTexture(
                        message,
                        new CairoFont(25, GuiStyle.StandardFontName, ColorUtil.WhiteArgbDouble),
                        350,
                        new TextBackground() { FillColor = GuiStyle.DialogLightBgColor, Padding = 3, Radius = GuiStyle.ElementBGRadius },
                        EnumTextOrientation.Center
                    );

                    messageTextures.Insert(0, new MessageTexture()
                    {
                        tex = tex,
                        message = message,
                        receivedTime = capi.World.ElapsedMilliseconds
                    });

                }
            }
        }


        


        public virtual void TesselateShape()
        {
            if (!loaded)
            {
                return;
            }

            shapeFresh = true;
            CompositeShape compositeShape = OverrideCompositeShape != null ? OverrideCompositeShape : entity.Properties.Client.Shape;

            Shape entityShape = OverrideEntityShape != null ? OverrideEntityShape : entity.Properties.Client.LoadedShapeForEntity;

            if (entityShape == null)
            {
                return;
            }

            OnTesselation?.Invoke(ref entityShape, compositeShape.Base.ToString());
            entity.OnTesselation(ref entityShape, compositeShape.Base.ToString());

            defaultTexSource = GetTextureSource();

            TyronThreadPool.QueueTask(() =>
            {
                MeshData meshdata;

                if (entity.Properties.Client.Shape.VoxelizeTexture)
                {
                    int altTexNumber = entity.WatchedAttributes.GetInt("textureIndex", 0);

                    TextureAtlasPosition pos = defaultTexSource["all"];
                    CompositeTexture[] Alternates = entity.Properties.Client.FirstTexture.Alternates;

                    CompositeTexture tex = altTexNumber == 0 ? entity.Properties.Client.FirstTexture : Alternates[altTexNumber % Alternates.Length];
                    meshdata = capi.Tesselator.VoxelizeTexture(tex, capi.EntityTextureAtlas.Size, pos);
                    for (int i = 0; i < meshdata.xyz.Length; i += 3)
                    {
                        meshdata.xyz[i] -= 0.125f;
                        meshdata.xyz[i + 1] -= 0.5f;
                        meshdata.xyz[i + 2] += 0.125f / 2;
                    }
                }
                else
                {
                    try
                    {
                        TesselationMetaData meta = new TesselationMetaData()
                        {
                            QuantityElements = compositeShape.QuantityElements,
                            SelectiveElements = compositeShape.SelectiveElements,
                            TexSource = this,
                            WithJointIds = true,
                            WithDamageEffect = true,
                            TypeForLogging = "entity",
                            Rotation = new Vec3f(compositeShape.rotateX, compositeShape.rotateY, compositeShape.rotateZ)
                        };

                        capi.Tesselator.TesselateShape(meta, entityShape, out meshdata);

                        meshdata.Translate(compositeShape.offsetX, compositeShape.offsetY, compositeShape.offsetZ);
                        
                    }
                    catch (Exception e)
                    {
                        capi.World.Logger.Fatal("Failed tesselating entity {0} with id {1}. Entity will probably be invisible!. The teselator threw {2}", entity.Code, entity.EntityId, e);
                        return;
                    }
                }

                MeshData opaqueMesh = meshdata.Clone().Clear();
                opaqueMesh.AddMeshData(meshdata);
                //MeshData oitMesh = meshdata.Clone().Clear();
                //opaqueMesh.AddMeshData(meshdata, EnumChunkRenderPass.Opaque);
                //oitMesh.AddMeshData(meshdata, EnumChunkRenderPass.Transparent);

                capi.Event.EnqueueMainThreadTask(() =>
                {
                    if (meshRefOpaque != null)
                    {
                        meshRefOpaque.Dispose();
                        meshRefOpaque = null;
                    }

                    if (capi.IsShuttingDown)
                    {
                        return;
                    }

                    if (opaqueMesh.VerticesCount > 0)
                    {
                        meshRefOpaque = capi.Render.UploadMesh(opaqueMesh);
                    }

                }, "uploadentitymesh");

                capi.TesselatorManager.ThreadDispose();
            });
        }


        protected virtual ITexPositionSource GetTextureSource()
        {
            int altTexNumber = entity.WatchedAttributes.GetInt("textureIndex", 0);
            
            return capi.Tesselator.GetTextureSource(entity, extraTexturesByTextureName, altTexNumber);
        }


        protected void UpdateDebugInfo(float dt)
        {
            OnDebugInfoChanged();

            entity.DebugAttributes.MarkClean();
        }



        protected void OnDebugInfoChanged()
        {
            bool showDebuginfo = capi.Settings.Bool["showEntityDebugInfo"];

            if (showDebuginfo && !entity.DebugAttributes.AllDirty && !entity.DebugAttributes.PartialDirty && debugTagTexture != null) return;

            if (debugTagTexture != null)
            {
                // Don't refresh if player is more than 10 blocks away, so its less laggy
                if (showDebuginfo && capi.World.Player.Entity.Pos.SquareDistanceTo(entity.Pos) > 15 * 15 && debugTagTexture.Width > 10)
                {
                    return;
                }

                debugTagTexture.Dispose();
                debugTagTexture = null;
            }

            if (!showDebuginfo) return;


            StringBuilder text = new StringBuilder();
            foreach (KeyValuePair<string, IAttribute> val in entity.DebugAttributes)
            {
                text.AppendLine(val.Key +": " + val.Value.ToString());
            }

            debugTagTexture = capi.Gui.TextTexture.GenUnscaledTextTexture(
                text.ToString(), 
                new CairoFont(20, GuiStyle.StandardFontName, ColorUtil.WhiteArgbDouble), 
                new TextBackground() { FillColor = GuiStyle.DialogDefaultBgColor, Padding = 3, Radius = GuiStyle.ElementBGRadius }
            );

            lastDebugInfoChangeMs = entity.World.ElapsedMilliseconds;
        }

        protected void OnNameChanged()
        {
            var bh = entity.GetBehavior<EntityBehaviorNameTag>();
            if (nameTagRenderHandler == null || bh == null) return;
            if (nameTagTexture != null)
            {
                nameTagTexture.Dispose();
                nameTagTexture = null;
            }

            renderRange = bh.RenderRange;
            showNameTagOnlyWhenTargeted = bh.ShowOnlyWhenTargeted;
            nameTagTexture = nameTagRenderHandler.Invoke(capi, entity);
        }

        public string GetNameTagName()
        {
            EntityBehaviorNameTag behavior = entity.GetBehavior<EntityBehaviorNameTag>();
            return behavior?.DisplayName;
        }


        public override void BeforeRender(float dt)
        {
            if (!shapeFresh)
            {
                TesselateShape();
            }

            lightrgbs = capi.World.BlockAccessor.GetLightRGBs((int)(entity.Pos.X + entity.SelectionBox.X1 - entity.OriginSelectionBox.X1), (int)entity.Pos.Y, (int)(entity.Pos.Z + entity.SelectionBox.Z1 - entity.OriginSelectionBox.Z1));

            if (entity.SelectionBox.Y2 > 1)
            {
                Vec4f lightrgbs2 = capi.World.BlockAccessor.GetLightRGBs((int)(entity.Pos.X + entity.SelectionBox.X1 - entity.OriginSelectionBox.X1), (int)entity.Pos.Y + 1, (int)(entity.Pos.Z + entity.SelectionBox.Z1 - entity.OriginSelectionBox.Z1));
                if (lightrgbs2.W > lightrgbs.W) lightrgbs = lightrgbs2;
            }

            if (meshRefOpaque == null) return;

            if (gearInv == null && eagent?.GearInventory != null)
            {
                registerSlotModified();
                shapeFresh = true;
            }


            if (capi.IsGamePaused) return;

            if (player == null && entity is EntityPlayer)
            {
                player = capi.World.PlayerByUid((entity as EntityPlayer).PlayerUID) as IClientPlayer;

                nameTagRenderHandler = capi.ModLoader.GetModSystem<EntityNameTagRendererRegistry>().GetNameTagRenderer(entity);
                OnNameChanged();
            }

            frostAlphaAccum += dt;
            if (frostAlphaAccum > 5)
            {
                frostAlphaAccum = 0;
                targetFrostAlpha = getFrostAlpha();
            }


            isSpectator = player != null && player.WorldData.CurrentGameMode == EnumGameMode.Spectator;
            if (isSpectator) return;
            

            if (DisplayChatMessages && messageTextures.Count > 0)
            {
                MessageTexture tex = messageTextures.Last();
                if (capi.World.ElapsedMilliseconds > tex.receivedTime + 3500 + 100 * (tex.message.Length - 10))
                {
                    messageTextures.RemoveAt(messageTextures.Count - 1);
                    tex.tex.Dispose();
                }
            }

            if (player != null)
            {
                calcSidewaysSwivelForPlayer(dt);
            } else
            {
                calcSidewaysSwivelForEntity(dt);
            }
        }



        int skipRenderJointId = -2;
        int skipRenderJointId2 = -2;

        public override void DoRender3DOpaque(float dt, bool isShadowPass)
        {
            if (isSpectator) return;

            if (player != null)
            {
                bool isSelf = capi.World.Player.Entity.EntityId == entity.EntityId;
                loadModelMatrixForPlayer(entity, isSelf, dt);
                if (isSelf)
                {
                    bool immersiveFpMode = capi.Settings.Bool["immersiveFpMode"];
                    if (!isShadowPass && capi.Render.CameraType == EnumCameraMode.FirstPerson && !immersiveFpMode) return;

                    OriginPos.Set(0, 0, 0);

                    if (capi.Render.CameraType != EnumCameraMode.FirstPerson || !immersiveFpMode || isShadowPass)
                    {
                        skipRenderJointId = -2;
                        skipRenderJointId2 = -2;
                    } else
                    {
                        skipRenderJointId = entity.AnimManager.HeadController.HeadPose.ForElement.JointId;

                        if (entity.AnimManager.HeadController.NeckPose.ForElement != null)
                        {
                            skipRenderJointId2 = entity.AnimManager.HeadController.NeckPose.ForElement.JointId;
                        }
                    }
                }

            }
            else
            {
                loadModelMatrix(entity, dt, isShadowPass);
                Vec3d camPos = capi.World.Player.Entity.CameraPos;
                OriginPos.Set((float)(entity.Pos.X - camPos.X), (float)(entity.Pos.Y - camPos.Y), (float)(entity.Pos.Z - camPos.Z));
            }

            if (isShadowPass)
            {
                DoRender3DAfterOIT(dt, true);
            }
            
            // This was rendered in DoRender3DAfterOIT() - WHY? It makes torches render in front of water
            if (DoRenderHeldItem && !entity.AnimManager.ActiveAnimationsByAnimCode.ContainsKey("lie") && !isSpectator)
            {
                RenderHeldItem(dt, isShadowPass, false);
                RenderHeldItem(dt, isShadowPass, true);
            }
        }


        public override void DoRender3DAfterOIT(float dt, bool isShadowPass)
        {
            
        }


        float accum = 0;
        
        protected void RenderHeldItem(float dt, bool isShadowPass, bool right)
        {
            IRenderAPI rapi = capi.Render;
            ItemSlot slot = right ? eagent?.RightHandItemSlot : eagent?.LeftHandItemSlot;
            ItemStack stack = slot?.Itemstack;

            AttachmentPointAndPose apap = entity.AnimManager.Animator.GetAttachmentPointPose(right ? "RightHand" : "LeftHand");
            if (apap == null || stack == null) return;
            
            AttachmentPoint ap = apap.AttachPoint;
            ItemRenderInfo renderInfo = rapi.GetItemStackRenderInfo(slot, right ? EnumItemRenderTarget.HandTp : EnumItemRenderTarget.HandTpOff);
            IStandardShaderProgram prog = null;

            if (renderInfo?.Transform == null) return; // Happens with unknown items/blocks
            
            ItemModelMat
                .Set(ModelMat)
                .Mul(apap.AnimModelMatrix)
                .Translate(renderInfo.Transform.Origin.X, renderInfo.Transform.Origin.Y, renderInfo.Transform.Origin.Z)
                .Scale(renderInfo.Transform.ScaleXYZ.X, renderInfo.Transform.ScaleXYZ.Y, renderInfo.Transform.ScaleXYZ.Z)
                .Translate(ap.PosX / 16f + renderInfo.Transform.Translation.X, ap.PosY / 16f + renderInfo.Transform.Translation.Y, ap.PosZ / 16f + renderInfo.Transform.Translation.Z)
                .RotateX((float)(ap.RotationX + renderInfo.Transform.Rotation.X) * GameMath.DEG2RAD)
                .RotateY((float)(ap.RotationY + renderInfo.Transform.Rotation.Y) * GameMath.DEG2RAD)
                .RotateZ((float)(ap.RotationZ + renderInfo.Transform.Rotation.Z) * GameMath.DEG2RAD)
                .Translate(-(renderInfo.Transform.Origin.X), -(renderInfo.Transform.Origin.Y), -(renderInfo.Transform.Origin.Z))
            ;


            if (isShadowPass)
            {
                rapi.CurrentActiveShader.BindTexture2D("tex2d", renderInfo.TextureId, 0);
                float[] mvpMat = Mat4f.Mul(ItemModelMat.Values, capi.Render.CurrentModelviewMatrix, ItemModelMat.Values);
                Mat4f.Mul(mvpMat, capi.Render.CurrentProjectionMatrix, mvpMat);

                capi.Render.CurrentActiveShader.UniformMatrix("mvpMatrix", mvpMat);
                capi.Render.CurrentActiveShader.Uniform("origin", new Vec3f());
            }
            else
            {
                prog = rapi.StandardShader;
                prog.Use();
                prog.DontWarpVertices = 0;
                prog.AddRenderFlags = 0;
                prog.NormalShaded = 1;
                prog.Tex2D = renderInfo.TextureId;
                prog.RgbaTint = ColorUtil.WhiteArgbVec;
                prog.AlphaTest = renderInfo.AlphaTest;
                prog.DamageEffect = renderInfo.DamageEffect;

                prog.OverlayOpacity = renderInfo.OverlayOpacity;
                if (renderInfo.OverlayTexture != null && renderInfo.OverlayOpacity > 0)
                {
                    prog.Tex2dOverlay2D = renderInfo.OverlayTexture.TextureId;
                    prog.OverlayTextureSize = new Vec2f(renderInfo.OverlayTexture.Width, renderInfo.OverlayTexture.Height);
                    prog.BaseTextureSize = new Vec2f(renderInfo.TextureSize.Width, renderInfo.TextureSize.Height);
                    TextureAtlasPosition texPos = rapi.GetTextureAtlasPosition(stack);
                    prog.BaseUvOrigin = new Vec2f(texPos.x1, texPos.y1);
                }

                
                int temp = (int)stack.Collectible.GetTemperature(capi.World, stack);
                float[] glowColor = ColorUtil.GetIncandescenceColorAsColor4f(temp);
                /*lightrgbs[0] += glowColor[0];
                lightrgbs[1] += glowColor[1];
                lightrgbs[2] += glowColor[2];*/
                
                var gi = GameMath.Clamp((temp - 500) / 3, 0, 255);
                prog.ExtraGlow = gi;
                prog.RgbaAmbientIn = rapi.AmbientColor;
                prog.RgbaLightIn = lightrgbs;
                prog.RgbaGlowIn = new Vec4f(glowColor[0], glowColor[1], glowColor[2], gi / 255f);
                prog.RgbaFogIn = rapi.FogColor;
                prog.FogMinIn = rapi.FogMin;
                prog.FogDensityIn = rapi.FogDensity;
                prog.NormalShaded = renderInfo.NormalShaded ? 1 : 0;

                prog.ProjectionMatrix = rapi.CurrentProjectionMatrix;
                prog.ViewMatrix = rapi.CameraMatrixOriginf;
                prog.ModelMatrix = ItemModelMat.Values;
            }

            
            if (!renderInfo.CullFaces)
            {
                rapi.GlDisableCullFace();
            }

            rapi.RenderMesh(renderInfo.ModelRef);

            if (!renderInfo.CullFaces)
            {
                rapi.GlEnableCullFace();
            }

            if (!isShadowPass)
            {
                prog.DamageEffect = 0;
            }

            if (!isShadowPass)
            {
                prog.Stop();

                float windAffectednessAtPos = Math.Max(0, 1 - capi.World.BlockAccessor.GetDistanceToRainFall(entity.Pos.AsBlockPos) / 5f);


                AdvancedParticleProperties[] ParticleProperties = stack.Collectible?.ParticleProperties;

                if (stack.Collectible != null && !capi.IsGamePaused)
                {
                    Vec4f pos = ItemModelMat.TransformVector(new Vec4f(stack.Collectible.TopMiddlePos.X, stack.Collectible.TopMiddlePos.Y, stack.Collectible.TopMiddlePos.Z, 1));
                    EntityPlayer entityPlayer = capi.World.Player.Entity;
                    accum += dt;
                    if (ParticleProperties != null && ParticleProperties.Length > 0 && accum > 0.05f)
                    {
                        accum = accum % 0.025f;

                        for (int i = 0; i < ParticleProperties.Length; i++)
                        {
                            AdvancedParticleProperties bps = ParticleProperties[i];

                            bps.WindAffectednesAtPos = windAffectednessAtPos;
                            bps.WindAffectednes = windAffectednessAtPos;
                            bps.basePos.X = pos.X + entity.Pos.X + -(entity.Pos.X - entityPlayer.CameraPos.X);
                            bps.basePos.Y = pos.Y + entity.Pos.Y + -(entity.Pos.Y - entityPlayer.CameraPos.Y);
                            bps.basePos.Z = pos.Z + entity.Pos.Z + -(entity.Pos.Z - entityPlayer.CameraPos.Z);

                            eagent.World.SpawnParticles(bps);
                        }
                    }
                }

            }

        }

        public override void RenderToGui(float dt, double posX, double posY, double posZ, float yawDelta, float size)
        {
            if (gearInv == null && eagent?.GearInventory != null)
            {
                registerSlotModified();
            }

            loadModelMatrixForGui(entity, posX, posY, posZ, yawDelta, size);

            if (meshRefOpaque != null)
            {
                capi.Render.CurrentActiveShader.UniformMatrix("projectionMatrix", capi.Render.CurrentProjectionMatrix);
                capi.Render.CurrentActiveShader.UniformMatrix("modelViewMatrix", Mat4f.Mul(ModelMat, capi.Render.CurrentModelviewMatrix, ModelMat));
                capi.Render.RenderMesh(meshRefOpaque);
            }

            if (!shapeFresh)
            {
                TesselateShape();
            }
        }

        protected void registerSlotModified(bool callModified = true)
        {
            eagent.GearInventory.SlotModified += gearSlotModified;
            gearInv = eagent.GearInventory;

            if (entity is EntityPlayer eplr)
            {
                IInventory inv = eplr.Player?.InventoryManager.GetOwnInventory(GlobalConstants.backpackInvClassName);
                if (inv != null) inv.SlotModified += backPackSlotModified;
            }

            if (callModified)
            {
                MarkShapeModified();
            }
        }

        protected void backPackSlotModified(int slotId)
        {
            MarkShapeModified();
        }

        protected void gearSlotModified(int slotid)
        {
            MarkShapeModified();
        }

        public virtual void reloadSkin()
        {

        }


        public override void DoRender3DOpaqueBatched(float dt, bool isShadowPass)
        {
            if (isSpectator || meshRefOpaque == null) return;

            if (isShadowPass)
            {
                Mat4f.Mul(tmpMvMat, capi.Render.CurrentModelviewMatrix, ModelMat);
                capi.Render.CurrentActiveShader.UniformMatrix("modelViewMatrix", tmpMvMat);
            }
            else
            {
                frostAlpha += (targetFrostAlpha - frostAlpha) * dt / 2f;
                float fa = (float)Math.Round(GameMath.Clamp(frostAlpha, 0, 1), 4);

                capi.Render.CurrentActiveShader.Uniform("rgbaLightIn", lightrgbs);
                capi.Render.CurrentActiveShader.Uniform("extraGlow", entity.Properties.Client.GlowLevel);
                capi.Render.CurrentActiveShader.UniformMatrix("modelMatrix", ModelMat);
                capi.Render.CurrentActiveShader.UniformMatrix("viewMatrix", capi.Render.CurrentModelviewMatrix);
                capi.Render.CurrentActiveShader.Uniform("addRenderFlags", AddRenderFlags);
                capi.Render.CurrentActiveShader.Uniform("windWaveIntensity", (float)WindWaveIntensity);
                capi.Render.CurrentActiveShader.Uniform("skipRenderJointId", skipRenderJointId);
                capi.Render.CurrentActiveShader.Uniform("skipRenderJointId2", skipRenderJointId2);
                capi.Render.CurrentActiveShader.Uniform("entityId", (int)entity.EntityId);
                capi.Render.CurrentActiveShader.Uniform("glitchFlicker", glitchFlicker ? 1 : 0);
                capi.Render.CurrentActiveShader.Uniform("frostAlpha", fa);
                capi.Render.CurrentActiveShader.Uniform("waterWaveCounter", capi.Render.ShaderUniforms.WaterWaveCounter);

                color[0] = (entity.RenderColor >> 16 & 0xff) / 255f;
                color[1] = ((entity.RenderColor >> 8) & 0xff) / 255f;
                color[2] = ((entity.RenderColor >> 0) & 0xff) / 255f;
                color[3] = ((entity.RenderColor >> 24) & 0xff) / 255f;

                capi.Render.CurrentActiveShader.Uniform("renderColor", color);

                double stab = entity.WatchedAttributes.GetDouble("temporalStability", 1);
                double plrStab = capi.World.Player.Entity.WatchedAttributes.GetDouble("temporalStability", 1);
                double stabMin = Math.Min(stab, plrStab);

                float strength = (float)(glitchAffected ? Math.Max(0, 1 - 1 / 0.4f * stabMin) : 0);
                capi.Render.CurrentActiveShader.Uniform("glitchEffectStrength", strength);
            }


            capi.Render.CurrentActiveShader.UniformMatrices4x3(
                "elementTransforms", 
                GlobalConstants.MaxAnimatedElements, 
                entity.AnimManager.Animator.Matrices4x3
            );

            if (meshRefOpaque != null)
            {
                capi.Render.RenderMesh(meshRefOpaque);
            }
        }

        public override void DoRender2D(float dt)
        {
            if (isSpectator || (nameTagTexture == null && debugTagTexture == null)) return;
            if ((entity as EntityPlayer)?.ServerControls.Sneak == true && debugTagTexture==null) return;

            IRenderAPI rapi = capi.Render;
            EntityPlayer entityPlayer = capi.World.Player.Entity;
            Vec3d aboveHeadPos;

            if (capi.World.Player.Entity.EntityId == entity.EntityId) {
                if (rapi.CameraType == EnumCameraMode.FirstPerson) return;
                aboveHeadPos = new Vec3d(entityPlayer.CameraPos.X + entityPlayer.LocalEyePos.X, entityPlayer.CameraPos.Y + 0.4 + entityPlayer.LocalEyePos.Y, entityPlayer.CameraPos.Z + entityPlayer.LocalEyePos.Z);
            } else
            {
                var thisMount = (entity as EntityAgent)?.MountedOn;
                var selfMount = entityPlayer.MountedOn;

                if (thisMount?.MountSupplier != null && thisMount.MountSupplier == selfMount?.MountSupplier)
                {
                    var mpos = thisMount.MountSupplier.GetMountOffset(entity);

                    aboveHeadPos = new Vec3d(entityPlayer.CameraPos.X + entityPlayer.LocalEyePos.X, entityPlayer.CameraPos.Y + 0.4 + entityPlayer.LocalEyePos.Y, entityPlayer.CameraPos.Z + entityPlayer.LocalEyePos.Z);
                    aboveHeadPos.Add(mpos);
                } else
                {
                    aboveHeadPos = new Vec3d(entity.Pos.X, entity.Pos.Y + entity.SelectionBox.Y2 + 0.2, entity.Pos.Z);
                }
                
            }

            double offX = entity.SelectionBox.X2 - entity.OriginSelectionBox.X2;
            double offZ = entity.SelectionBox.Z2 - entity.OriginSelectionBox.Z2;
            aboveHeadPos.Add(offX, 0, offZ);


            Vec3d pos = MatrixToolsd.Project(aboveHeadPos, rapi.PerspectiveProjectionMat, rapi.PerspectiveViewMat, rapi.FrameWidth, rapi.FrameHeight);

            // Z negative seems to indicate that the name tag is behind us \o/
            if (pos.Z < 0) return;

            float scale = 4f / Math.Max(1, (float)pos.Z);

            float cappedScale = Math.Min(1f, scale);
            if (cappedScale > 0.75f) cappedScale = 0.75f + (cappedScale - 0.75f) / 2;

            float offY = 0;

            double dist = entityPlayer.Pos.SquareDistanceTo(entity.Pos);
            if (nameTagTexture != null && (!showNameTagOnlyWhenTargeted || capi.World.Player.CurrentEntitySelection?.Entity == entity) && renderRange * renderRange > dist)
            {
                float posx = (float)pos.X - cappedScale * nameTagTexture.Width / 2;
                float posy = rapi.FrameHeight - (float)pos.Y - (nameTagTexture.Height * Math.Max(0, cappedScale));

                rapi.Render2DTexture(
                    nameTagTexture.TextureId, posx, posy, cappedScale * nameTagTexture.Width, cappedScale * nameTagTexture.Height, 20
                );

                offY += nameTagTexture.Height;
            }

            if (debugTagTexture != null)
            {
                float posx = (float)pos.X - cappedScale * debugTagTexture.Width / 2;
                float posy = rapi.FrameHeight - (float)pos.Y - (offY + debugTagTexture.Height) * Math.Max(0, cappedScale);

                rapi.Render2DTexture(
                    debugTagTexture.TextureId, posx, posy - offY, cappedScale * debugTagTexture.Width, cappedScale * debugTagTexture.Height, 20
                );
            }

            if (messageTextures != null)
            {
                offY += 0;

                foreach (MessageTexture mt in messageTextures)
                {
                    offY += (mt.tex.Height * cappedScale) + 4;

                    float posx = (float)pos.X - cappedScale * mt.tex.Width / 2;
                    float posy = (float)pos.Y + offY;
                    

                    rapi.Render2DTexture(
                        mt.tex.TextureId, posx, rapi.FrameHeight - posy, cappedScale * mt.tex.Width, cappedScale * mt.tex.Height, 20
                    );
                }
            }
        }


        double stepPitch;
        double prevY;
        double prevYAccum;
        public float xangle = 0, yangle = 0, zangle = 0;

        public void loadModelMatrix(Entity entity, float dt, bool isShadowPass)
        {
            EntityPlayer entityPlayer = capi.World.Player.Entity;

            Mat4f.Identity(ModelMat);

            if (entity is IMountableSupplier ims && ims.IsMountedBy(entityPlayer))
            {
                var mountoffset = ims.GetMountOffset(entityPlayer);
                Mat4f.Translate(ModelMat, ModelMat, -mountoffset.X, -mountoffset.Y, -mountoffset.Z);
            }
            else
            {
                Mat4f.Translate(ModelMat, ModelMat, (float)(entity.Pos.X - entityPlayer.CameraPos.X), (float)(entity.Pos.Y - entityPlayer.CameraPos.Y), (float)(entity.Pos.Z - entityPlayer.CameraPos.Z));
            }

            float rotX = entity.Properties.Client.Shape?.rotateX ?? 0;
            float rotY = entity.Properties.Client.Shape?.rotateY ?? 0;
            float rotZ = entity.Properties.Client.Shape?.rotateZ ?? 0;

            Mat4f.Translate(ModelMat, ModelMat, 0, entity.SelectionBox.Y2 / 2, 0);

            if (!isShadowPass)
            {
                updateStepPitch(dt);
            }

            double[] quat = Quaterniond.Create();
            float bodyPitch = entity is EntityPlayer ? 0 : entity.Pos.Pitch;
            float yaw = entity.Pos.Yaw + (rotY + 90) * GameMath.DEG2RAD;

            BlockFacing climbonfacing = entity.ClimbingOnFace;

            // To fix climbing locust rotation weirdnes on east and west faces. Brute forced fix. There's probably a correct solution to this.
            bool fuglyHack = entity.Properties.RotateModelOnClimb && entity.ClimbingOnFace?.Axis == EnumAxis.X;
            float sign = -1;

            Quaterniond.RotateX(quat, quat, bodyPitch + rotX * GameMath.DEG2RAD + (fuglyHack ? yaw * sign : 0) + xangle);
            Quaterniond.RotateY(quat, quat, (fuglyHack ? 0 : yaw) + yangle);
            Quaterniond.RotateZ(quat, quat, entity.Pos.Roll + stepPitch + rotZ * GameMath.DEG2RAD + (fuglyHack ? GameMath.PIHALF * (climbonfacing == BlockFacing.WEST ? -1 : 1) : 0) + zangle);
            
            float[] qf = new float[quat.Length];
            for (int i = 0; i < quat.Length; i++) qf[i] = (float)quat[i];
            Mat4f.Mul(ModelMat, ModelMat, Mat4f.FromQuat(Mat4f.Create(), qf));
            Mat4f.RotateX(ModelMat, ModelMat, sidewaysSwivelAngle);

            float scale = entity.Properties.Client.Size;
            Mat4f.Translate(ModelMat, ModelMat, 0, -entity.SelectionBox.Y2 / 2, 0f);
            Mat4f.Scale(ModelMat, ModelMat, new float[] { scale, scale, scale });
            Mat4f.Translate(ModelMat, ModelMat, -0.5f, 0, -0.5f);
        }


        public void loadModelMatrixForPlayer(Entity entity, bool isSelf, float dt)
        {
            EntityPlayer entityPlayer = capi.World.Player.Entity;
            EntityPlayer eplr = entity as EntityPlayer;

            Mat4f.Identity(ModelMat);

            if (!isSelf)
            {
                // We use special positioning code for mounted entities that are on the same mount as we are.
                // While this should not be necesssary, because the client side physics does set the entity position accordingly, it does same to create 1-frame jitter if we dont specially handle this
                var selfMountedOn = entityPlayer.MountedOn?.MountSupplier;
                var heMountedOn = (entity as EntityAgent).MountedOn?.MountSupplier;
                if (selfMountedOn != null && selfMountedOn == heMountedOn)
                {
                    var selfmountoffset = selfMountedOn.GetMountOffset(entityPlayer);
                    var hemountoffset = heMountedOn.GetMountOffset(entity);
                    Mat4f.Translate(ModelMat, ModelMat, -selfmountoffset.X + hemountoffset.X, -selfmountoffset.Y + hemountoffset.Y, -selfmountoffset.Z + hemountoffset.Z);
                } else
                {
                    Mat4f.Translate(ModelMat, ModelMat, (float)(entity.Pos.X - entityPlayer.CameraPos.X), (float)(entity.Pos.Y - entityPlayer.CameraPos.Y), (float)(entity.Pos.Z - entityPlayer.CameraPos.Z));
                }

                
            }

            float rotX = entity.Properties.Client.Shape != null ? entity.Properties.Client.Shape.rotateX : 0;
            float rotY = entity.Properties.Client.Shape != null ? entity.Properties.Client.Shape.rotateY : 0;
            float rotZ = entity.Properties.Client.Shape != null ? entity.Properties.Client.Shape.rotateZ : 0;
            float bodyYaw = 0;

            if (eagent != null)
            {
                float yawDist = GameMath.AngleRadDistance(bodyYawLerped, eagent.BodyYaw);
                bodyYawLerped += GameMath.Clamp(yawDist, -dt * 8, dt * 8);
                bodyYaw = bodyYawLerped;
            }

            //Mat4f.Translate(ModelMat, ModelMat, 0, -entity.SelectionBox.Y2 / 2, 0);

            float bodyPitch = eplr == null ? 0 : eplr.WalkPitch;
            Mat4f.RotateX(ModelMat, ModelMat, entity.Pos.Roll + rotX * GameMath.DEG2RAD);
            Mat4f.RotateY(ModelMat, ModelMat, bodyYaw + (180 + rotY) * GameMath.DEG2RAD);
            Mat4f.RotateZ(ModelMat, ModelMat, bodyPitch + rotZ * GameMath.DEG2RAD);

            Mat4f.RotateX(ModelMat, ModelMat, sidewaysSwivelAngle);

            if (entityPlayer != null)
            {
                float targetIntensity = entity.WatchedAttributes.GetFloat("intoxication");
                intoxIntensity += (targetIntensity - intoxIntensity) * dt / 3;
                capi.Render.PerceptionEffects.ApplyToTpPlayer(entityPlayer, ModelMat, intoxIntensity);
            }

            float scale = entity.Properties.Client.Size;
            //Mat4f.Translate(ModelMat, ModelMat, 0, entity.SelectionBox.Y2 / 2, 0); - WTF is this for? It breaks drunken effects and boat rocking rotations

            Mat4f.Scale(ModelMat, ModelMat, new float[] { scale, scale, scale });

            Mat4f.Translate(ModelMat, ModelMat, -0.5f, 0, -0.5f);
        }


        protected void loadModelMatrixForGui(Entity entity, double posX, double posY, double posZ, double yawDelta, float size)
        {
            Mat4f.Identity(ModelMat);
            Mat4f.Translate(ModelMat, ModelMat, (float)posX, (float)posY, (float)posZ);

            Mat4f.Translate(ModelMat, ModelMat, size, 2 * size, 0);

            float rotX = entity.Properties.Client.Shape != null ? entity.Properties.Client.Shape.rotateX : 0;
            float rotY = entity.Properties.Client.Shape != null ? entity.Properties.Client.Shape.rotateY : 0;
            float rotZ = entity.Properties.Client.Shape != null ? entity.Properties.Client.Shape.rotateZ : 0;

            Mat4f.RotateX(ModelMat, ModelMat, GameMath.PI + rotX * GameMath.DEG2RAD);
            Mat4f.RotateY(ModelMat, ModelMat, (float)yawDelta + rotY * GameMath.DEG2RAD);
            Mat4f.RotateZ(ModelMat, ModelMat, rotZ * GameMath.DEG2RAD);

            float scale = entity.Properties.Client.Size * size;
            Mat4f.Scale(ModelMat, ModelMat, new float[] { scale, scale, scale });
            Mat4f.Translate(ModelMat, ModelMat, -0.5f, 0f, -0.5f);
        }



        #region Sideways swivel when changing the movement direction

        float stepingAccum = 0f;
        float fallingAccum = 0f;

        private void updateStepPitch(float dt)
        {
            prevYAccum += dt;
            if (prevYAccum > 1f / 5f)
            {
                prevYAccum = 0;
                prevY = entity.Pos.Y;
            }

            if (eagent?.Alive == false)
            {
                stepPitch = Math.Max(0, stepPitch - 2 * dt);
            }

            if (eagent == null || entity.Properties.CanClimbAnywhere || !eagent.Alive || entity.Attributes.GetInt("dmgkb", 0) != 0 || !entity.Properties.Client.PitchStep) return;


            if (entity.Properties.Habitat == EnumHabitat.Air || eagent.Controls.IsClimbing)
            {
                stepPitch = GameMath.Clamp(entity.Pos.Y - prevY + 0.1, 0, 0.3) - GameMath.Clamp(prevY - entity.Pos.Y - 0.1, 0, 0.3);
                return;
            }

            double deltaY = entity.Pos.Y - prevY;

            bool steppingUp = deltaY > 0.02 && !entity.FeetInLiquid && !entity.Swimming && !entity.OnGround;
            bool falling = deltaY < 0 && !entity.OnGround && !entity.FeetInLiquid && !entity.Swimming;

            double targetPitch = 0;

            stepingAccum = Math.Max(0f, stepingAccum - dt);
            fallingAccum = Math.Max(0f, fallingAccum - dt);

            if (steppingUp) stepingAccum = 0.2f;
            if (falling) fallingAccum = 0.2f;

            if (stepingAccum > 0) targetPitch = -0.5;
            else if (fallingAccum > 0) targetPitch = 0.5;

            stepPitch += (targetPitch - stepPitch) * dt * 5;
        }


        public float sidewaysSwivelAngle = 0;
        double prevAngleSwing;
        double prevPosXSwing;
        double prevPosZSwing;

        void calcSidewaysSwivelForPlayer(float dt)
        {
            double nowAngle = Math.Atan2(entity.Pos.Motion.Z, entity.Pos.Motion.X);
            double walkspeedsq = entity.Pos.Motion.LengthSq();
            
            if (walkspeedsq > 0.001 && entity.OnGround)
            {
                float angledist = GameMath.AngleRadDistance((float)prevAngleSwing, (float)nowAngle);
                sidewaysSwivelAngle -= GameMath.Clamp(angledist, -0.05f, 0.05f) * dt * 40 * (float)Math.Min(0.025f, walkspeedsq) *  80 * (eagent?.Controls.Backward == true ? -1 : 1);
                sidewaysSwivelAngle = GameMath.Clamp(sidewaysSwivelAngle, -0.3f, 0.3f);
            }

            sidewaysSwivelAngle *= Math.Min(0.99f, 1 - 0.1f * dt * 60f);
            prevAngleSwing = nowAngle;

            (entity as EntityPlayer).sidewaysSwivelAngle = sidewaysSwivelAngle;
        }

        void calcSidewaysSwivelForEntity(float dt)
        {
            double dx = entity.Pos.X - prevPosXSwing;
            double dz = entity.Pos.Z - prevPosZSwing;
            double nowAngle = Math.Atan2(dz, dx);

            if (dx * dx + dz * dz > 0.001 && entity.OnGround)
            {
                float angledist = GameMath.AngleRadDistance((float)prevAngleSwing, (float)nowAngle);
                sidewaysSwivelAngle -= GameMath.Clamp(angledist, -0.05f, 0.05f) * dt * 40; // * (eagent?.Controls.Backward == true ? 1 : -1);
                sidewaysSwivelAngle = GameMath.Clamp(sidewaysSwivelAngle, -0.3f, 0.3f);
            }

            sidewaysSwivelAngle *= Math.Min(0.99f, 1 - 0.1f * dt * 60f);

            prevAngleSwing = nowAngle;

            prevPosXSwing = entity.Pos.X;
            prevPosZSwing = entity.Pos.Z;
        }

        #endregion



        public override void Dispose()
        {
            if (meshRefOpaque != null)
            {
                meshRefOpaque.Dispose();
                meshRefOpaque = null;
            }

            if (nameTagTexture != null)
            {
                nameTagTexture.Dispose();
                nameTagTexture = null;
            }

            if (debugTagTexture != null)
            {
                debugTagTexture.Dispose();
                debugTagTexture = null;
            }

            capi.Event.ReloadShapes -= MarkShapeModified;

            if (DisplayChatMessages)
            {
                capi.Event.ChatMessage -= OnChatMessage;
            }


            if (eagent?.GearInventory != null)
            {
                eagent.GearInventory.SlotModified -= gearSlotModified;
            }

            if (entity is EntityPlayer eplr)
            {
                IInventory inv = eplr.Player?.InventoryManager.GetOwnInventory(GlobalConstants.backpackInvClassName);
                if (inv != null) inv.SlotModified -= backPackSlotModified;
            }

        }


    }
}
