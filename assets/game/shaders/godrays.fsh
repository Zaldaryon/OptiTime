#version 330 core

uniform sampler2D inputTexture;
uniform sampler2D glowParts;


in vec2 texCoord;
in vec3 sunPosScreen;
in float iGlobalTime;
in float intensity;
in float direction;

out vec4 outColor;


// Falloff over distance
const float decay = 0.9985;
const float invOneMinusDecay = 1.0 / (1.0 - decay);

float hash(vec2 p) { return fract(sin(dot(p, vec2(41, 289)))*45758.5453); }


vec2 clampDeltas(vec2 dtuv) {
	float lenSq = dot(dtuv, dtuv);
	if (lenSq > 0.000025) {
		dtuv *= 0.005 * inversesqrt(lenSq);
	}

	return dtuv;
}

vec4 applyGodRays(in vec2 uv, in vec2 nSunPos) {
	float weight = intensity / 23.0 / 1.5;

	int samples = int(180 * min(1, intensity * 1.2));

	float glow = texture(glowParts, uv).g;
	vec4 col = (glow > 0.0) ? (texture(inputTexture, uv) * glow) : vec4(0.0);

	if (samples > 0) {
		vec2 sdTuv = clampDeltas((nSunPos - uv) * intensity / 200 * direction);
		vec2 ldTuv = clampDeltas((nSunPos - uv) * intensity / 64 * direction);
		vec2 dTuv = sdTuv;
		float invSamples = 1.0 / float(samples);

		for (int i = 0; i < samples; i++) {
			vec2 nextUv = clamp(uv + dTuv, 0.0, 1.0);
			bool atCorner = (uv.x <= 0.0 || uv.x >= 1.0) && (uv.y <= 0.0 || uv.y >= 1.0);
			bool zeroDelta = sdTuv.x == 0.0 && sdTuv.y == 0.0 && ldTuv.x == 0.0 && ldTuv.y == 0.0;
			if (nextUv.x == uv.x && nextUv.y == uv.y && (atCorner || zeroDelta)) {
				float gEdge = texture(glowParts, uv).g;
				if (gEdge > 0.0) {
					float rem = float(samples - i);
					float geoSum = weight * (1.0 - pow(decay, rem)) * invOneMinusDecay;
					col += texture(inputTexture, uv) * (gEdge * geoSum);
				}
				break;
			}
			uv = nextUv;
			float g = texture(glowParts, uv).g;
			if (g > 0.0) {
				col += texture(inputTexture, uv) * (g * weight);
			}
			weight *= decay;
			dTuv = mix(sdTuv, ldTuv, float(i) * invSamples);
		}
	}

	col.rgb *= clamp(1 - max((col.r+col.g+col.b)/3 - 0.7, 0), 0, 1);

	col.a = min(1, col.a);

	return col;
}


void main(void) {
	vec2 nSunPos = (clamp(sunPosScreen.xy, -10, 10) + 1) / 2;
	outColor = applyGodRays(texCoord, nSunPos);

	outColor.a = 1;
}
