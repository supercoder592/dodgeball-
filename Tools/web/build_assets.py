#!/usr/bin/env python3
"""
Builds the realistic-human assets for the Dodgeball Ultra WEB build (docs/assets/).

  python3 Tools/web/build_assets.py            # all ten heroes + both animation sets
  python3 Tools/web/build_assets.py --heroes Rayne,Elsa

Pipeline (Microsoft Rocketbox, MIT, pinned by Tools/rocketbox_manifest.json):
  download FBX + TGA  ->  FBX2glTF (skinned mesh / animation)  ->  gltf_post.mjs (strip placeholder textures,
  keep rotation tracks + root translation so clips retarget onto every avatar)  ->  textures resized to 1024 px WebP
  (colour, normal, ORM built from the specular map, hair opacity)  ->  docs/assets/manifest.json

Requires: python3 + Pillow, node + `npm install` in Tools/web.
"""
import argparse
import concurrent.futures as cf
import json
import os
import shutil
import subprocess
import sys
import time
import urllib.request

from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))
MANIFEST = os.path.join(REPO, "Tools", "rocketbox_manifest.json")
CACHE = os.path.join(HERE, ".cache")
OUT = os.path.join(REPO, "docs", "assets")
FBX2GLTF = os.path.join(HERE, "node_modules", "fbx2gltf", "bin", "Linux", "FBX2glTF")

# Hero -> Rocketbox avatar casting (same as the Unity build's HeroRosterFactory).
HEROES = {
    "Rayne": ("Sports_Male_02", "m"),
    "Shadow": ("Security_Male_01", "m"),
    "Gale": ("Sports_Female_02", "f"),
    "Bear": ("Fire_Male_02", "m"),
    "Gouki": ("Military_Male_01", "m"),
    "Screws": ("Construction_Male_01", "m"),
    "Houdini": ("Business_Male_01", "m"),
    "Elsa": ("Pilot_Female_01", "f"),
    "Specter": ("Sports_Male_04", "m"),
    "Chrono": ("Military_Female_01", "f"),
}

# Semantic clip -> (Rocketbox folder suffix, file stem without gender prefix, loop)
CLIPS = {
    "idle": ("static", "idle_neutral_01", True),
    "breathe": ("static", "idle_breathe_01", True),
    "lookaround": ("static", "idle_look_around_01", True),
    "walk": ("xy", "walk_neutral_01", True),
    "run": ("xy", "run_neutral_01", True),
    "sprint": ("xy", "run_fast_01", True),
    "crouch": ("static", "crouch_idle", True),
    "stunned": ("static", "idle_drunk_01", True),
    "cheer": ("static", "cheer_01", False),
    "wave": ("static", "wave_01", False),
    "defeat": ("static", "gestic_shrug_01", False),
}

TEX_SIZE = 1024


def log(msg):
    print(msg, flush=True)


def download(url, dest, retries=4):
    if os.path.exists(dest) and os.path.getsize(dest) > 0:
        return dest
    os.makedirs(os.path.dirname(dest), exist_ok=True)
    for attempt in range(retries):
        try:
            tmp = dest + ".part"
            with urllib.request.urlopen(url, timeout=120) as r, open(tmp, "wb") as f:
                shutil.copyfileobj(r, f, 1 << 20)
            os.replace(tmp, dest)
            return dest
        except Exception as e:  # noqa: BLE001
            if attempt == retries - 1:
                raise RuntimeError(f"download failed: {url}: {e}")
            time.sleep(2 ** (attempt + 1))
    return dest


def run(cmd):
    p = subprocess.run(cmd, capture_output=True, text=True)
    if p.returncode != 0:
        raise RuntimeError(f"command failed ({p.returncode}): {' '.join(cmd)}\n{p.stdout}\n{p.stderr}")
    return p.stdout


