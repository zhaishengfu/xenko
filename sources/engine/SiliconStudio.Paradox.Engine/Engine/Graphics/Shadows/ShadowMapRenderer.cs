// Copyright (c) 2014 Silicon Studio Corp. (http://siliconstudio.co.jp)
// This file is distributed under GPL v3. See LICENSE.md for details.

using System;
using System.Collections.Generic;

using SiliconStudio.Core;
using SiliconStudio.Core.Collections;
using SiliconStudio.Core.Extensions;
using SiliconStudio.Core.Mathematics;
using SiliconStudio.Paradox.Effects.Lights;
using SiliconStudio.Paradox.Engine;
using SiliconStudio.Paradox.Engine.Graphics;
using SiliconStudio.Paradox.Engine.Graphics.Composers;
using SiliconStudio.Paradox.EntityModel;
using SiliconStudio.Paradox.Graphics;

namespace SiliconStudio.Paradox.Effects.Shadows
{
    /// <summary>
    /// Handles rendering of shadow map casters.
    /// </summary>
    public class ShadowMapRenderer : EntityComponentRendererCoreBase
    {
        // TODO: Extract a common interface and implem for shadow renderer (not only shadow maps)

        private FastListStruct<ShadowMapAtlasTexture> atlases;

        private PoolListStruct<LightShadowMapTexture> shadowMapTextures;

        private readonly int MaximumTextureSize = (int)(MaximumShadowSize * ComputeSizeFactor(LightShadowImportance.High, LightShadowMapSize.Large) * 2.0f);

        private readonly static PropertyKey<ShadowMapRenderer> Current = new PropertyKey<ShadowMapRenderer>("ShadowMapRenderer.Current", typeof(ShadowMapRenderer));

        private const float MaximumShadowSize = 1024;

        internal static readonly ParameterKey<ShadowMapReceiverInfo[]> Receivers = ParameterKeys.New(new ShadowMapReceiverInfo[1]);
        internal static readonly ParameterKey<ShadowMapReceiverVsmInfo[]> ReceiversVsm = ParameterKeys.New(new ShadowMapReceiverVsmInfo[1]);
        internal static readonly ParameterKey<ShadowMapCascadeLevel[]> LevelReceivers = ParameterKeys.New(new ShadowMapCascadeLevel[1]);
        internal static readonly ParameterKey<int> ShadowMapLightCount = ParameterKeys.New(0);
        
        // rectangles to blur for each shadow map
        private HashSet<LightShadowMapTexture> shadowMapTexturesToBlur = new HashSet<LightShadowMapTexture>();

        private readonly Entity cameraEntity;

        private readonly ModelComponentRenderer modelRenderer;

        private readonly string effectName;

        private readonly ModelComponentRenderer shadowModelComponentRenderer;

        private readonly RenderItemCollection opaqueRenderItems;

        private readonly RenderItemCollection transparentRenderItems;

        private readonly Dictionary<Type, ILightShadowMapRenderer> renderers;

        private readonly ParameterCollection shadowParameters;

        public readonly Dictionary<LightComponent, LightShadowMapTexture> LightComponentsWithShadows;

        private readonly Dictionary<ShaderGroupDataKey, ILightShadowMapShaderGroupData> shaderGroupDatas;

        private List<LightComponent> visibleLights;

        public ShadowMapRenderer(string effectName)
        {
            if (effectName == null) throw new ArgumentNullException("effectName");
            this.effectName = effectName;
            atlases = new FastListStruct<ShadowMapAtlasTexture>(16);
            shadowMapTextures = new PoolListStruct<LightShadowMapTexture>(16, CreateLightShadowMapTexture);
            LightComponentsWithShadows = new Dictionary<LightComponent, LightShadowMapTexture>(16);

            opaqueRenderItems = new RenderItemCollection(512, false);
            transparentRenderItems = new RenderItemCollection(512, true);

            renderers = new Dictionary<Type, ILightShadowMapRenderer>();

            ShadowCamera = new CameraComponent { UseCustomViewMatrix = true, UseCustomProjectionMatrix = true };

            // Creates a model renderer for the shadows casters
            shadowModelComponentRenderer = new ModelComponentRenderer(effectName + ".ShadowMapCaster")
            {
                Callbacks = 
                {
                    UpdateMeshes = FilterCasters,
                }
            };

            shadowParameters = new ParameterCollection();
        }

        /// <summary>
        /// Gets or sets the camera.
        /// </summary>
        /// <value>The camera.</value>
        public CameraComponent Camera { get; private set; }

        /// <summary>
        /// The shadow camera used for rendering from the shadow space.
        /// </summary>
        public readonly CameraComponent ShadowCamera;

