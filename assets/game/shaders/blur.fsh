#version 330 core

// Linear-sampling 9-tap Gaussian blur (OptiTime).
// Mathematically equivalent to vanilla's 17-tap Gaussian via the
// bilinear filter offset trick (Rakos, RasterGrid 2010). Each pair of
// adjacent vanilla samples collapses into one bilinear fetch at a
// fractional offset, weighted by their combined Gaussian weight.
//
// Vanilla framebuffer textures are configured with GL_LINEAR
// (ClientPlatformWindows.setupAttachment, line 1561), so the sampler
// returns the correct weighted blend in hardware.
//
// Texture fetches per pixel: 17 -> 9 (47% reduction).

uniform sampler2D inputTexture;

in vec2 texCoords[9];

out vec4 outColor;

void main(void)
{
	vec4 c = vec4(0.0);
	c += texture(inputTexture, texCoords[0]) * 0.152663;
	c += texture(inputTexture, texCoords[1]) * 0.255886;
	c += texture(inputTexture, texCoords[2]) * 0.255886;
	c += texture(inputTexture, texCoords[3]) * 0.126531;
	c += texture(inputTexture, texCoords[4]) * 0.126531;
	c += texture(inputTexture, texCoords[5]) * 0.035575;
	c += texture(inputTexture, texCoords[6]) * 0.035575;
	c += texture(inputTexture, texCoords[7]) * 0.005677;
	c += texture(inputTexture, texCoords[8]) * 0.005677;

	outColor = c;
}
