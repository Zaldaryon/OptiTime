#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

uniform sampler2D terrainTex;
uniform sampler2D terrainTexLinear;

in vec4 rgba;
in vec4 rgbaFog;
in float fogAmount;
in vec2 uv;
in float glowLevel;
flat in int renderFlags;
in vec3 normal;
in vec4 worldPos;
in vec3 vertexPosition;
in vec3 blockLight;
in vec4 gnormal;
in vec4 camPos;
in float lod0Fade;
in float nb;

uniform float alphaTest;
uniform float fogDensityIn;
uniform float fogMinIn;
uniform float horizonFog;
uniform vec3 sunPosition;
uniform float dayLight;
uniform int haxyFade;

layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;
#if SSAOLEVEL > 0
layout(location = 2) out vec4 outGNormal;
layout(location = 3) out vec4 outGPosition;
#endif

#include vertexflagbits.ash
#include fogandlight.fsh
#include dither.fsh
#include skycolor.fsh
#include colormap.fsh
#include underwatereffects.fsh

void main()
{
	vec4 texColor = getColorMapped(terrainTexLinear, texture(terrainTex, uv)) * rgba;

#if NORMALVIEW == 0
	// Alpha never changes across shadow, fog, sky-fade, or underwater stages.
	// Discarding cutout foliage/grass fragments up front skips 9-18 shadow map fetches and lighting.
	float aTest = texColor.a + max(0.0, 1.0 - rgba.a) * min(1.0, texColor.a * 10.0) - lod0Fade;

	if ((renderFlags & WindModeBitMask) == WindModeWeakLowAlphaTest) aTest *= 4.0;

	if (aTest < alphaTest || rgba.a < 0.005) discard;
#endif

	if (psychedelicStrength > Epsilon) texColor = applyPsychedelicEffect(texColor, vertexPosition * 2.0, 0);
	if (glitchStrength > Epsilon) texColor = applyRustEffect(texColor, normal, vertexPosition, 1);

	float b = 1.0;
#if SHADOWQUALITY > 0
	if (shadowIntensity > 0.0001 && blockBrightness < 1.0) {
#if SHADOWQUALITY == 1
		if (shadowCoordsFar.w > 0.0) {
			float minShadowB = clamp(1.0 - shadowIntensity * shadowCoordsFar.w * 0.5 + blockBrightness, 0.0, 1.0);
			bool needShadow = nb > minShadowB;
#if SHINYEFFECT > 0
			needShadow = needShadow || ((renderFlags & ReflectiveBitMask) != 0);
#endif
			if (needShadow) {
				b = getBrightnessFromShadowMap();
			}
		}
#else
		if (shadowCoordsFar.w > 0.0 || shadowCoordsNear.w > 0.0) {
			float minShadowB = clamp(1.0 - shadowIntensity * (shadowCoordsFar.w + shadowCoordsNear.w) * 0.5 + blockBrightness, 0.0, 1.0);
			bool needShadow = nb > minShadowB;
#if SHINYEFFECT > 0
			needShadow = needShadow || ((renderFlags & ReflectiveBitMask) != 0);
#endif
			if (needShadow) {
				b = getBrightnessFromShadowMap();
			}
		}
#endif
	}
#endif

	float murkiness = getUnderwaterMurkiness();
	outColor = applyFogAndShadowFromBrightness(texColor, clamp(fogAmount - 50.0 * murkiness, 0.0, 1.0), min(b, nb), worldPos.xyz);

	float glow = 0.0;
	float godrayLevel = 0.0;

	if (haxyFade > 0) {
		if (rgba.a < 0.999) {
			vec4 skyColor = vec4(1.0);
			vec4 skyGlow = vec4(1.0);
			float sealevelOffsetFactor = 0.25;

			getSkyColorAt(worldPos.xyz, sunPosition, sealevelOffsetFactor, clamp(dayLight, 0.0, 1.0), horizonFog, skyColor, skyGlow);
			godrayLevel = skyGlow.g;
			outColor.rgb = mix(skyColor.rgb, outColor.rgb, max(1.0 - dayLight, max(0.0, rgba.a)));
		}
	}

	outColor.rgb = applyUnderwaterEffects(outColor.rgb, murkiness);

#if SHINYEFFECT > 0
	if ((renderFlags & ReflectiveBitMask) != 0) {
		outColor = mix(applyReflectiveEffect(outColor, glow, renderFlags, uv, normal, worldPos, camPos, blockLight), outColor, clamp(2.0 * fogAmount + 2.0 * (1.0 - b), 0.0, 1.0));
	}
	glow += pow(max(0.0, dot(normal, lightPosition)), 6.0) * 0.125 * shadowIntensity * (1.0 - fogAmount - murkiness);
#endif

#if SSAOLEVEL > 0
	outGPosition = vec4(camPos.xyz, fogAmount * 2.0 + glowLevel + murkiness);
	outGNormal = gnormal;
#endif

#if NORMALVIEW > 0
	outColor = vec4((normal.x + 1.0) * 0.5, (normal.y + 1.0) * 0.5, (normal.z + 1.0) * 0.5, 1.0);
#endif

	outGlow = vec4(glowLevel + glow, godrayLevel, 0.0, min(1.0, fogAmount + outColor.a));
}