        public ILightShadowMapRenderer FindRenderer(Type lightType)
        {
            ILightShadowMapRenderer shadowMapRenderer;
            renderers.TryGetValue(lightType, out shadowMapRenderer);
            return shadowMapRenderer;
        }

        public void Attach(ModelComponentRenderer modelRenderer)
        {
            // TODO: Add logic to plug shadow mapping into 

        }

        public void Draw(RenderContext context, List<LightComponent> visibleLights)
        {
            var current = context.Tags.Get(Current);
            if (current != null)
            {
                return;
            }

            this.visibleLights = visibleLights;

            LightComponentsWithShadows.Clear();
            using (var t1 = context.PushTagAndRestore(Current, this))
            {
                PreDrawCoreInternal(context);
                DrawCore(context);
                PostDrawCoreInternal(context);
            }
        }

        public void RenderCasters(RenderContext context, EntityGroupMask cullingMask)
        {
            context.PushParameters(shadowParameters);

            CameraComponentRenderer.UpdateParameters(context, ShadowCamera);

            opaqueRenderItems.Clear();
            transparentRenderItems.Clear();
            shadowModelComponentRenderer.CurrentCullingMask = cullingMask;
            shadowModelComponentRenderer.Prepare(context, opaqueRenderItems, transparentRenderItems);
            shadowModelComponentRenderer.Draw(context, opaqueRenderItems, 0, opaqueRenderItems.Count-1);

            context.PopParameters();
        }

        protected void DrawCore(RenderContext context)
        {
            // We must be running inside the context of 
            var sceneInstance = SceneInstance.GetCurrent(context);
            if (sceneInstance == null)
            {
                throw new InvalidOperationException("ShadowMapRenderer expects to be used inside the context of a SceneInstance.Draw()");
            }
            
            // Gets the current camera
            Camera = context.GetCurrentCamera();
            if (Camera == null)
            {
                return;
            }

            // Collect all required shadow maps
            shadowMapTextures.Clear();
            CollectShadowMaps();

            // No shadow maps to render
            if (shadowMapTextures.Count == 0)
            {
                return;
            }

            // Assign rectangles to shadow maps
            AssignRectangles();

            // Reset the state of renderers
            foreach (var rendererKeyPairs in renderers)
            {
                var renderer = rendererKeyPairs.Value;
                renderer.Reset();
            }

            // Prepare and render shadow maps
            foreach (var shadowMapTexture in shadowMapTextures)
            {
                shadowMapTexture.Renderer.Render(context, this, shadowMapTexture);
            }
        }

        private void AssignRectangles()
        {
            // Clear atlases
            foreach (var atlas in atlases)
            {
                atlas.Clear();
            }

            // Assign rectangles for shadowmaps
            foreach (var shadowMapTexture in shadowMapTextures)
            {
                AssignRectangles(shadowMapTexture);
            }
        }

        private void AssignRectangles(LightShadowMapTexture lightShadowMapTexture)
        {
            // TODO: This is not good to have to detect the light type here
            lightShadowMapTexture.CascadeCount = lightShadowMapTexture.Light is LightDirectional ? (int)lightShadowMapTexture.Shadow.CascadeCount : 1;

            var size = lightShadowMapTexture.Size;

            // Try to fit the shadow map into an existing atlas
            foreach (var atlas in atlases)
            {
                if (atlas.TryInsert(size, size, lightShadowMapTexture.CascadeCount))
                {
                    AssignRectangles(ref lightShadowMapTexture, atlas);
                    return;
                }
            }

            // TODO: handle FilterType texture creation here
            // TODO: This does not work for Omni lights
            
            // Allocate a new atlas texture
            var texture = Texture.New2D(Context.GraphicsDevice, MaximumTextureSize, MaximumTextureSize, 1, PixelFormat.D32_Float, TextureFlags.DepthStencil | TextureFlags.ShaderResource);
            var newAtlas = new ShadowMapAtlasTexture(texture) { FilterType = lightShadowMapTexture.FilterType };
            AssignRectangles(ref lightShadowMapTexture, newAtlas);
            atlases.Add(newAtlas);
        }

        private void AssignRectangles(ref LightShadowMapTexture lightShadowMapTexture, ShadowMapAtlasTexture atlas)
        {
            // Make sure the atlas cleared (will be clear just once)
            atlas.ClearRenderTarget(Context);

            var size = lightShadowMapTexture.Size;
            for (int i = 0; i < lightShadowMapTexture.CascadeCount; i++)
            {
                var rect = Rectangle.Empty;
                atlas.Insert(size, size, ref rect);
                lightShadowMapTexture.SetRectangle(i, rect);
                lightShadowMapTexture.Atlas = atlas;
            }
        }