def fbx_to_glb(fbx, glb_out, mode):
    tmp_base = glb_out + ".raw"
    run([FBX2GLTF, "--binary", "--input", fbx, "--output", tmp_base])
    raw = tmp_base + ".glb"
    run(["node", os.path.join(HERE, "gltf_post.mjs"), mode, raw, glb_out])
    os.remove(raw)


def save_webp(img, path, quality, lossless=False):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    img.save(path, "WEBP", quality=quality, method=6, lossless=lossless)


def convert_color(src, dst):
    im = Image.open(src).convert("RGB").resize((TEX_SIZE, TEX_SIZE), Image.LANCZOS)
    save_webp(im, dst, 88)


def convert_normal(src, dst):
    im = Image.open(src).convert("RGB").resize((TEX_SIZE, TEX_SIZE), Image.LANCZOS)
    save_webp(im, dst, 95)


def convert_orm(spec_src, dst, kind):
    """ORM texture for three.js: R = ambient occlusion (1), G = roughness, B = metalness (0).
    Rocketbox ships 3ds Max specular-level maps; brighter = shinier. Skin keeps a soft sheen range."""
    spec = Image.open(spec_src).convert("L").resize((512, 512), Image.LANCZOS)
    lo, hi = (0.42, 0.78) if kind == "head" else (0.48, 0.95)
    lut = []
    for v in range(256):
        s = min(1.0, (v / 255.0) * 2.2)          # expand the (dark) specular-level range
        rough = hi - (hi - lo) * s
        lut.append(int(round(max(0.0, min(1.0, rough)) * 255)))
    g = spec.point(lut)
    r = Image.new("L", g.size, 255)
    b = Image.new("L", g.size, 0)
    save_webp(Image.merge("RGB", (r, g, b)), dst, 90)


def convert_opacity(src, dst):
    im = Image.open(src).convert("RGBA").resize((TEX_SIZE, TEX_SIZE), Image.LANCZOS)
    save_webp(im, dst, 90)


