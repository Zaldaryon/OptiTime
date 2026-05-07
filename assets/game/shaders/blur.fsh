#version 330 core

uniform sampler2D inputTexture;

in vec2 frameSize;
in vec2 texCoords[21];

out vec4 outColor;

// http://dev.theomader.com/gaussian-kernel-calculator/
void main(void)
{
	vec4 out_colour = vec4(0.0);
	out_colour += texture(inputTexture, texCoords[0]) * 0.001422;
	out_colour += texture(inputTexture, texCoords[1]) * 0.004255;
	out_colour += texture(inputTexture, texCoords[2]) * 0.011001;
	out_colour += texture(inputTexture, texCoords[3]) * 0.024574;
	out_colour += texture(inputTexture, texCoords[4]) * 0.047431;
	out_colour += texture(inputTexture, texCoords[5]) * 0.0791;
	out_colour += texture(inputTexture, texCoords[6]) * 0.113978;
	out_colour += texture(inputTexture, texCoords[7]) * 0.141908;
	out_colour += texture(inputTexture, texCoords[8]) * 0.152663;
	out_colour += texture(inputTexture, texCoords[9]) * 0.141908;
	out_colour += texture(inputTexture, texCoords[10]) * 0.113978;
	out_colour += texture(inputTexture, texCoords[11]) * 0.0791;
	out_colour += texture(inputTexture, texCoords[12]) * 0.047431;
	out_colour += texture(inputTexture, texCoords[13]) * 0.024574;
	out_colour += texture(inputTexture, texCoords[14]) * 0.011001;
	out_colour += texture(inputTexture, texCoords[15]) * 0.004255;
	out_colour += texture(inputTexture, texCoords[16]) * 0.001422;
	
	outColor = out_colour;
}