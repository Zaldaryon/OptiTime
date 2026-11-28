#version 330 core

uniform sampler2D inputTexture;
uniform sampler2D depthTexture;

in vec2 frameSize;
in vec2 texCoords[11];

out vec4 outColor;

// 9-tap bilateral blur: drops the redundant center depth fetch and the 0.3% outer tails
// (texCoords[10] is also left unwritten at (0,0) by the vanilla vertex loop i=-5..4).
void main(void)
{
	float refDepth = texture(depthTexture, texCoords[5]).r;
	const float fac = 300.0;

	float w1 = (1.0 - clamp(abs(texture(depthTexture, texCoords[1]).r - refDepth) * fac, 0.0, 1.0)) * 0.015715;
	float w2 = (1.0 - clamp(abs(texture(depthTexture, texCoords[2]).r - refDepth) * fac, 0.0, 1.0)) * 0.051008;
	float w3 = (1.0 - clamp(abs(texture(depthTexture, texCoords[3]).r - refDepth) * fac, 0.0, 1.0)) * 0.118235;
	float w4 = (1.0 - clamp(abs(texture(depthTexture, texCoords[4]).r - refDepth) * fac, 0.0, 1.0)) * 0.195779;
	const float w5 = 0.231613;
	float w6 = (1.0 - clamp(abs(texture(depthTexture, texCoords[6]).r - refDepth) * fac, 0.0, 1.0)) * 0.195779;
	float w7 = (1.0 - clamp(abs(texture(depthTexture, texCoords[7]).r - refDepth) * fac, 0.0, 1.0)) * 0.118235;
	float w8 = (1.0 - clamp(abs(texture(depthTexture, texCoords[8]).r - refDepth) * fac, 0.0, 1.0)) * 0.051008;
	float w9 = (1.0 - clamp(abs(texture(depthTexture, texCoords[9]).r - refDepth) * fac, 0.0, 1.0)) * 0.015715;

	float wsum = w1 + w2 + w3 + w4 + w5 + w6 + w7 + w8 + w9;

	vec4 out_colour = texture(inputTexture, texCoords[1]) * w1;
	out_colour += texture(inputTexture, texCoords[2]) * w2;
	out_colour += texture(inputTexture, texCoords[3]) * w3;
	out_colour += texture(inputTexture, texCoords[4]) * w4;
	out_colour += texture(inputTexture, texCoords[5]) * w5;
	out_colour += texture(inputTexture, texCoords[6]) * w6;
	out_colour += texture(inputTexture, texCoords[7]) * w7;
	out_colour += texture(inputTexture, texCoords[8]) * w8;
	out_colour += texture(inputTexture, texCoords[9]) * w9;

	outColor = out_colour / wsum;
}
