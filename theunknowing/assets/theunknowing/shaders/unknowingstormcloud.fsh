#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

in vec2 uv;
in vec4 color;
in vec4 rgbaFog;
in float fogAmount;
in float glowLevel;
in vec3 vertexPosition;
flat in int renderFlags;
in vec3 normal;
in vec4 worldPos;
in vec3 blockLight;
in vec4 camPos;
in float damageEffect;
in float fragFrostAlpha;

// Our include system is dumb and does not do conditional includes
// So we add a OIT preprocceor test to oit.fsh as well
#include oit.fsh

#if USEOIT==0
	layout(location = 0) out vec4 outColor;
	layout(location = 1) out vec4 outGlow;
	#if SSAOLEVEL > 0
	in vec4 fragPosition;
	in vec4 gnormal;
	layout(location = 2) out vec4 outGNormal;
	layout(location = 3) out vec4 outGPosition;
	#endif
#endif

uniform sampler2D entityTex;
uniform float alphaTest = 0.001;
uniform float glitchEffectStrength;
uniform int entityId;
uniform int glitchFlicker;
#if defined(ALLOWDEPTHOFFSET)
#if ALLOWDEPTHOFFSET > 0
uniform float depthOffset;
#endif
#endif

// Wraps the sampled V coordinate within [entityTexVBounds.x, entityTexVBounds.y] - the
// "cloud" face's own sub-rectangle inside the shared EntityTextureAtlas - rather than
// wrapping the whole atlas, which would scroll into other entities' textures once the
// offset crossed our sub-rect's edge. entityTexVBounds is set from
// StormcloudRenderer.this["cloud"] (a TextureAtlasPosition, atlas-normalized 0-1) each
// frame; uvScrollOffset is a plain 0-1 ramp (fraction of one full loop) advanced by
// StormcloudRenderer from real elapsed time, independent of the atlas layout. Faces
// outside these bounds (the "top" cap) fall through unscrolled - see the bounds check
// below, not a texture-wide effect.
uniform float uvScrollOffset = 0.0;
uniform vec2 entityTexVBounds = vec2(0.0, 0.0);

#include vertexflagbits.ash
#include fogandlight.fsh
#include noise3d.ash
#include noise2d.ash
#include underwatereffects.fsh

void main() {
	float b = 1;

	if (damageEffect > 0) {
		float f = cnoise2(floor(vec2(uv.x, uv.y) * 4096) / 4);
		if (f < damageEffect - 1.3) discard;
		b = min(1, f * 1.5 + 0.65 + (1-damageEffect));
	}

	vec2 scrollUv = uv;
	float vSpan = entityTexVBounds.y - entityTexVBounds.x;
	if (vSpan > 0.0 && uv.y >= entityTexVBounds.x && uv.y <= entityTexVBounds.y) {
		// Minus, not plus - reads as the nebula sinking/drifting downward rather than
		// rising. GLSL mod() is floor-based, so it stays in [0, vSpan) for negative
		// operands too.
		scrollUv.y = entityTexVBounds.x + mod((uv.y - entityTexVBounds.x) - uvScrollOffset * vSpan, vSpan);
	}
	vec4 texColor = textureLod(entityTex, scrollUv, 0.0);

	#if SHADOWQUALITY > 0
	float intensity = 0.34 + (1 - shadowIntensity)/8.0; // this was 0.45, which makes shadow acne visible on blocks
	#else
	float intensity = 0.45;
	#endif


	//float seed = mod(entityId, 1000) / 5.0; - this is broken on NVIDIA cards O_O
	int eidfloor = (entityId / 100) * 100;
	float seed = (entityId - eidfloor) / 5.0;

	texColor = applyFrostEffect(fragFrostAlpha, texColor, normal, vertexPosition + vec3(seed));
	if (psychedelicStrength > Epsilon) texColor = applyPsychedelicEffect(texColor, vertexPosition, 0);
	if (glitchStrength > Epsilon) texColor = applyRustEffect(texColor, normal, vertexPosition + vec3(seed), 0);

	texColor *= color;
	texColor.rgb *= b;

#if USEOIT>0
	vec4 outColor;
#endif

	float murkiness=getUnderwaterMurkiness();
	if (murkiness > 0) {
		outColor = applyFogAndShadowWithNormal(texColor, 0, normal, 1, intensity, worldPos.xyz);
		outColor.rgb = applyUnderwaterEffects(outColor.rgb, murkiness);
	} else {
		outColor = applyFogAndShadowWithNormal(texColor, fogAmount, normal, 1, intensity, worldPos.xyz);
	}


	if (glitchFlicker >0 && glitchEffectStrength > 0) {
		float g = gnoise(vec3(gl_FragCoord.y / 2.0, gl_FragCoord.x / 2.0, windWaveCounter*30 + entityId * 3));
		outColor.a *= mix(1, clamp(0.7 + g / 2, 0, 1), glitchEffectStrength);

		float b = gnoise(vec3(0, 0, windWaveCounter*60 + entityId * 3));
		outColor.a *= mix(1, clamp(b * 10 + 2, 0, 1), glitchEffectStrength);
	}

#if NORMALVIEW == 0
	if (outColor.a < alphaTest) discard;
#endif



	float glow = 0;
#if SHINYEFFECT > 0
	outColor = mix(applyReflectiveEffect(outColor, glow, renderFlags, uv, normal, worldPos, camPos, vec3(1)), outColor, min(1, 2 * fogAmount));
#endif

#if USEOIT==0 && SSAOLEVEL > 0
	outGPosition = vec4(fragPosition.xyz, fogAmount + glowLevel);
	outGNormal = vec4(gnormal.xyz, 0);
#endif

#if NORMALVIEW > 0
	outColor = vec4((normal.x + 1) / 2, (normal.y + 1)/2, (normal.z+1)/2, 1);
#endif



#if USEOIT > 0
	OIT(outColor, glowLevel+glow);
#else
	outGlow = vec4(glowLevel + glow, 0, 0, color.a);
#endif



#if defined(ALLOWDEPTHOFFSET) && ALLOWDEPTHOFFSET > 0
	// This likely tanks performance in any other scenario so we do only only for the first person mode rendering. See also https://www.khronos.org/opengl/wiki/Early_Fragment_Test#Limitations
	gl_FragDepth = gl_FragCoord.z + depthOffset;

	// A bit hacky: We use ALLOWDEPTHOFFSET for the first person rendering. SSAO seems to break on it, so we disable it
	#if USEOIT==0 && SSAOLEVEL > 0
		outGPosition.w=1;
	#endif

#endif

}
