// Post-processes GLBs produced by FBX2glTF for the web build.
//   node gltf_post.mjs avatar <in.glb> <out.glb>   -> drops placeholder textures/images (the game assigns real PBR maps)
//   node gltf_post.mjs anim   <in.glb> <out.glb>   -> keeps only bone ROTATION tracks + the root (Bip01) translation,
//                                                    so one motion-capture clip retargets onto every Rocketbox avatar
//                                                    without imposing the source skeleton's bone lengths.
import { NodeIO } from '@gltf-transform/core';
import { prune, dedup, resample } from '@gltf-transform/functions';

const [mode, input, output] = process.argv.slice(2);
if (!mode || !input || !output) {
  console.error('usage: node gltf_post.mjs <avatar|anim> <in.glb> <out.glb>');
  process.exit(2);
}

const io = new NodeIO();
const doc = await io.read(input);
const root = doc.getRoot();

if (mode === 'avatar') {
  for (const mat of root.listMaterials()) {
    mat.setBaseColorTexture(null).setNormalTexture(null).setEmissiveTexture(null)
      .setOcclusionTexture(null).setMetallicRoughnessTexture(null);
  }
  for (const tex of root.listTextures()) tex.dispose();
  for (const anim of root.listAnimations()) anim.dispose();
} else if (mode === 'anim') {
  for (const mesh of root.listMeshes()) mesh.dispose();
  for (const anim of root.listAnimations()) {
    for (const ch of anim.listChannels()) {
      const node = ch.getTargetNode();
      const path = ch.getTargetPath();
      const name = node ? node.getName() : '';
      const keep = path === 'rotation' || (path === 'translation' && name === 'Bip01');
      if (!keep) {
        const sampler = ch.getSampler();
        ch.dispose();
        if (sampler && sampler.listParents().filter((p) => p !== root).length === 1) sampler.dispose();
      }
    }
    anim.setName('clip');
  }
  await doc.transform(resample({ tolerance: 1e-4 }));
} else {
  console.error('unknown mode ' + mode);
  process.exit(2);
}

// Avatars: never dedup materials - they are identical once textures are stripped, but the game needs their names.
if (mode === 'anim') await doc.transform(dedup(), prune());
else await doc.transform(prune({ keepLeaves: true, keepAttributes: true }));
await io.write(output, doc);
