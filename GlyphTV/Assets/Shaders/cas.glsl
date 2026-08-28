//!HOOK MAIN
//!BIND HOOKED
//!DESC FidelityFX CAS (Contrast Adaptive Sharpening)

#define SHARPNESS 0.65

vec4 hook() {
    vec2 pos = HOOKED_pos;
    vec2 pt = HOOKED_pt;
    
    vec3 a = HOOKED_tex(pos + vec2(-pt.x, -pt.y)).rgb;
    vec3 b = HOOKED_tex(pos + vec2(0.0, -pt.y)).rgb;
    vec3 c = HOOKED_tex(pos + vec2(pt.x, -pt.y)).rgb;
    vec3 d = HOOKED_tex(pos + vec2(-pt.x, 0.0)).rgb;
    vec3 e = HOOKED_tex(pos).rgb;
    vec3 f = HOOKED_tex(pos + vec2(pt.x, 0.0)).rgb;
    vec3 g = HOOKED_tex(pos + vec2(-pt.x, pt.y)).rgb;
    vec3 h = HOOKED_tex(pos + vec2(0.0, pt.y)).rgb;
    vec3 i = HOOKED_tex(pos + vec2(pt.x, pt.y)).rgb;
    
    vec3 min_rgb = min(min(min(d, e), min(f, b)), h);
    vec3 min_rgb2 = min(min(min(min_rgb, a), min(c, g)), i);
    min_rgb += min_rgb2;

    vec3 max_rgb = max(max(max(d, e), max(f, b)), h);
    vec3 max_rgb2 = max(max(max(max_rgb, a), max(c, g)), i);
    max_rgb += max_rgb2;
    
    vec3 rcp_max = 1.0 / max(max_rgb, vec3(1e-5));
    vec3 amp = clamp(min(min_rgb, 2.0 - max_rgb) * rcp_max, 0.0, 1.0);
    amp = inversesqrt(amp);
    
    float peak = -3.0 * SHARPNESS + 8.0;
    vec3 w = -1.0 / (amp * peak);
    vec3 rcp_w = 1.0 / (1.0 + 4.0 * w);
    
    vec3 col = clamp((b*w + d*w + f*w + h*w + e) * rcp_w, 0.0, 1.0);
    return vec4(col, 1.0);
}
