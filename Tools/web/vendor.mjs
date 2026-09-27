// Copies the runtime libraries used by the web build into docs/vendor/ (no bundler: native ES modules + import map).
//   node Tools/web/vendor.mjs
import fs from 'fs';
import path from 'path';
import { fileURLToPath } from 'url';

const here = path.dirname(fileURLToPath(import.meta.url));
const nm = path.join(here, 'node_modules');
const out = path.join(here, '..', '..', 'docs', 'vendor');

function copy(src, dst) {
  fs.mkdirSync(path.dirname(dst), { recursive: true });
  fs.cpSync(src, dst, { recursive: true });
}

fs.rmSync(out, { recursive: true, force: true });
const three = path.join(nm, 'three');
for (const f of ['three.module.js', 'three.core.js']) copy(path.join(three, 'build', f), path.join(out, 'three', 'build', f));
for (const dir of ['postprocessing', 'shaders', 'utils', 'environments', 'math', 'csm', 'objects', 'geometries', 'lines', 'animation', 'misc', 'curves', 'modifiers', 'effects', 'lights', 'helpers']) {
  copy(path.join(three, 'examples', 'jsm', dir), path.join(out, 'three', 'examples', 'jsm', dir));
}
for (const f of ['GLTFLoader.js', 'RGBELoader.js', 'HDRLoader.js', 'EXRLoader.js']) {
  const src = path.join(three, 'examples', 'jsm', 'loaders', f);
  if (fs.existsSync(src)) copy(src, path.join(out, 'three', 'examples', 'jsm', 'loaders', f));
}
copy(path.join(three, 'examples', 'jsm', 'libs', 'fflate.module.js'), path.join(out, 'three', 'examples', 'jsm', 'libs', 'fflate.module.js'));
copy(path.join(three, 'LICENSE'), path.join(out, 'three', 'LICENSE'));
copy(path.join(nm, 'cannon-es', 'dist', 'cannon-es.js'), path.join(out, 'cannon-es', 'cannon-es.js'));
copy(path.join(nm, 'cannon-es', 'LICENSE'), path.join(out, 'cannon-es', 'LICENSE'));
const version = JSON.parse(fs.readFileSync(path.join(three, 'package.json'), 'utf8')).version;
fs.writeFileSync(path.join(out, 'VERSIONS.txt'), `three ${version}\ncannon-es ${JSON.parse(fs.readFileSync(path.join(nm, 'cannon-es', 'package.json'), 'utf8')).version}\n`);
console.log('vendored into', out);
