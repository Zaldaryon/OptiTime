#version 330 core

uniform sampler2D inputTexture;
uniform sampler2D glowParts;

in vec2 texCoord;
in vec3 sunPosScreen;
in float iGlobalTime;
in float intensity;
in float direction;

out vec4 outColor;

const float decay = 0.9985;

float hash(vec2 p) { return fract(sin(dot(p, vec2(41, 289)))*45758.5453); }

vec2 clampDeltas(vec2 dtuv) {
	if (length(dtuv) > 0.005) {
		dtuv = normalize(dtuv) * 0.005;
	}
	return dtuv;
}

vec4 applyGodRays(in vec2 uv, in vec2 nSunPos) {
	float weight = intensity / 23.0 / 1.5;
	
	int samples = int(90 * min(1, intensity * 1.2));

    // Early out when intensity/glow is negligible to avoid wasted samples
    float baseGlow = texture(glowParts, uv).g;
    if (intensity < 0.05 || baseGlow < 0.01) {
        return texture(inputTexture, uv) * baseGlow;
    }
	
	vec2 sdTuv = clampDeltas((nSunPos - uv) * intensity / 200 * direction);
	vec2 ldTuv = clampDeltas((nSunPos - uv) * intensity / 64 * direction);
	vec2 dTuv = sdTuv;
	
	float glow = texture(glowParts, uv).g;
    vec4 col = texture(inputTexture, uv) * glow;
    
    for (float i=0.0; i < samples; i++) {
		uv.x = clamp(uv.x + dTuv.x, 0, 1);
		uv.y = clamp(uv.y + dTuv.y, 0, 1);
        col += texture(inputTexture, uv) * texture(glowParts, uv).g * weight;
        weight *= decay;
		dTuv = mix(sdTuv, ldTuv, i/samples);
    }
	
	col.rgb *= clamp(1 - max((col.r+col.g+col.b)/3 - 0.7, 0), 0, 1);
	col.a = min(1, col.a);
	
    return col;
}

void main(void) {
	vec2 nSunPos = (clamp(sunPosScreen.xy, -10, 10) + 1) / 2;	
	outColor = applyGodRays(texCoord, nSunPos);	
	outColor.a=1;
}
