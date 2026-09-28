import * as THREE from '/home/user/dodgeball-/Tools/web/node_modules/three/build/three.module.js';
import { GLTFLoader } from '/home/user/dodgeball-/Tools/web/node_modules/three/examples/jsm/loaders/GLTFLoader.js';
import fs from 'fs';
const loader = new GLTFLoader();
const A = '/home/user/dodgeball-/docs/assets/';
function load(p) { const b = fs.readFileSync(A + p); return new Promise((res, rej) => loader.parse(b.buffer.slice(b.byteOffset, b.byteOffset + b.byteLength), '', res, rej)); }
const hero = process.argv[2] || 'rayne';
const g = await load(`heroes/${hero}/model.glb`);
const scene = g.scene; scene.updateMatrixWorld(true);
const tree = []; const walk = (o, d) => { tree.push('  '.repeat(d) + o.type + ' ' + o.name + (o.isBone ? '' : '') + ' s=' + o.scale.toArray().map(a=>+a.toFixed(3)) + ' r=' + o.quaternion.toArray().map(a=>+a.toFixed(3)) + ' p=' + o.position.toArray().map(a=>+a.toFixed(3))); if (d < 4 || !o.isBone) for (const c of o.children) walk(c, d + 1); };
walk(scene, 0); console.log(tree.slice(0, 40).join('\n'));
const names = ['Bip01','Bip01_Pelvis','Bip01_Spine','Bip01_Spine1','Bip01_Spine2','Bip01_Neck','Bip01_Head','Bip01_HeadNub','Bip01_R_Clavicle','Bip01_R_UpperArm','Bip01_R_Forearm','Bip01_R_Hand','Bip01_L_UpperArm','Bip01_L_Forearm','Bip01_L_Hand','Bip01_R_Finger2','Bip01_R_Finger21','Bip01_R_Thigh','Bip01_R_Calf','Bip01_R_Foot','Bip01_R_Toe0','Bip01_L_Thigh','Bip01_L_Calf','Bip01_L_Foot'];
const v = new THREE.Vector3(), q = new THREE.Quaternion();
for (const n of names) { const o = scene.getObjectByName(n); if (!o) { console.log('missing', n); continue; } o.getWorldPosition(v); o.getWorldQuaternion(q);
  const x = new THREE.Vector3(1,0,0).applyQuaternion(q), y = new THREE.Vector3(0,1,0).applyQuaternion(q), z = new THREE.Vector3(0,0,1).applyQuaternion(q);
  console.log(n.padEnd(18), 'pos', v.toArray().map(a=>a.toFixed(3)).join(','), ' X', x.toArray().map(a=>a.toFixed(2)).join(','), ' Y', y.toArray().map(a=>a.toFixed(2)).join(','), ' Z', z.toArray().map(a=>a.toFixed(2)).join(','), 'parent', o.parent.name);
}
scene.traverse(o=>{ if (o.isMesh) { console.log('meshnode', o.name, o.type, Array.isArray(o.material)?o.material.map(m=>m.name):o.material.name, 'parent', o.parent.name, 'bindMode', o.bindMode, o.skeleton && o.skeleton.bones.length); o.geometry.computeBoundingBox(); console.log('  bbox', o.geometry.boundingBox.min.toArray().map(a=>+a.toFixed(2)), o.geometry.boundingBox.max.toArray().map(a=>+a.toFixed(2)), 'groups', o.geometry.groups.length, Object.keys(o.geometry.attributes).join(',')); }});
let bones = []; scene.traverse(o => { if (o.isBone) bones.push(o.name); }); console.log(bones.length, bones.join(' '));
