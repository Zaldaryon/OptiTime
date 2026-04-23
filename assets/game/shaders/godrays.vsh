#version 330 core

uniform vec2 invFrameSizeIn;
uniform vec3 sunPosScreenIn;
uniform vec3 sunPos3dIn;
uniform vec3 playerViewVector;
uniform float iGlobalTimeIn;
uniform float directionIn;
uniform int dusk;

out vec2 texCoord;
out vec3 sunPosScreen;
out float iGlobalTime;
out float intensity;
out float direction;

void main(void)
{
	// https://randallr.wordpress.com/2014/06/14/rendering-a-screen-covering-triangle-in-opengl
	float x = -1.0 + float((gl_VertexID & 1) << 2);
    float y = -1.0 + float((gl_VertexID & 2) << 1);
    gl_Position = vec4(x, y, 0, 1);
    texCoord = vec2((x+1.0) * 0.5, (y + 1.0) * 0.5);
	
	sunPosScreen = sunPosScreenIn;
	iGlobalTime = iGlobalTimeIn;
	
	float sunPlrAngle = (dot(sunPos3dIn, playerViewVector) + 1) / 3;
	
	direction = dot(sunPos3dIn, playerViewVector) >= 0 ? 1 : -1;
	
	// https://www.toolfk.com/online-plotter-frame/#W3sidHlwZSI6MCwiZXEiOiJtYXgoMSwxLjc1KigxLTYqYWJzKHgtMC4yMikpKSIsImNvbG9yIjoiIzAwMDAwMCJ9LHsidHlwZSI6MTAwMCwid2luZG93IjpbIi0xIiwiMSIsIjAiLCIyIl19XQ--
	float dawnMul = max(1, (1-dusk) * 2 * (1 - 6*abs(sunPos3dIn.y - 0.1)));
	
	// Intensity is determined by how directly the player is looking at the sun
	// above intensity 0.8 we get godrays where they shouldn't be o.o
	intensity = clamp(sunPlrAngle * dawnMul, 0, 0.8); 
}