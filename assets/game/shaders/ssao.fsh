#version 330 core

// VBAO - Visibility Bitmask Ambient Occlusion (OptiTime).
// Replaces vanilla Crytek-style SSAO with the algorithm from
// Therrien & Levesque, "Screen Space Indirect Lighting with
// Visibility Bitmask" (HPG 2023, arXiv:2301.11376).
//
// Uses horizon-based slices with a 32-bit bitmask per slice to track
// occluded angular sectors. Handles thin geometry (fences, leaves)
// naturally without the vanilla leavesHack branch.
//
// Same uniform interface as vanilla for engine compatibility.
// Quality 1: 3 dirs x 3 steps x 2 sides = 18 gPosition fetches
// Quality 2: 4 dirs x 4 steps x 2 sides = 32 gPosition fetches
// vs vanilla: 20-24 random hemisphere samples + per-sample matrix
// multiply + leavesHack gNormal fetches (up to 2x for vegetation).
// VBAO produces higher quality per sample (coherent slices, thin
// geometry handling via bitmask) with fewer or comparable fetches
// and no per-sample projection math.

uniform sampler2D gPosition;
uniform sampler2D gNormal;
uniform sampler2D texNoise;
uniform vec3 samples[64];
uniform vec2 screenSize;
uniform sampler2D revealage;
uniform mat4 projection;

in vec2 texcoord;
out vec4 outOcclusion;

#if SSAOLEVEL == 2
const int DIR_COUNT = 4;
const int STEP_COUNT = 4;
const float RADIUS = 0.9;
#else
const int DIR_COUNT = 3;
const int STEP_COUNT = 3;
const float RADIUS = 0.9;
#endif

const int SECTOR_COUNT = 32;
const float THICKNESS = 0.4;
const float PI = 3.14159265;
const float HALF_PI = PI * 0.5;

// Bayer dither for spatial noise
float bayer2(vec2 a) {
    a = floor(a);
    return fract(dot(a, vec2(0.5, a.y * 0.75)));
}
float bayer4(vec2 a)   { return bayer2(0.5 * a) * 0.25 + bayer2(a); }
float bayer8(vec2 a)   { return bayer4(0.5 * a) * 0.25 + bayer2(a); }
float bayer16(vec2 a)  { return bayer4(0.25 * a) * 0.0625 + bayer4(a); }
float bayer128(vec2 a) { return bayer16(0.125 * a) * 0.015625 + bayer8(a); }

// Fast acos approximation (Lagarde 2014, max error ~0.02 rad)
float fastAcos(float x) {
    float ax = abs(x);
    float res = -0.156583 * ax + HALF_PI;
    res *= sqrt(1.0 - ax);
    return x >= 0.0 ? res : PI - res;
}

// Set bits in bitmask between minH and maxH (normalized 0..1 over hemisphere)
uint updateSectors(float minH, float maxH, uint bitfield) {
    uint startBit = uint(minH * float(SECTOR_COUNT));
    uint span = min(uint(ceil((maxH - minH) * float(SECTOR_COUNT))), uint(SECTOR_COUNT));
    uint mask;
    if (span == 0u) {
        mask = 0u;
    } else if (span >= 32u) {
        mask = 0xFFFFFFFFu;
    } else {
        mask = (0xFFFFFFFFu >> (32u - span));
    }
    return bitfield | (mask << startBit);
}

// Manual popcount for GLSL 330 (bitCount requires GLSL 400)
int popcount(uint v) {
    v = v - ((v >> 1u) & 0x55555555u);
    v = (v & 0x33333333u) + ((v >> 2u) & 0x33333333u);
    v = (v + (v >> 4u)) & 0x0F0F0F0Fu;
    return int((v * 0x01010101u) >> 24u);
}