        private void CollectShadowMaps()
        {
            foreach (var lightComponent in visibleLights)
            {
                var light = lightComponent.Type as IDirectLight;
                if (light == null)
                {
                    continue;
                }

                var shadowMap = light.Shadow;
                if (shadowMap == null || !shadowMap.Enabled)
                {
                    continue;
                }

                // Check if the light has a shadow map renderer
                var lightType = light.GetType();
                ILightShadowMapRenderer renderer;
                if (!renderers.TryGetValue(lightType, out renderer))
                {
                    // Create renderers just once per ShadowMapRenderer instance
                    renderer = shadowMap.CreateRenderer(light);
                    renderers[lightType] = renderer;
                }

                // If no shadow map renderer, skip it.
                if (renderer == null)
                {
                    continue;
                }

                var direction = lightComponent.Direction;
                var position = lightComponent.Position;

                // Compute the coverage of this light on the screen
                var size = light.ComputeScreenCoverage(Context, position, direction);

                // Converts the importance into a shadow size factor
                var sizeFactor = ComputeSizeFactor(shadowMap.Importance, shadowMap.Size);
                    
                // Compute the size of the final shadow map
                // TODO: Handle GraphicsProfile
                var shadowMapSize = (int)Math.Min(MaximumShadowSize * sizeFactor, MathUtil.NextPowerOfTwo(size * sizeFactor));

                if (shadowMapSize <= 0) // TODO: Validate < 0 earlier in the setters
                {
                    continue;
                }

                // Get or allocate  a ShadowMapTexture
                var shadowMapTexture = shadowMapTextures.Add();
                shadowMapTexture.Initialize(lightComponent, light, shadowMap, shadowMapSize, renderer);
                LightComponentsWithShadows.Add(lightComponent, shadowMapTexture);
            }
        }

        private static float ComputeSizeFactor(LightShadowImportance importance, LightShadowMapSize shadowMapSize)
        {
            // Calculate a basic factor from the importance of this shadow map
            var factor = importance == LightShadowImportance.High ? 2.0f : importance == LightShadowImportance.Medium ? 1.0f : 0.5f;

            // Then reduce the size based on the shadow map size
            factor *= (float)Math.Pow(2.0f, (int)shadowMapSize - 2.0f);
            return factor;
        }

        private static void FilterCasters(RenderContext context, FastList<RenderMesh> meshes)
        {
            for (int i = 0; i < meshes.Count; i++)
            {
                var mesh = meshes[i];
                // If there is no caster
                if (!mesh.MaterialInstance.IsShadowCaster || !mesh.RenderModel.ModelComponent.IsShadowCaster)
                {
                    meshes.SwapRemoveAt(i--);
                }

                var extension = mesh.Parameters.Get(ParadoxEffectBaseKeys.ExtensionPostVertexStageShader);
                const string ShadowMapCasterExtension = "ShadowMapCaster";
                if (extension != ShadowMapCasterExtension)
                {
                    mesh.Parameters.Set(ParadoxEffectBaseKeys.ExtensionPostVertexStageShader, ShadowMapCasterExtension);
                }
            }
        }

        private static LightShadowMapTexture CreateLightShadowMapTexture()
        {
            return new LightShadowMapTexture();
        }

        struct ShaderGroupDataKey : IEquatable<ShaderGroupDataKey>
        {
            public readonly Texture Texture;

            public readonly ILightShadowMapRenderer Renderer;

            public readonly int CascadeCount;

            public readonly int LightMaxCount;

            public ShaderGroupDataKey(Texture texture, ILightShadowMapRenderer renderer, int cascadeCount, int lightMaxCount)
            {
                Texture = texture;
                Renderer = renderer;
                CascadeCount = cascadeCount;
                LightMaxCount = lightMaxCount;
            }

            public bool Equals(ShaderGroupDataKey other)
            {
                return Texture.Equals(other.Texture) && Renderer.Equals(other.Renderer) && CascadeCount == other.CascadeCount && LightMaxCount == other.LightMaxCount;
            }

            public override bool Equals(object obj)
            {
                if (ReferenceEquals(null, obj)) return false;
                return obj is ShaderGroupDataKey && Equals((ShaderGroupDataKey)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hashCode = Texture.GetHashCode();
                    hashCode = (hashCode * 397) ^ Renderer.GetHashCode();
                    hashCode = (hashCode * 397) ^ CascadeCount;
                    hashCode = (hashCode * 397) ^ LightMaxCount;
                    return hashCode;
                }
            }

            public static bool operator ==(ShaderGroupDataKey left, ShaderGroupDataKey right)
            {
                return left.Equals(right);
            }

            public static bool operator !=(ShaderGroupDataKey left, ShaderGroupDataKey right)
            {
                return !left.Equals(right);
            }
        }
    }
}