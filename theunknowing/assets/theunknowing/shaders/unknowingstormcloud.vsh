#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

layout(location = 0) in vec3 vertexPositionIn;
layout(location = 1) in vec2 uvIn;
layout(location = 2) in vec4 colorIn;
layout(location = 3) in int flags;
layout(location = 4) in float damageEffectIn;
layout(location = 5) in int jointId;

uniform vec3 rgbaAmbientIn;
uniform vec4 rgbaLightIn;
uniform vec4 rgbaFogIn;
uniform float fogMinIn;
uniform float fogDensityIn;
uniform vec4 renderColor;
uniform int addRenderFlags;
uniform float frostAlpha = 0;
uniform mat4 projectionMatrix;
uniform mat4 viewMatrix;
uniform mat4 modelMatrix;
uniform int extraGlow;

// No longer needed but kept to not break mods that still assign these values
uniform int skipRenderJointId;
uniform int skipRenderJointId2;

// Stock entityanimated.vsh multiplies modelMatrix by an Animation UBO joint matrix here
// (ElementTransforms.values[jointId]) - deliberately not replicated in this custom shader.
// Registering it under our own program name left prog.UBOs with no "Animation" entry despite
// an identical UBO declaration compiling fine (confirmed live - crashed on
// prog.UBOs["Animation"] with KeyNotFoundException), so StormcloudRenderer instead folds the
// entity's single joint's animated transform into modelMatrix itself, in C#, from
// entity.AnimManager.Animator.Matrices, before this shader ever sees it - modelMatrix already
// IS the joint-animated transform by the time it reaches here. jointId is still declared
// above (location 5) purely to match the mesh's baked vertex format; its value is unused.

out vec2 uv;
out vec4 color;
out vec4 rgbaFog;
out float fogAmount;
out vec3 vertexPosition;
out vec4 worldPos;
out float damageEffect;
out vec4 camPos;
out float fragFrostAlpha;
flat out int renderFlags;

out vec4 glPos;

out vec3 normal;
#if SSAOLEVEL > 0
out vec4 fragPosition;
out vec4 gnormal;
#endif


#include vertexflagbits.ash
#include shadowcoords.vsh
#include fogandlight.vsh
#include vertexwarp.vsh

void main(void)
{
	damageEffect = damageEffectIn;
	mat4 animModelMat = modelMatrix;
	worldPos = animModelMat * vec4(vertexPositionIn, 1.0);

	renderFlags = flags | addRenderFlags;

	if ((renderFlags & WindModeFruitMask) > 0) {
		fragFrostAlpha = frostAlpha / 4;
		renderFlags &= ~WindModeFruitMask;
	} else fragFrostAlpha = frostAlpha;

	if ((renderFlags & WindModeWaterMask) > 0) {
		worldPos = applyLiquidWarping(true, worldPos, 5);
	} else {
		worldPos = applyVertexWarping(renderFlags, worldPos);
	}
	worldPos = applyGlobalWarping(worldPos);

	vertexPosition = vertexPositionIn.xyz * 1.5;

	vec4 cameraPos = camPos = viewMatrix * worldPos;

	uv = uvIn;
	int renderFlags = extraGlow + flags;
	color = renderColor * colorIn * applyLight(rgbaAmbientIn, rgbaLightIn, renderFlags, cameraPos);
	rgbaFog = rgbaFogIn;

	// Distance fade out
	color.a *= clamp(20 * (1.05 - length(worldPos.xz) / viewDistance) - 5, -1, 1);

	gl_Position = projectionMatrix * cameraPos;
	calcShadowMapCoords(viewMatrix, worldPos);


	fogAmount = getFogLevel(worldPos, fogMinIn, fogDensityIn);

	normal = unpackNormal(renderFlags);
	normal = (animModelMat * vec4(normal.x, normal.y, normal.z, 0)).xyz;

	#if SSAOLEVEL > 0
		fragPosition = cameraPos;
		gnormal = viewMatrix * vec4(normal, 0);
	#endif
}