void main()
{
    float wboitatn = max(0.0, 1.0 - texture(revealage, texcoord).r) * 0.75;

    vec4 posData = texture(gPosition, texcoord);
    vec3 fragPos = posData.xyz;
    float attenuate = posData.w + wboitatn;

    vec4 normData = texture(gNormal, texcoord);
    vec3 normal = normalize(normData.xyz);
    bool isLeaves = normData.w > 0.0;

    // Vanilla normal offset to fix distant flickering
    if (!isLeaves) {
        fragPos += normal * clamp(-fragPos.z / 150.0 - 0.05, 0.0, 10.0);
    }

    float distanceFade = clamp(1.2 - (-fragPos.z) / 250.0, 0.0, 1.0);

    if (fragPos.x == 0.0 || distanceFade == 0.0) {
        outOcclusion = vec4(1.0);
        return;
    }

    // View direction (camera is at origin in view space)
    vec3 V = normalize(-fragPos);

    // Spatial noise for direction rotation
    float dither = bayer128(texcoord * screenSize);

    // Pixel size in UV space
    vec2 pixelSize = 1.0 / screenSize;

    // Radius in screen space (approximate via projection)
    float screenRadius = (RADIUS * 0.5 * projection[1][1]) / (-fragPos.z);
    screenRadius = max(screenRadius, float(STEP_COUNT) * pixelSize.x);

    float stepSize = screenRadius / float(STEP_COUNT);

    float totalOcclusion = 0.0;

    for (int d = 0; d < DIR_COUNT; d++) {
        // Direction angle with spatial jitter
        float angle = (float(d) + dither) * (PI / float(DIR_COUNT));
        vec2 dir = vec2(cos(angle), sin(angle));

        // Slice geometry: compute projected normal onto slice plane
        vec3 sliceDir = vec3(dir.x, dir.y, 0.0);
        vec3 sliceN = normalize(cross(sliceDir, V));
        vec3 projN = normal - sliceN * dot(normal, sliceN);
        float projNLen = length(projN);
        float cosN = clamp(dot(projN / projNLen, V), -1.0, 1.0);

        // Tangent of the slice (perpendicular to V within slice plane)
        vec3 T = cross(V, sliceN);
        float N_angle = -sign(dot(projN, T)) * fastAcos(cosN);

        uint occludedBits = 0u;

        // March in both directions along the slice
        for (int side = 0; side < 2; side++) {
            float sideSign = side == 0 ? 1.0 : -1.0;
            vec2 rayDir = dir * sideSign;

            for (int s = 1; s <= STEP_COUNT; s++) {
                vec2 sampleUV = texcoord + rayDir * stepSize * float(s);
                sampleUV = clamp(sampleUV, pixelSize, 1.0 - pixelSize);

                vec3 samplePos = texture(gPosition, sampleUV).xyz;
                vec3 deltaPos = samplePos - fragPos;
                float deltaLen = length(deltaPos);

                if (deltaLen < 0.001) continue;

                // Front horizon angle
                float frontCos = dot(deltaPos / deltaLen, V);
                float frontAngle = fastAcos(frontCos);

                // Back face (constant thickness model)
                vec3 backPos = deltaPos - V * THICKNESS;
                float backLen = length(backPos);
                float backCos;
                if (backLen > 0.001) {
                    backCos = dot(backPos / backLen, V);
                } else {
                    backCos = frontCos;
                }
                float backAngle = fastAcos(backCos);

                // Convert to hemisphere-normalized coordinates [0..1]
                float hFront = clamp(((sideSign * -frontAngle) - N_angle + HALF_PI) / PI, 0.0, 1.0);
                float hBack = clamp(((sideSign * -backAngle) - N_angle + HALF_PI) / PI, 0.0, 1.0);

                float minH = min(hFront, hBack);
                float maxH = max(hFront, hBack);

                if (maxH > minH) {
                    occludedBits = updateSectors(minH, maxH, occludedBits);
                }
            }
        }

        // Visibility = 1 - fraction of occluded sectors
        float sliceAO = 1.0 - float(popcount(occludedBits)) / float(SECTOR_COUNT);
        totalOcclusion += sliceAO * projNLen;
    }

    totalOcclusion /= float(DIR_COUNT);

    // Apply distance fade and attenuation (matching vanilla behavior)
    float occ = clamp(totalOcclusion + (1.0 - totalOcclusion) * (1.0 - distanceFade) + (1.0 - totalOcclusion) * attenuate, 0.0, 1.0);

    // Clamp lower limit (matching vanilla)
#if SSAOLEVEL == 2
    occ = max(occ, 0.5);
#else
    occ = max(occ, 0.7);
#endif

    // Boost AO for non-leaves (matching vanilla 1.4x multiplier)
    if (!isLeaves) {
        occ = 1.0 - (1.0 - occ) * 1.4;
    }

    outOcclusion = vec4(occ, occ, occ, 1.0);
}
