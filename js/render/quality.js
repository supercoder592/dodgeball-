// ---------------------------------------------------------------------------------------------------------------
// Render quality presets (owner: render). Shared by the Renderer (post stack, pixel ratio) and the Arena (shadow
// casters, shadow map size, texture resolution, fill lights). Pure data - no three.js import so tests can use it.
// ---------------------------------------------------------------------------------------------------------------

/**
 * @typedef {Object} QualityPreset
 * @property {number}  maxPixelRatio   cap for window.devicePixelRatio
 * @property {boolean} ao              GTAO ambient occlusion pass
 * @property {number}  aoScale         AO buffer resolution relative to the frame (AO is low-frequency)
 * @property {number}  aoSamples       GTAO horizon samples
 * @property {boolean} bloom           UnrealBloom pass (subtle, high threshold)
 * @property {'smaa'|'fxaa'|'none'} aa post anti-aliasing
 * @property {number}  shadowLights    how many of the key floodlights cast shadows
 * @property {number}  shadowMapSize   shadow map resolution per casting light
 * @property {number}  shadowRadius    PCF (vogel disk) softening radius in texels
 * @property {boolean} fillLights      extra non-shadow spots for the bleachers / end walls
 * @property {number}  texScale        procedural texture resolution multiplier (1 = 2048 hardwood tile)
 * @property {number}  anisotropy      max anisotropic filtering for the floor
 * @property {number}  crowdCell       crowd impostor atlas cell width in px (height = 2x)
 * @property {boolean} clearcoat       physical clear-coat varnish on the hardwood (MeshPhysicalMaterial)
 */

/** @type {Record<'low'|'medium'|'high', QualityPreset>} */
export const QUALITY_PRESETS = Object.freeze({
  low: Object.freeze({
    maxPixelRatio: 1, ao: false, aoScale: 0.5, aoSamples: 8, bloom: false, aa: 'fxaa',
    shadowLights: 2, shadowMapSize: 1024, shadowRadius: 2.5, fillLights: false,
    texScale: 0.5, anisotropy: 4, crowdCell: 64, clearcoat: false,
  }),
  medium: Object.freeze({
    maxPixelRatio: 1.25, ao: true, aoScale: 0.5, aoSamples: 8, bloom: true, aa: 'fxaa',
    shadowLights: 4, shadowMapSize: 1024, shadowRadius: 3, fillLights: true,
    texScale: 0.75, anisotropy: 8, crowdCell: 96, clearcoat: true,
  }),
  high: Object.freeze({
    maxPixelRatio: 2, ao: true, aoScale: 0.5, aoSamples: 16, bloom: true, aa: 'smaa',
    shadowLights: 4, shadowMapSize: 2048, shadowRadius: 3.5, fillLights: true,
    texScale: 1, anisotropy: 16, crowdCell: 128, clearcoat: true,
  }),
});

/** Normalises any string to a valid quality id (falls back to 'high'). */
export function qualityId(q) {
  return q === 'low' || q === 'medium' || q === 'high' ? q : 'high';
}

/** Preset for a quality id (invalid ids fall back to 'high'). */
export function qualityPreset(q) {
  return QUALITY_PRESETS[qualityId(q)];
}
