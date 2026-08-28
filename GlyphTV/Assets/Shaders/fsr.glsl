//!HOOK MAIN
//!BIND HOOKED
//!DESC FidelityFX FSR (RCAS / Super Resolution)

#define FSR_SHARPENING 0.75

vec4 hook() {
    vec2 pos = HOOKED_pos;
    vec2 pt = HOOKED_pt;

    vec3 b = HOOKED_tex(pos + vec2(0.0, -pt.y)).rgb;
    vec3 d = HOOKED_tex(pos + vec2(-pt.x, 0.0)).rgb;
    vec3 e = HOOKED_tex(pos).rgb;
    vec3 f = HOOKED_tex(pos + vec2(pt.x, 0.0)).rgb;
    vec3 h = HOOKED_tex(pos + vec2(0.0, pt.y)).rgb;

    vec3 mn = min(min(min(d, e), min(f, b)), h);
    vec3 mx = max(max(max(d, e), max(f, b)), h);

    vec3 hit = min(mn, 2.0 - mx);
    vec3 lobe = clamp(-hit / (mx * 4.0 + 1e-4), -0.18, 0.0);
    vec3 w = lobe * FSR_SHARPENING;

    vec3 result = (b * w + d * w + f * w + h * w + e) / (1.0 + 4.0 * w);
    return vec4(clamp(result, 0.0, 1.0), 1.0);
}
