// Post-processes GLBs produced by FBX2glTF for the web build.
//   node gltf_post.mjs avatar <in.glb> <out.glb>   -> drops placeholder textures/images (the game assigns real PBR maps)
//                                                    and the unused COLOR_0 vertex colours
//   node gltf_post.mjs anim   <in.glb> <out.glb>   -> keeps only bone ROTATION tracks + the root (Bip01) translation,
//                                                    so one motion-capture clip retargets onto every Rocketbox avatar
//                                                    without imposing the source skeleton's bone lengths; removes
//                                                    redundant keys (resample)
//   add --raw to skip compression (debugging).
//
// Both modes then compress with EXT_meshopt_compression (the runtime GLTFLoader needs
// three/addons/libs/meshopt_decoder.module.js via setMeshoptDecoder):
//   - avatars: vertex reorder, TEXCOORD 12 bit / WEIGHTS 8 bit quantization (KHR_mesh_quantization), NORMAL 8 bit
//     octahedral filter. POSITION stays float32 on purpose: a quantized skinned mesh keeps its vertices in a
//     [-1, 1] grid and moves the dequantization into the inverse bind matrices, which would break code that measures
//     the bind-pose geometry (Avatar._computeScale uses geometry.boundingBox).
//   - clips: rotation outputs 16 bit quaternion filter, translation 12 bit exponential filter (the root / pelvis
//     translation is kept), key times float32.
// Node / bone names are never touched (three sanitizes "Bip01 R Hand" to Bip01_R_Hand; the game relies on them).
// An input that is already meshopt-compressed is copied unchanged, so the step is idempotent on docs/assets.
import fs from 'fs';
import { NodeIO } from '@gltf-transform/core';
import { ALL_EXTENSIONS, EXTMeshoptCompression } from '@gltf-transform/extensions';
import { prune, dedup, resample, reorder, quantize } from '@gltf-transform/functions';
import { MeshoptDecoder, MeshoptEncoder } from 'meshoptimizer';

const args = process.argv.slice(2);
const raw = args.includes('--raw');
const [mode, input, output] = args.filter((a) => !a.startsWith('--'));
if (!mode || !input || !output || !['avatar', 'anim'].includes(mode)) {
  console.error('usage: node gltf_post.mjs <avatar|anim> <in.glb> <out.glb> [--raw]');
  process.exit(2);
}

await Promise.all([MeshoptDecoder.ready, MeshoptEncoder.ready]);
const io = new NodeIO().registerExtensions(ALL_EXTENSIONS)
  .registerDependencies({ 'meshopt.decoder': MeshoptDecoder, 'meshopt.encoder': MeshoptEncoder });
const doc = await io.read(input);
const root = doc.getRoot();

if (root.listExtensionsUsed().some((e) => e.extensionName === 'EXT_meshopt_compression')) {
  if (input !== output) fs.copyFileSync(input, output);
  console.log(`${output}: already meshopt-compressed, unchanged`);
  process.exit(0);
}

if (mode === 'avatar') {
  for (const mat of root.listMaterials()) {
    mat.setBaseColorTexture(null).setNormalTexture(null).setEmissiveTexture(null)
      .setOcclusionTexture(null).setMetallicRoughnessTexture(null);
  }
  for (const tex of root.listTextures()) tex.dispose();
  for (const anim of root.listAnimations()) anim.dispose();
  // FBX2glTF exports the 3ds Max vertex colours; no material in the game enables vertexColors.
  for (const mesh of root.listMeshes()) {
    for (const prim of mesh.listPrimitives()) {
      for (const sem of prim.listSemantics()) if (sem.startsWith('COLOR_')) prim.setAttribute(sem, null);
    }
  }
} else {
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
}

// Avatars: never dedup materials - they are identical once textures are stripped, but the game needs their names.
if (mode === 'anim') await doc.transform(dedup(), prune());
else await doc.transform(prune({ keepLeaves: true, keepAttributes: true }));

if (!raw) {
  if (mode === 'avatar') {
    await doc.transform(
      reorder({ encoder: MeshoptEncoder, target: 'size' }),
      quantize({ pattern: /^(TEXCOORD|JOINTS|WEIGHTS)(_\d+)?$/, quantizeTexcoord: 12, quantizeWeight: 8, cleanup: false }),
    );
  }
  doc.createExtension(EXTMeshoptCompression).setRequired(true)
    .setEncoderOptions({ method: EXTMeshoptCompression.EncoderMethod.FILTER });
}
await io.write(output, doc);
