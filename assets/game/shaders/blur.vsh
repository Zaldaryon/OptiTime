#version 330 core

// Linear-sampling 9-tap Gaussian blur - vertex shader (OptiTime).
// Computes the 9 sample positions used by the matching blur.fsh.
// Center tap + 4 pairs per side, where each pair's offset is the
// weighted midpoint of two adjacent vanilla taps so that bilinear
// hardware filtering reproduces the original Gaussian sum exactly.

uniform vec2 frameSize;
uniform int isVertical;

out vec2 texCoords[9];

void main(void)
{
	float x = -1.0 + float((gl_VertexID & 1) << 2);
	float y = -1.0 + float((gl_VertexID & 2) << 1);
	gl_Position = vec4(x, y, 0, 1);
	vec2 texCoord = vec2((x + 1.0) * 0.5, (y + 1.0) * 0.5);

	vec2 axis;
	float pixelSize;
	if (isVertical == 1) {
		pixelSize = 1.0 / frameSize.y;
		axis = vec2(0.0, 1.0);
	} else {
		pixelSize = 1.0 / frameSize.x;
		axis = vec2(1.0, 0.0);
	}

	// Center tap (offset 0, weight 0.152663)
	texCoords[0] = texCoord;

	// Pair (1,2): combined weight 0.255886, offset 1.445437
	texCoords[1] = texCoord + axis * pixelSize *  1.445437;
	texCoords[2] = texCoord + axis * pixelSize * -1.445437;

	// Pair (3,4): combined weight 0.126531, offset 3.374815
	texCoords[3] = texCoord + axis * pixelSize *  3.374815;
	texCoords[4] = texCoord + axis * pixelSize * -3.374815;

	// Pair (5,6): combined weight 0.035575, offset 5.309010
	texCoords[5] = texCoord + axis * pixelSize *  5.309010;
	texCoords[6] = texCoord + axis * pixelSize * -5.309010;

	// Pair (7,8): combined weight 0.005677, offset 7.250485
	texCoords[7] = texCoord + axis * pixelSize *  7.250485;
	texCoords[8] = texCoord + axis * pixelSize * -7.250485;
}
