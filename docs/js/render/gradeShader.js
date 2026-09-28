// ---------------------------------------------------------------------------------------------------------------
// 'grade' post pass (owner: render). Runs in LINEAR HDR (before OutputPass tone mapping), so exposure pulses behave
// like a real camera: highlights roll off through ACES instead of clipping.
//   radial punch-in (zoom) -> rewind wobble -> chromatic aberration -> exposure / flash -> saturation
//   -> freeze (cold tint + frost edges) -> rewind (desaturated, scan-lined) -> danger (pulsing red edges)
//   -> vignette -> luminance-weighted film grain
// Uniforms are driven every frame by Renderer._applyGrade() from ScreenPulses.values + GRADE_BASE.
// ---------------------------------------------------------------------------------------------------------------
import * as THREE from 'three';

/** Resting grade (broadcast camera look). */
export const GRADE_BASE = Object.freeze({
  vignette: 0.30,      // edge darkening 0..1
  ca: 0.0006,          // resting lens chromatic aberration (uv at the frame edge)
  saturation: 1.06,    // slight broadcast punch (ACES desaturates highlights)
  grain: 0.028,        // sensor noise amplitude (multiplicative)
  flashColor: [1.0, 0.96, 0.9],
});

export const GradeShader = {
  name: 'DodgeballGradeShader',
  uniforms: {
    tDiffuse: { value: null },
    uResolution: { value: new THREE.Vector2(1280, 720) },
    uTime: { value: 0 },
    uExposure: { value: 0 },
    uFlash: { value: 0 },
    uFlashColor: { value: new THREE.Color(1, 0.96, 0.9) },
    uCA: { value: GRADE_BASE.ca },
    uSaturation: { value: GRADE_BASE.saturation },
    uVignette: { value: GRADE_BASE.vignette },
    uDanger: { value: 0 },
    uFreeze: { value: 0 },
    uRewind: { value: 0 },
    uZoom: { value: 0 },
    uGrain: { value: GRADE_BASE.grain },
  },
  vertexShader: /* glsl */`
    varying vec2 vUv;
    void main() {
      vUv = uv;
      gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0);
    }
  `,
  fragmentShader: /* glsl */`
    uniform sampler2D tDiffuse;
    uniform vec2 uResolution;
    uniform float uTime;
    uniform float uExposure;
    uniform float uFlash;
    uniform vec3 uFlashColor;
    uniform float uCA;
    uniform float uSaturation;
    uniform float uVignette;
    uniform float uDanger;
    uniform float uFreeze;
    uniform float uRewind;
    uniform float uZoom;
    uniform float uGrain;
    varying vec2 vUv;

    // Dave Hoskins' hash (stable on mobile GPUs, no sin()).
    float hash12(vec2 p) {
      vec3 p3 = fract(vec3(p.xyx) * 0.1031);
      p3 += dot(p3, p3.yzx + 33.33);
      return fract((p3.x + p3.y) * p3.z);
    }

    void main() {
      float aspect = uResolution.x / max(uResolution.y, 1.0);
      vec2 c0 = vUv - 0.5;
      // Radial punch-in (hit / ultimate impact).
      vec2 uv = 0.5 + c0 * (1.0 - uZoom);

      // Rewind: horizontal tape wobble + tearing lines.
      if (uRewind > 0.001) {
        float line = floor(uv.y * 90.0);
        float tear = hash12(vec2(line, floor(uTime * 24.0))) - 0.5;
        uv.x += (sin(uv.y * 140.0 + uTime * 30.0) * 0.0015 + tear * 0.004) * uRewind;
      }

      // Chromatic aberration grows toward the frame edges (real lens behaviour).
      vec2 d = uv - 0.5;
      vec2 da = d * vec2(aspect, 1.0);
      float r2 = dot(da, da);
      vec2 off = d * uCA * (0.35 + r2 * 2.2);
      vec3 col;
      col.r = texture2D(tDiffuse, uv + off).r;
      col.g = texture2D(tDiffuse, uv).g;
      col.b = texture2D(tDiffuse, uv - off).b;

      // Exposure + flash (linear HDR, tone mapped later by OutputPass).
      col *= 1.0 + uExposure;
      col += uFlashColor * uFlash;

      float lum = dot(col, vec3(0.2126, 0.7152, 0.0722));
      col = max(mix(vec3(lum), col, uSaturation), 0.0);

      float edge = length(c0 * vec2(aspect, 1.0));

      // Freeze: cold grade + crystalline frost creeping in from the edges.
      if (uFreeze > 0.001) {
        vec3 cold = vec3(lum) * vec3(0.78, 0.93, 1.22) + vec3(0.004, 0.012, 0.03);
        col = mix(col, cold, uFreeze * 0.6);
        float crystals = hash12(floor(vUv * uResolution / 3.0));
        float frost = smoothstep(0.42, 0.95, edge * 1.05 + (crystals - 0.5) * 0.14);
        col = mix(col, vec3(0.72, 0.86, 1.0) * max(lum, 0.3) * 1.35, frost * uFreeze * 0.55);
      }

      // Rewind: desaturated, faintly teal, rolling scan lines.
      if (uRewind > 0.001) {
        float scan = 0.9 + 0.1 * sin(vUv.y * uResolution.y * 1.25 - uTime * 12.0);
        vec3 mono = vec3(lum) * vec3(0.93, 1.0, 1.06) * scan;
        col = mix(col, mono, uRewind * 0.85);
      }

      // Danger Sense: red edges beating like a pulse.
      if (uDanger > 0.001) {
        float box = max(abs(c0.x), abs(c0.y)) * 2.0;
        float e = smoothstep(0.55, 1.05, box * 0.55 + edge * 0.6);
        float beat = 0.7 + 0.3 * sin(uTime * 9.0);
        col = mix(col, vec3(0.9, 0.03, 0.02) * (0.35 + lum), clamp(e * uDanger * beat, 0.0, 0.85));
      }

      // Vignette (optical falloff).
      float vig = smoothstep(0.95, 0.28, edge * 0.92);
      col *= mix(1.0, vig, clamp(uVignette, 0.0, 1.0));

      // Film grain, stronger in the shadows like a real sensor.
      float n = hash12(vUv * uResolution + fract(uTime * 7.13) * vec2(113.0, 71.0)) - 0.5;
      col *= 1.0 + n * uGrain * (1.4 - clamp(lum, 0.0, 1.0));

      gl_FragColor = vec4(col, 1.0);
    }
  `,
};
