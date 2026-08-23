using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace TheUnknowing
{
    // Registered in place of the stock "Shape" renderer for theunknowing:stormcloud (see
    // TheUnknowingModSystem.StartClientSide and stormcloud.json's "renderer" field) - everything
    // about tesselation, mesh upload, and the spawn/widen keyframe animations is still handled by
    // the EntityShapeRenderer base class unchanged. The only thing this class adds is a
    // continuously scrolling nebula texture, which the base class's shared "entityanimated"
    // shader has no hook for (VS's shape/keyframe animation system only moves vertices - see
    // AnimationKeyFrameElement/ElementPose in vsapi - it has no concept of UV at all, so no amount
    // of shape JSON can animate the *texture* itself).
    //
    // Rather than rotating the mesh (tried and rejected - it reads as the box turning, not the
    // nebula churning, since all 4 side faces show the exact same static art), this scrolls the
    // sampled UV coordinate over time in a custom fragment shader
    // (assets/theunknowing/shaders/unknowingstormcloud.fsh, a copy of the stock entityanimated.fsh
    // plus that one addition). The vertex shader is a copy of entityanimated.vsh with its
    // Animation UBO lookup removed - see the comment above the constructor for why, and why
    // "column" gets a real joint (1, not 0) despite the shape never declaring one explicitly.
    //
    // The draw happens inside DoRender3DOpaqueBatched, not DoRender3DOpaque, despite the name -
    // see the doc comments on EntityRenderer (vsapi): DoRender3DOpaque runs with "no shader
    // initialized", while DoRender3DOpaqueBatched runs "with the Entityanimated shader loaded and
    // initialized" - i.e. inside the engine's normal opaque G-buffer setup, the same render
    // target every visible Shape-rendered entity in the game draws into. An earlier version of
    // this class put all its rendering in DoRender3DOpaque instead (misreading "batched" as
    // "shared shader, so don't touch it") - every diagnostic (GL errors, mesh state, matrix math)
    // came back clean there, yet the entity stayed totally invisible, because that hook simply
    // isn't guaranteed to land in the frame the player sees composited. Here, we briefly swap the
    // engine's shared program for our own mid-call and explicitly restore it afterward so
    // whichever Shape-rendered entity the engine batches next isn't affected. Losing the batching
    // optimization for just this swap is a non-issue: at most a handful of these entities exist
    // at once (one per storm chunk column).
    public class StormcloudRenderer : EntityShapeRenderer
    {
        private const string ShaderName = "unknowingstormcloud";

        // Real seconds for the starfield to scroll through one full texture-height loop -
        // deliberately not exposed in UnknowingConfig yet (unlike every other tunable in this
        // mod) since it hasn't been seen live; revisit once it has.
        private const float ScrollPeriodSeconds = 20f;

        private static IShaderProgram? shaderProgram;

        // False until LoadShader confirms the custom shader actually compiled - our fsh/vsh
        // #include several stock files that live in the "game" asset domain (oit.fsh,
        // noise3d.ash, etc.) while our own shader is registered under "theunknowing"; whether
        // that include resolves across domains isn't something confirmable by reading source
        // alone. If compilation fails for any reason, every override below falls back to the
        // base class's normal rendering (stock shader, no scroll) instead of leaving the entity
        // broken or invisible.
        private static bool shaderReady;

        // 0-1 fraction of one full scroll loop, advanced from real elapsed time and wrapped every
        // frame - kept independent of the atlas's actual sub-rect size (entityTexVBounds) so this
        // class never needs to know atlas layout; the shader multiplies it back out (see
        // unknowingstormcloud.fsh).
        private float scrollProgress;

        // Scratch buffers for folding the "column" element's animated transform (spawn/widen's
        // stretch keyframes) into modelMatrix ourselves before upload - see the comment on
        // "modelMatrix" in unknowingstormcloud.vsh for why this replaces the stock shader's
        // Animation UBO lookup. Reused every frame rather than allocated fresh.
        //
        // Reads entity.AnimManager.Animator.Matrices[jointOffset..], NOT
        // GetPosebyName("column").AnimModelMatrix (tried second, briefly) - hand-verified the
        // math for both against ShapeElement.GetLocalTransformMatrix's actual formula (animVersion
        // 0: Translate(origin) * Rotate * Scale * Translate(From/16 - origin)) and
        // GetInverseModelMatrix (inverseModelTransform = Invert of that formula at bind pose,
        // tf=noTransform). For our mesh's actual baked vertex positions (absolute From/To block
        // coordinates, e.g. the "from" corner at (-6,-3,-6)), AnimModelMatrix * inverseModelTransform
        // (= Matrices[jointOffset]) correctly cancels the bind-pose offset before reapplying the
        // current scale - by hand, the "from" corner maps to (-16,-3,-16) at full widen scale,
        // exactly right (X/Z x2.6667, Y untouched). AnimModelMatrix alone is missing that
        // cancellation and is wrong for this use. jointOffset is NOT 0: every element starts at
        // JointId 0, but any element referenced by name in an animation's keyframes (ours is, by
        // both "spawn" and "widen") gets a real joint assigned starting at JointId 1 - resolved
        // once per instance from the compiled shape rather than hardcoded.
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

        public StormcloudRenderer(Entity entity, ICoreClientAPI api) : base(entity, api)
        {
            shaderProgram ??= LoadShader(api);
        }

        private static IShaderProgram LoadShader(ICoreClientAPI api)
        {
            IShaderProgram prog = api.Shader.NewShaderProgram();
            prog.AssetDomain = "theunknowing";
            prog.VertexShader = api.Shader.NewShader(EnumShaderType.VertexShader);
            prog.FragmentShader = api.Shader.NewShader(EnumShaderType.FragmentShader);
            // Our fsh #includes oit.fsh (copied verbatim from stock entityanimated.fsh), which
            // gates its whole body behind "#if USEOIT > 0" and, when true, replaces the plain
            // opaque outColor/outGlow (layout locations 0/1) with an entirely different 6-target
            // OIT accumulation layout (OITreveal/outReveal/outGlow/OITaccumulation0-2) meant for a
            // separate transparent-pass framebuffer that later gets composited by a dedicated
            // "transparentcompose" step. USEOIT is driven by this Oit flag (see
            // EnumShaderProgram.Entityanimated_Oit - the engine compiles entityanimated in both
            // variants) - explicit like ModSystemFpHands does for its own non-OIT shader. Left
            // unset, we render into the OIT layout while actually bound to the plain opaque
            // batched pass: depth writes fine (occludes clouds, casts a shadow) but the real
            // color attachment never gets our texColor - explains the "hole with nothing visible"
            // symptom exactly.
            prog.Oit = false;
            api.Shader.RegisterFileShaderProgram(ShaderName, prog);
            shaderReady = prog.Compile();
            if (!shaderReady)
            {
                api.Logger.Error("[TheUnknowing] {0} shader failed to compile - stormcloud will render without the UV scroll (stock shader fallback).", ShaderName);
            }
            return prog;
        }

        public override void DoRender3DOpaqueBatched(float dt, bool isShadowPass)
        {
            // Falls back to the base class's own batched draw (stock shader, shared with every
            // other Shape-rendered entity) both when our shader failed to compile and for the
            // shadow pass, which doesn't need the scrolling starfield since nobody sees the
            // shadow map's colors.
            if (!shaderReady || isShadowPass || isSpectator || meshRefOpaque == null || entity.AnimManager?.Animator == null)
            {
                base.DoRender3DOpaqueBatched(dt, isShadowPass);
                return;
            }

            scrollProgress = (scrollProgress + dt / ScrollPeriodSeconds) % 1f;
            EnsureJointMatrixOffsetResolved();

            // The engine has already bound its own shared "entityanimated" program for this
            // batched pass (capi.Render.CurrentActiveShader, per stock
            // EntityShapeRenderer.DoRender3DOpaqueBatched) before calling us here - swap to our
            // own program for just this entity's draw, then explicitly rebind the engine's
            // program afterward so whichever Shape-rendered entity renders next in this same
            // batch loop isn't left pointed at ours. ShaderProgramBase.Use() throws
            // ("Already a different shader in use!") if the engine's program isn't explicitly
            // Stop()'d first - confirmed live, it doesn't silently swap.
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
            // Defensive bounds check, not an expected case - falls back to the plain
            // (unanimated) ModelMat rather than letting Array.Copy throw and take rendering
            // down entirely for an out-of-range offset.
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
