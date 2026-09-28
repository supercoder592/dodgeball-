// ---------------------------------------------------------------------------------------------------------------
// Module worker (owner: fx): renders the procedural sound bank off the main thread so boot and gameplay never hitch.
// In:  { ids: string[] }   (priority order)
// Out: { id, sr, variants: Float32Array[][] } per sound (buffers transferred, zero copy), then { done: true }.
// ---------------------------------------------------------------------------------------------------------------
import { SOUNDS, renderSound } from './bank.js';

self.onmessage = (e) => {
  const ids = (e.data && e.data.ids) || Object.keys(SOUNDS);
  for (const id of ids) {
    if (!SOUNDS[id]) continue;
    try {
      const { sr, variants } = renderSound(id);
      const transfer = [];
      const out = variants.map((v) => {
        const chans = Array.isArray(v) ? v : [v];
        for (const c of chans) transfer.push(c.buffer);
        return chans;
      });
      self.postMessage({ id, sr, variants: out }, transfer);
    } catch (err) {
      self.postMessage({ id, error: String(err && err.message || err) });
    }
  }
  self.postMessage({ done: true });
};
