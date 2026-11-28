#version 330 core

uniform sampler2D gPosition;
uniform sampler2D gNormal;
uniform sampler2D texNoise;
uniform vec3[64] samples;
uniform vec2 screenSize;
uniform sampler2D revealage;

in vec2 texcoord;
out vec4 outOcclusion;

#if SSAOLEVEL == 2
const int kernelSize = 24;
const float invKernelSize = 1.0 / 24.0;
const float radius = 0.9;
#else
const int kernelSize = 20;
const float invKernelSize = 1.0 / 20.0;
const float radius = 0.9;
#endif

const float bias = 0.01;

uniform mat4 projection;

void main()
{
	vec4 posVal = texture(gPosition, texcoord);
	vec3 fragPos = posVal.xyz;
	float distanceFade = clamp(1.2 - (-fragPos.z) * 0.004, 0.0, 1.0);

	// Skip cleared sky pixels (0, 0, 0) and distant fragments before reading gNormal, revealage, or noise textures.
	if ((fragPos.x == 0.0 && fragPos.z == 0.0) || distanceFade == 0.0) {
		outOcclusion = vec4(1.0);
		return;
	}

	float wboitatn = max(0.0, 1.0 - texture(revealage, texcoord).r) * 0.75;
	float attenuate = posVal.w + wboitatn;

	vec4 normVal = texture(gNormal, texcoord);
	vec3 normal = normalize(normVal.xyz);
	bool leavesHack = normVal.w > 0.0;

	if (!leavesHack) {
		fragPos += normal * clamp(-fragPos.z * (1.0 / 150.0) - 0.05, 0.0, 10.0);
	}

	vec2 noiseScale = screenSize * 0.0625;
	vec3 randomVec = texture(texNoise, texcoord * noiseScale).xyz;

	vec3 tangent = normalize(randomVec - normal * dot(randomVec, normal));
	vec3 bitangent = cross(normal, tangent);
	mat3 TBNr = mat3(tangent * radius, bitangent * radius, normal * radius);

	// Perspective projection only needs rows 0, 1, and 3 to project sample positions into screen UV.
	vec2 projXY = vec2(projection[0][0], projection[1][1]);
	vec2 projZ = vec2(projection[2][0], projection[2][1]);
	float projW2 = projection[2][3];
	float projW3 = projection[3][3];
	vec2 uvMin = texcoord - 0.04;
	vec2 uvMax = texcoord + 0.04;

	float occlusion = 0.0;

	if (leavesHack) {
		for (int i = 0; i < kernelSize; ++i) {
			vec3 samplePos = fragPos + TBNr * samples[i];
			float invW = 0.5 / (samplePos.z * projW2 + projW3);
			vec2 offsetUV = clamp((samplePos.xy * projXY + samplePos.z * projZ) * invW + 0.5, uvMin, uvMax);

			float sampleDepth = texture(gPosition, offsetUV).z;
			float depthDiff = sampleDepth - (samplePos.z + bias);
			if (depthDiff >= 0.02 && depthDiff < 0.2 && abs(dot(texture(gNormal, offsetUV).rgb, normal) - 1.0) > 0.25) {
				occlusion += smoothstep(0.0, 1.0, radius / abs(fragPos.z - sampleDepth));
			}
		}
	} else {
		for (int i = 0; i < kernelSize; ++i) {
			vec3 samplePos = fragPos + TBNr * samples[i];
			float invW = 0.5 / (samplePos.z * projW2 + projW3);
			vec2 offsetUV = clamp((samplePos.xy * projXY + samplePos.z * projZ) * invW + 0.5, uvMin, uvMax);

			float sampleDepth = texture(gPosition, offsetUV).z;
			float depthDiff = sampleDepth - (samplePos.z + bias);
			if (depthDiff > 0.0 && depthDiff < 0.2) {
				occlusion += smoothstep(0.0, 1.0, radius / abs(fragPos.z - sampleDepth));
			}
		}
	}

	float occ = clamp(1.0 - min(1.0, occlusion * invKernelSize * distanceFade) * (1.0 - attenuate), 0.0, 1.0);

#if SSAOLEVEL == 2
	occ = max(occ, 0.5);
#else
	occ = max(occ, 0.7);
#endif

	if (!leavesHack) {
		occ = 1.0 - (1.0 - occ) * 1.4;
	}

	outOcclusion = vec4(occ, occ, occ, 1.0);
}