def make_portrait(src, dst):
    """Crops head-and-shoulders from Rocketbox's full-body T-pose render (black background)."""
    im = Image.open(src).convert("RGB")
    bbox = im.convert("L").point(lambda v: 255 if v > 12 else 0).getbbox() or (0, 0, im.width, im.height)
    x0, y0, x1, y1 = bbox
    fig_h = y1 - y0
    side = int(fig_h * 0.30)
    cx = (x0 + x1) // 2
    box = (max(0, cx - side // 2), max(0, y0 - int(fig_h * 0.02)), 0, 0)
    box = (box[0], box[1], min(im.width, box[0] + side), min(im.height, box[1] + side))
    save_webp(im.crop(box).resize((384, 384), Image.LANCZOS), dst, 88)


def build_hero(hero, avatar_name, gender, man):
    a = man["avatars"][avatar_name]
    base = man["rawBaseUrl"] + a["path"] + "/"
    cache = os.path.join(CACHE, "avatars", avatar_name)
    files = [a["model"], a["portrait"]] + a["textures"]
    for rel in files:
        download(base + rel, os.path.join(cache, rel))
    out = os.path.join(OUT, "heroes", hero.lower())
    os.makedirs(out, exist_ok=True)
    fbx_to_glb(os.path.join(cache, a["model"]), os.path.join(out, "model.glb"), "avatar")

    # Texture names look like <id>_<part>_<color|normal|specular>[_<variant>].tga, e.g. sm002_body_color_acu.tga,
    # m111_tools_normal.tga. Group them per part; the FBX material for a part is named <id>_<part>.
    parts = {}
    for t in a["textures"]:
        stem = os.path.basename(t)[:-4]
        tokens = stem.split("_")
        for i, tok in enumerate(tokens):
            if tok in ("color", "normal", "specular") and i >= 2:
                tex_id, part = tokens[0], "_".join(tokens[1:i])
                parts.setdefault((tex_id, part), {})[tok] = os.path.join(cache, t)
                break
    mats = {}
    for (tex_id, part), maps in sorted(parts.items()):
        kind = "skin" if part == "head" else "hair" if part == "opacity" else "gear" if part in ("helmet", "equipment", "tools", "combat_knife", "hat") else "body"
        entry = {"kind": kind}
        if "color" in maps:
            name = f"{part}_color.webp"
            (convert_opacity if kind == "hair" else convert_color)(maps["color"], os.path.join(out, name))
            entry["map"] = name
        if "normal" in maps:
            name = f"{part}_normal.webp"
            convert_normal(maps["normal"], os.path.join(out, name))
            entry["normalMap"] = name
        if "specular" in maps:
            name = f"{part}_orm.webp"
            convert_orm(maps["specular"], os.path.join(out, name), "head" if kind == "skin" else part)
            entry["ormMap"] = name
        if kind == "hair":
            entry["alphaTest"] = 0.35
        mats[f"{tex_id}_{part}"] = entry
    make_portrait(os.path.join(cache, a["portrait"]), os.path.join(out, "portrait.webp"))
    return {
        "avatar": avatar_name,
        "gender": "female" if gender == "f" else "male",
        "folder": f"heroes/{hero.lower()}/",
        "model": "model.glb",
        "portrait": "portrait.webp",
        "materials": mats,
    }


def build_clip(gender, key, man):
    folder, stem, loop = CLIPS[key]
    rel = f"Assets/Animations/all_animations_max_motextr_{folder}/{gender}_{stem}.max.fbx"
    if rel not in man["animations"]:
        raise RuntimeError(f"missing animation {rel}")
    fbx = download(man["rawBaseUrl"] + rel, os.path.join(CACHE, "anims", os.path.basename(rel)))
    out = os.path.join(OUT, "anims", gender, f"{key}.glb")
    os.makedirs(os.path.dirname(out), exist_ok=True)
    fbx_to_glb(fbx, out, "anim")
    return {"file": f"anims/{gender}/{key}.glb", "loop": loop, "source": os.path.basename(rel)}


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--heroes", default="all", help="comma list or 'all'")
    ap.add_argument("--skip-anims", action="store_true")
    args = ap.parse_args()

    if not os.path.exists(FBX2GLTF):
        sys.exit("FBX2glTF missing: run `npm install` in Tools/web first")
    man = json.load(open(MANIFEST, encoding="utf-8"))
    heroes = list(HEROES) if args.heroes == "all" else [h.strip() for h in args.heroes.split(",") if h.strip()]

    manifest_path = os.path.join(OUT, "manifest.json")
    out_man = json.load(open(manifest_path)) if os.path.exists(manifest_path) else {"heroes": {}, "clips": {"m": {}, "f": {}}}
    out_man["source"] = man["source"]
    out_man["license"] = man["license"]
    out_man["commit"] = man["commit"]

    with cf.ThreadPoolExecutor(max_workers=4) as ex:
        futs = {ex.submit(build_hero, h, HEROES[h][0], HEROES[h][1], man): h for h in heroes}
        if not args.skip_anims:
            for g in ("m", "f"):
                for key in CLIPS:
                    futs[ex.submit(build_clip, g, key, man)] = (g, key)
        for fut in cf.as_completed(futs):
            tag = futs[fut]
            res = fut.result()
            if isinstance(tag, tuple):
                out_man["clips"].setdefault(tag[0], {})[tag[1]] = res
                log(f"clip {tag[0]}/{tag[1]} <- {res['source']}")
            else:
                out_man["heroes"][tag] = res
                log(f"hero {tag} <- {res['avatar']}")

    os.makedirs(OUT, exist_ok=True)
    with open(manifest_path, "w", encoding="utf-8") as f:
        json.dump(out_man, f, indent=1, sort_keys=True)
    lic = os.path.join(OUT, "LICENSE-Rocketbox.txt")
    download(man["rawBaseUrl"] + "LICENSE.md", lic)
    log("done -> " + OUT)


if __name__ == "__main__":
    main()
