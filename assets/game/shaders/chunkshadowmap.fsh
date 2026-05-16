#version 330 core

uniform sampler2D tex2d;

in vec2 uv;
out vec4 outColor;

void main () {
	outColor = texture(tex2d, uv);
	if (outColor.a < 0.15) discard;

}
