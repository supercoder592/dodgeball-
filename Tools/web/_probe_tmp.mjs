import * as THREE from 'three';
import { GLTFLoader } from 'three/examples/jsm/loaders/GLTFLoader.js';
import fs from 'fs';
const loader = new GLTFLoader();
function load(p) { const b = fs.readFileSync(p); return new Promise((res, rej) => loader.parse(b.buffer.slice(b.byteOffset, b.byteOffset + b.byteLength), '', res, rej)); }
const g = await load('../../docs/assets/heroes/rayne/model.glb');
const scene = g.scene; scene.updateMatrixWorld(true);
const names = ['Bip01','Bip01_Pelvis','Bip01_Spine','Bip01_Spine2','Bip01_Neck','Bip01_Head','Bip01_R_UpperArm','Bip01_R_Forearm','Bip01_R_Hand','Bip01_L_Hand','Bip01_R_Finger2','Bip01_R_Thigh','Bip01_R_Calf','Bip01_R_Foot','Bip01_R_Toe0'];
const v = new THREE.Vector3(), q = new THREE.Quaternion();
for (const n of names) { const o = scene.getObjectByName(n); if (!o) { console.log('missing', n); continue; } o.getWorldPosition(v); o.getWorldQuaternion(q);
  const x = new THREE.Vector3(1,0,0).applyQuaternion(q), y = new THREE.Vector3(0,1,0).applyQuaternion(q), z = new THREE.Vector3(0,0,1).applyQuaternion(q);
  console.log(n.padEnd(18), 'pos', v.toArray().map(a=>a.toFixed(3)).join(','), ' X', x.toArray().map(a=>a.toFixed(2)).join(','), ' Y', y.toArray().map(a=>a.toFixed(2)).join(','), ' Z', z.toArray().map(a=>a.toFixed(2)).join(','));
}
scene.traverse(o => { if (o.isSkinnedMesh) { console.log('skinned', o.name, o.material.name||o.material.map?.(m=>m.name), 'bindMode', o.bindMode, 'bones', o.skeleton.bones.length); o.geometry.computeBoundingBox(); console.log(o.geometry.boundingBox); console.log(o.geometry.groups, Object.keys(o.geometry.attributes)); } if (o.isMesh && !o.isSkinnedMesh) console.log('mesh', o.name); if (o.isGroup||o.type==='Object3D') {} });
scene.traverse(o=>{ if (o.isMesh) console.log('meshnode', o.name, o.type, Array.isArray(o.material)?o.material.map(m=>m.name):o.material.name, o.parent.name); });
const fingers = []; scene.traverse(o => { if (/Finger/.test(o.name)) fingers.push(o.name); }); console.log(fingers.join(' '));
for (const k of ['idle','walk','run','sprint','crouch','stunned','cheer','wave','defeat','breathe','lookaround']) {
  const a = await load(`../../docs/assets/anims/m/${k}.glb`);
  const c = a.animations[0];
  const pos = c.tracks.filter(t => t.name.endsWith('.position'));
  let info = '';
  for (const t of pos) { const v = t.values; const n = v.length/3; info += `${t.name} n=${n} first=(${v[0].toFixed(3)},${v[1].toFixed(3)},${v[2].toFixed(3)}) last=(${v[v.length-3].toFixed(3)},${v[v.length-2].toFixed(3)},${v[v.length-1].toFixed(3)}) `; }
  console.log(k, 'dur', c.duration.toFixed(3), 'tracks', c.tracks.length, info, c.tracks.slice(0,4).map(t=>t.name).join('|'));
}
