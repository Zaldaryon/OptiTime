#version 330 core

uniform sampler2D inputTexture;

in vec2 frameSize;
in vec2 texCoords[21];

out vec4 outColor;

// Optimized 9-tap Gaussian blur (reduced from 17-tap)
// Maintains visual quality while reducing texture samples by ~47%
void main(void)
{
	// Pair taps to reduce ALU while keeping weights identical
	vec4 out_colour = vec4(0.0);
	out_colour += (texture(inputTexture, texCoords[4]) + texture(inputTexture, texCoords[12])) * 0.05;
	out_colour += (texture(inputTexture, texCoords[5]) + texture(inputTexture, texCoords[11])) * 0.09;
	out_colour += (texture(inputTexture, texCoords[6]) + texture(inputTexture, texCoords[10])) * 0.12;
	out_colour += (texture(inputTexture, texCoords[7]) + texture(inputTexture, texCoords[9])) * 0.15;
	out_colour += texture(inputTexture, texCoords[8]) * 0.18;

	outColor = out_colour;
}
