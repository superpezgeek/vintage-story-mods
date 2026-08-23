using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace TheUnknowing
{
    // Registered in place of the stock "Shape" renderer for theunknowing:theunknowing. Mesh
    // upload and the spawn/widen keyframe animations are still handled entirely by the base
    // EntityShapeRenderer - the only thing this class adds is a continuously scrolling starfield
    // texture, which the shape/keyframe animation system has no hook for (it only moves vertices,
    // never UVs). See NOTES.local.md for the full implementation story (OIT pitfall,
    // jointMatrixOffset derivation, the ReloadShader crash) - only the load-bearing gotchas are
    // kept here.
    public class TheUnknowingRenderer : EntityShapeRenderer
    {
        private const string ShaderName = "theunknowing";
        private const float ScrollPeriodSeconds = 5.0f;

        private static IShaderProgram? shaderProgram;
        private static bool reloadHookRegistered;

        // False if the custom shader failed to compile (its fsh #includes stock "game"-domain
        // files from our own "theunknowing" domain - not guaranteed to resolve) - every override
        // below falls back to the base class's stock rendering in that case.
        private static bool shaderReady;

        private float scrollProgress;

        // Folds the "column" element's animated transform (spawn/widen's stretch keyframes) into
        // modelMatrix ourselves, since our custom shader replaces the stock one's Animation UBO
        // lookup entirely (see theunknowing.vsh).
        private readonly float[] jointMatScratch = new float[16];
        private readonly float[] combinedModelMat = Mat4f.Create();
        private int jointMatrixOffset = -1;

        private void EnsureJointMatrixOffsetResolved()
        {
            if (jointMatrixOffset >= 0) return;

            Vintagestory.API.Common.Shape? shape = entity.Properties.Client.LoadedShapeForEntity;
            Vintagestory.API.Common.ShapeElement? columnElement = shape?.GetElementByName("column");
            jointMatrixOffset = (columnElement?.JointId ?? 0) * 16;
        }

        public TheUnknowingRenderer(Entity entity, ICoreClientAPI api) : base(entity, api)
        {
            if (!reloadHookRegistered)
            {
                reloadHookRegistered = true;

                // The engine disposes/recompiles every registered shader program on a graphics
                // settings change - without this, the static shaderProgram reference goes stale
                // and the next draw throws "Can't use a disposed shader!" (confirmed via a client
                // crash, 2026-08-23).
                api.Event.ReloadShader += () =>
                {
                    shaderProgram = LoadShader(api);
                    return shaderReady;
                };
            }

            shaderProgram ??= LoadShader(api);
        }

        private static IShaderProgram LoadShader(ICoreClientAPI api)
        {
            IShaderProgram prog = api.Shader.NewShaderProgram();
            prog.AssetDomain = "theunknowing";
            prog.VertexShader = api.Shader.NewShader(EnumShaderType.VertexShader);
            prog.FragmentShader = api.Shader.NewShader(EnumShaderType.FragmentShader);
            // Must be explicit: entityanimated compiles in both an opaque and an OIT
            // (order-independent-transparency) variant, and this batched pass is the opaque one.
            // Left unset, we'd write into the OIT target layout while bound to the opaque pass -
            // depth writes fine, but the real color attachment never gets our texColor.
            prog.Oit = false;
            api.Shader.RegisterFileShaderProgram(ShaderName, prog);
            shaderReady = prog.Compile();
            if (!shaderReady)
            {
                api.Logger.Error("[TheUnknowing] {0} shader failed to compile - the storm cloud entity will render without the UV scroll (stock shader fallback).", ShaderName);
            }
            return prog;
        }

        public override void DoRender3DOpaqueBatched(float dt, bool isShadowPass)
        {
            if (!shaderReady || isShadowPass || isSpectator || meshRefOpaque == null || entity.AnimManager?.Animator == null)
            {
                base.DoRender3DOpaqueBatched(dt, isShadowPass);
                return;
            }

            scrollProgress = (scrollProgress + dt / ScrollPeriodSeconds) % 1f;
            EnsureJointMatrixOffsetResolved();

            // The engine has already bound its own shared program for this batched pass - swap to
            // ours for just this draw, then explicitly restore it (Use() throws if the engine's
            // program isn't Stop()'d first) so the next Shape-rendered entity in this batch isn't
            // left pointed at ours.
            IShaderProgram engineProg = capi.Render.CurrentActiveShader;
            engineProg?.Stop();
            IShaderProgram prog = shaderProgram!;
            prog.Use();

            prog.Uniform("rgbaAmbientIn", capi.Render.AmbientColor);
            prog.Uniform("rgbaFogIn", capi.Render.FogColor);
            prog.Uniform("fogMinIn", capi.Render.FogMin);
            prog.Uniform("fogDensityIn", capi.Render.FogDensity);
            prog.UniformMatrix("projectionMatrix", capi.Render.CurrentProjectionMatrix);
            prog.Uniform("lightPosition", capi.Render.ShaderUniforms.LightPosition3D);
            prog.Uniform("rgbaLightIn", lightrgbs);
            prog.Uniform("extraGlow", entity.Properties.Client.GlowLevel);

            float[] jointMatrices = entity.AnimManager.Animator.Matrices;
            if (jointMatrixOffset + 16 <= jointMatrices.Length)
            {
                Array.Copy(jointMatrices, jointMatrixOffset, jointMatScratch, 0, 16);
                Mat4f.Mul(combinedModelMat, ModelMat, jointMatScratch);
                prog.UniformMatrix("modelMatrix", combinedModelMat);
            }
            else
            {
                prog.UniformMatrix("modelMatrix", ModelMat);
            }
            prog.UniformMatrix("viewMatrix", capi.Render.CurrentModelviewMatrix);
            prog.Uniform("addRenderFlags", AddRenderFlags);
            prog.Uniform("windWaveIntensity", 0f);
            prog.Uniform("entityId", (int)entity.EntityId);
            prog.Uniform("glitchFlicker", 0);
            prog.Uniform("frostAlpha", 0f);
            prog.Uniform("waterWaveCounter", capi.Render.ShaderUniforms.WaterWaveCounter);

            TextureAtlasPosition cloudTexPos = this["cloud"];
            prog.Uniform("entityTexVBounds", cloudTexPos.y1, cloudTexPos.y2);
            prog.Uniform("uvScrollOffset", scrollProgress);

            color.R = (entity.RenderColor >> 16 & 0xff) / 255f;
            color.G = ((entity.RenderColor >> 8) & 0xff) / 255f;
            color.B = ((entity.RenderColor >> 0) & 0xff) / 255f;
            color.A = ((entity.RenderColor >> 24) & 0xff) / 255f;
            prog.Uniform("renderColor", color);

            capi.Render.RenderMultiTextureMesh(meshRefOpaque, "entityTex");

            prog.Stop();
            engineProg?.Use();
        }
    }
}
