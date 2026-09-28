#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
fetch_rocketbox.py - command-line downloader for the Microsoft Rocketbox avatars and motion-capture clips used by
Dodgeball Ultra (the realistic human characters, MIT licensed).

It performs exactly the same download as the Unity Setup Wizard (``CharacterPipeline.DownloadAsync``):

* every file is fetched from the *pinned* commit recorded in ``Tools/rocketbox_manifest.json`` (``rawBaseUrl``), so
  the art never changes underneath the project;
* avatars land in ``<dest>/Avatars/<Name>/{Export,Textures}/...`` plus the portrait ``<dest>/Avatars/<Name>/<Name>.png``;
* the curated motion-capture set lands in ``<dest>/Animations/<file>`` (see ``CURATED_ANIMATIONS``);
* ``<dest>/LICENSE-Rocketbox.txt`` (MIT, Copyright (c) 2020 Microsoft) is written next to them;
* files that already exist are skipped (resume); partial downloads go to ``*.part`` and are only renamed when complete;
* transient failures are retried with exponential backoff + jitter.

Only the Python 3 standard library is used (urllib, json, argparse, concurrent.futures).

Examples::

    python3 Tools/fetch_rocketbox.py --avatars all-heroes --animations
    python3 Tools/fetch_rocketbox.py --avatars Sports_Male_02,Pilot_Female_01 --dest /tmp/rocketbox
    python3 Tools/fetch_rocketbox.py --avatars all-heroes --animations --dry-run
    python3 Tools/fetch_rocketbox.py --list

Keep ``HERO_AVATARS`` / ``CURATED_ANIMATIONS`` in sync with
``Assets/DodgeballUltra/Scripts/Editor/Characters/RocketboxAssetSet.cs``.
"""

from __future__ import annotations

import argparse
import concurrent.futures
import json
import os
import random
import ssl
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import dataclass
from typing import Dict, List, Optional, Sequence, Tuple

# ---------------------------------------------------------------------------------------------------------------------
# Data shared with the Unity pipeline (RocketboxAssetSet.cs)
# ---------------------------------------------------------------------------------------------------------------------

#: Hero -> Rocketbox avatar casting (HeroRosterFactory.GetDefaultAvatar).
HERO_AVATARS: Dict[str, str] = {
    "Rayne": "Sports_Male_02",
    "Shadow": "Security_Male_01",
    "Gale": "Sports_Female_02",
    "Bear": "Fire_Male_02",
    "Gouki": "Military_Male_01",
    "Screws": "Construction_Male_01",
    "Houdini": "Business_Male_01",
    "Elsa": "Pilot_Female_01",
    "Specter": "Sports_Male_04",
    "Chrono": "Military_Female_01",
}

#: Curated motion-capture clips, as base names without the ``m_`` / ``f_`` prefix and the ``.max.fbx`` suffix.
#: Each one is downloaded for BOTH the male (``m_``) and the female (``f_``) skeleton. Usage in the generated
#: AnimatorControllers (DU_Male / DU_Female):
#:
#: ================================  ==============================================================
#: clip                              animator usage
#: ================================  ==============================================================
#: idle_neutral_01   (static)        Locomotion blend tree, Speed 0 m/s (athletic standing idle)
#: walk_neutral_01   (xy)            Locomotion blend tree, Speed 1.6 m/s
#: run_neutral_01    (xy)            Locomotion blend tree, Speed 4.6 m/s
#: run_fast_01       (xy)            Locomotion blend tree, Speed 7.4 m/s (sprint)
#: crouch_idle       (static)        Crouch (slides + catch stance) and Airborne (tucked jump)
#: idle_drunk_01     (static)        Stunned (dizzy sway)
#: cheer_01          (static)        Cheer (round / match won)
#: gestic_shrug_01   (static)        Defeat (round / match lost)
#: ================================  ==============================================================
CURATED_ANIMATIONS: Tuple[str, ...] = (
    "idle_neutral_01",
    "walk_neutral_01",
    "run_neutral_01",
    "run_fast_01",
    "crouch_idle",
    "idle_drunk_01",
    "cheer_01",
    "gestic_shrug_01",
)

GENDER_PREFIXES: Tuple[str, ...] = ("m_", "f_")
ANIMATION_SUFFIX = ".max.fbx"

#: Only real image textures are fetched: the Rocketbox repo also stores Unity ``.mat`` / ``.shader`` files inside some
#: Textures folders (they reference GUIDs that do not exist here and would not compile under HDRP).
TEXTURE_EXTENSIONS = (".tga", ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".psd", ".exr")
#: Wrinkle normal maps (``*_normal_wrinkle.tga``, ``* - wr.tga``) are for Rocketbox's facial-animation shader only; our
#: materials never sample them.
SKIPPED_TEXTURE_MARKERS = ("_wrinkle", " - wr")

LICENSE_FILE_NAME = "LICENSE-Rocketbox.txt"
LICENSE_TEXT = """Microsoft Rocketbox Avatar Library
https://github.com/microsoft/Microsoft-Rocketbox

The avatars, textures and motion-capture animations in this folder were downloaded from the repository above
(pinned commit {commit}) and are distributed under the MIT License:

MIT License

Copyright (c) 2020 Microsoft

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

Citation requested by the authors for research use:
M. Gonzalez-Franco et al., "The Rocketbox library and the utility of freely available rigged avatars",
Frontiers in Virtual Reality, 2020. DOI 10.3389/frvir.2020.561558
"""

USER_AGENT = "DodgeballUltra-fetch_rocketbox/1.0 (+https://github.com/microsoft/Microsoft-Rocketbox)"
CHUNK_SIZE = 1 << 16


# ---------------------------------------------------------------------------------------------------------------------
# Plan
# ---------------------------------------------------------------------------------------------------------------------

@dataclass(frozen=True)
class DownloadItem:
    """One file to fetch: repository-relative source path and destination path relative to ``--dest``."""

    source: str
    target: str


class ManifestError(Exception):
    """The manifest is missing, malformed or does not contain a requested entry."""


def load_manifest(path: str) -> dict:
    try:
        with open(path, "r", encoding="utf-8") as handle:
            manifest = json.load(handle)
    except FileNotFoundError as exc:
        raise ManifestError(f"manifest not found: {path}") from exc
    except json.JSONDecodeError as exc:
        raise ManifestError(f"manifest is not valid JSON ({path}): {exc}") from exc
    for key in ("rawBaseUrl", "avatars", "animations"):
        if key not in manifest:
            raise ManifestError(f"manifest {path} has no '{key}' entry")
    if not manifest["rawBaseUrl"].endswith("/"):
        manifest["rawBaseUrl"] += "/"
    return manifest


def is_wanted_texture(relative_path: str) -> bool:
    lower = relative_path.lower()
    if not lower.endswith(TEXTURE_EXTENSIONS):
        return False
    return not any(marker in lower for marker in SKIPPED_TEXTURE_MARKERS)


def resolve_avatar_names(spec: str, manifest: dict) -> List[str]:
    """Turns ``all-heroes`` / ``all`` / ``none`` / ``A,B,C`` into a list of avatar folder names (validated)."""
    spec = (spec or "").strip()
    if spec in ("", "none"):
        return []
    if spec == "all-heroes":
        names = list(dict.fromkeys(HERO_AVATARS.values()))
    elif spec == "all":
        names = sorted(manifest["avatars"].keys())
    else:
        names = []
        for raw in spec.split(","):
            raw = raw.strip()
            if not raw:
                continue
            # Allow hero names too ("Rayne" -> Sports_Male_02).
            names.append(HERO_AVATARS.get(raw, raw))
        names = list(dict.fromkeys(names))
    unknown = [n for n in names if n not in manifest["avatars"]]
    if unknown:
        raise ManifestError("unknown avatar(s): " + ", ".join(unknown) + " (use --list to see the manifest)")
    return names


def avatar_items(name: str, manifest: dict, include_portrait: bool = True) -> List[DownloadItem]:
    entry = manifest["avatars"][name]
    base = entry["path"].rstrip("/")
    items = [DownloadItem(f"{base}/{entry['model']}", f"Avatars/{name}/{entry['model']}")]
    for tex in entry.get("textures", []):
        if is_wanted_texture(tex):
            items.append(DownloadItem(f"{base}/{tex}", f"Avatars/{name}/{tex}"))
    portrait = entry.get("portrait")
    if include_portrait and portrait:
        items.append(DownloadItem(f"{base}/{portrait}", f"Avatars/{name}/{portrait}"))
    return items


def curated_animation_files() -> List[str]:
    return [prefix + base + ANIMATION_SUFFIX for base in CURATED_ANIMATIONS for prefix in GENDER_PREFIXES]


def animation_items(file_names: Sequence[str], manifest: dict) -> List[DownloadItem]:
    """Maps animation file names to their manifest paths. Raises when a curated file is not in the manifest."""
    by_file: Dict[str, str] = {}
    for path in manifest["animations"]:
        file_name = path.rsplit("/", 1)[-1]
        # The first occurrence wins: the manifest lists the in-place ("static") folder before the xy / xyz folders.
        by_file.setdefault(file_name, path)
    items: List[DownloadItem] = []
    missing: List[str] = []
    for file_name in file_names:
        if file_name.lower().endswith("_facial.fbx"):
            continue  # facial rigs are not used
        source = by_file.get(file_name)
        if source is None:
            missing.append(file_name)
            continue
        items.append(DownloadItem(source, f"Animations/{file_name}"))
    if missing:
        raise ManifestError("animation(s) not in manifest: " + ", ".join(missing))
    return items


def build_plan(args: argparse.Namespace, manifest: dict) -> List[DownloadItem]:
    plan: List[DownloadItem] = []
    for name in resolve_avatar_names(args.avatars, manifest):
        plan.extend(avatar_items(name, manifest, include_portrait=not args.no_portraits))
    extra = [f.strip() for f in (args.extra_animations or "").split(",") if f.strip()]
    extra = [f if f.lower().endswith(".fbx") else f + ANIMATION_SUFFIX for f in extra]
    anim_files = (curated_animation_files() if args.animations else []) + extra
    if anim_files:
        plan.extend(animation_items(list(dict.fromkeys(anim_files)), manifest))
    # De-duplicate while keeping order (two heroes may share an avatar).
    seen = set()
    unique: List[DownloadItem] = []
    for item in plan:
        if item.target not in seen:
            seen.add(item.target)
            unique.append(item)
    return unique


# ---------------------------------------------------------------------------------------------------------------------
# Download
# ---------------------------------------------------------------------------------------------------------------------

class Downloader:
    """Thread-safe downloader with retries, exponential backoff and atomic writes."""

    def __init__(self, base_url: str, dest: str, retries: int, timeout: float, cafile: Optional[str], force: bool):
        self.base_url = base_url
        self.dest = dest
        self.retries = max(0, retries)
        self.timeout = timeout
        self.force = force
        self._lock = threading.Lock()
        self.bytes_downloaded = 0
        cafile = cafile or os.environ.get("SSL_CERT_FILE") or os.environ.get("REQUESTS_CA_BUNDLE")
        self.ssl_context = ssl.create_default_context(cafile=cafile if cafile and os.path.isfile(cafile) else None)
        # urllib honours HTTPS_PROXY / HTTP_PROXY / NO_PROXY through the default ProxyHandler.
        self.opener = urllib.request.build_opener(urllib.request.HTTPSHandler(context=self.ssl_context))

    def url_for(self, source: str) -> str:
        # Some Rocketbox file names contain spaces ("f_idle_finger nail_01.max.fbx").
        return self.base_url + urllib.parse.quote(source, safe="/")

    def local_path(self, item: DownloadItem) -> str:
        return os.path.join(self.dest, *item.target.split("/"))

    def exists(self, item: DownloadItem) -> bool:
        path = self.local_path(item)
        return os.path.isfile(path) and os.path.getsize(path) > 0

    def fetch(self, item: DownloadItem) -> Tuple[str, DownloadItem, int, str]:
        """Returns (status, item, bytes, message) with status in {'ok', 'skip', 'fail'}."""
        path = self.local_path(item)
        if not self.force and self.exists(item):
            return "skip", item, os.path.getsize(path), "already present"
        os.makedirs(os.path.dirname(path), exist_ok=True)
        part = path + ".part"
        url = self.url_for(item.source)
        last_error = ""
        for attempt in range(self.retries + 1):
            if attempt:
                # Exponential backoff with jitter: ~1 s, 2 s, 4 s, 8 s ...
                time.sleep(min(30.0, (2 ** (attempt - 1))) * (0.75 + random.random() * 0.5))
            try:
                size = self._download_once(url, part)
                os.replace(part, path)
                with self._lock:
                    self.bytes_downloaded += size
                return "ok", item, size, "downloaded" if attempt == 0 else f"downloaded after {attempt} retr{'y' if attempt == 1 else 'ies'}"
            except urllib.error.HTTPError as exc:
                last_error = f"HTTP {exc.code} {exc.reason}"
                if exc.code in (400, 401, 403, 404, 407, 410):
                    break  # permanent: retrying will not help
            except (urllib.error.URLError, OSError, ValueError) as exc:
                last_error = str(getattr(exc, "reason", exc)) or exc.__class__.__name__
            finally:
                if os.path.exists(part):
                    try:
                        os.remove(part)
                    except OSError:
                        pass
        return "fail", item, 0, f"{last_error} ({url})"

    def _download_once(self, url: str, part: str) -> int:
        request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
        with self.opener.open(request, timeout=self.timeout) as response:
            expected = response.headers.get("Content-Length")
            expected_size = int(expected) if expected and expected.isdigit() else -1
            written = 0
            with open(part, "wb") as out:
                while True:
                    chunk = response.read(CHUNK_SIZE)
                    if not chunk:
                        break
                    out.write(chunk)
                    written += len(chunk)
        if written == 0:
            raise ValueError("empty response")
        if expected_size >= 0 and written != expected_size:
            raise ValueError(f"truncated download ({written} of {expected_size} bytes)")
        return written


def write_license(dest: str, commit: str) -> str:
    path = os.path.join(dest, LICENSE_FILE_NAME)
    os.makedirs(dest, exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(LICENSE_TEXT.format(commit=commit or "unknown"))
    return path


def human_size(num_bytes: float) -> str:
    for unit in ("B", "KB", "MB", "GB"):
        if num_bytes < 1024 or unit == "GB":
            return f"{num_bytes:.1f} {unit}" if unit != "B" else f"{int(num_bytes)} B"
        num_bytes /= 1024.0
    return f"{num_bytes:.1f} GB"


# ---------------------------------------------------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------------------------------------------------

def default_paths() -> Tuple[str, str]:
    tools_dir = os.path.dirname(os.path.abspath(__file__))
    repo_root = os.path.dirname(tools_dir)
    return (os.path.join(tools_dir, "rocketbox_manifest.json"),
            os.path.join(repo_root, "Assets", "ThirdParty", "Rocketbox"))


def parse_args(argv: Optional[Sequence[str]] = None) -> argparse.Namespace:
    manifest_default, dest_default = default_paths()
    parser = argparse.ArgumentParser(
        description="Download Microsoft Rocketbox avatars (MIT) and the curated motion-capture set for Dodgeball Ultra.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="Hero casting: " + ", ".join(f"{h}={a}" for h, a in HERO_AVATARS.items()))
    parser.add_argument("--avatars", default="all-heroes",
                        help="'all-heroes' (default), 'all', 'none' or a comma-separated list of avatar folder names "
                             "(hero names such as 'Rayne' are accepted too)")
    parser.add_argument("--animations", action="store_true",
                        help="also download the curated motion-capture set (male + female): "
                             + ", ".join(CURATED_ANIMATIONS))
    parser.add_argument("--extra-animations", default="",
                        help="comma-separated additional animation file names from the manifest, e.g. m_cheer_02")
    parser.add_argument("--dest", default=dest_default, help=f"destination folder (default: {dest_default})")
    parser.add_argument("--manifest", default=manifest_default, help="path to rocketbox_manifest.json")
    parser.add_argument("--jobs", type=int, default=4, help="parallel downloads (default 4)")
    parser.add_argument("--retries", type=int, default=4, help="retries per file on transient errors (default 4)")
    parser.add_argument("--timeout", type=float, default=60.0, help="socket timeout in seconds (default 60)")
    parser.add_argument("--cafile", default=None, help="CA bundle for TLS verification (default: system / SSL_CERT_FILE)")
    parser.add_argument("--no-portraits", action="store_true", help="skip the avatar portrait PNGs")
    parser.add_argument("--force", action="store_true", help="re-download files that already exist")
    parser.add_argument("--dry-run", action="store_true", help="print the plan without downloading anything")
    parser.add_argument("--list", action="store_true", help="list the avatars in the manifest and exit")
    parser.add_argument("--quiet", action="store_true", help="only print failures and the summary")
    return parser.parse_args(argv)


def main(argv: Optional[Sequence[str]] = None) -> int:
    args = parse_args(argv)
    try:
        manifest = load_manifest(args.manifest)
    except ManifestError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2

    if args.list:
        heroes_by_avatar = {a: h for h, a in HERO_AVATARS.items()}
        for name in sorted(manifest["avatars"]):
            entry = manifest["avatars"][name]
            hero = heroes_by_avatar.get(name)
            print(f"{name:32s} {entry.get('gender', '?'):7s} {entry.get('category', ''):12s}"
                  + (f"  <- {hero}" if hero else ""))
        return 0

    try:
        plan = build_plan(args, manifest)
    except ManifestError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2
    if not plan:
        print("Nothing to download (use --avatars and/or --animations).")
        return 0

    dest = os.path.abspath(args.dest)
    downloader = Downloader(manifest["rawBaseUrl"], dest, args.retries, args.timeout, args.cafile, args.force)
    print(f"Rocketbox commit {manifest.get('commit', '?')} -> {dest}")

    if args.dry_run:
        present = 0
        for item in plan:
            here = downloader.exists(item)
            present += here
            print(f"  {'[present]' if here else '[fetch]  '} {item.target}  <-  {downloader.url_for(item.source)}")
        print(f"Dry run: {len(plan)} files, {present} already present, {len(plan) - present} to download.")
        return 0

    os.makedirs(dest, exist_ok=True)
    started = time.monotonic()
    counts = {"ok": 0, "skip": 0, "fail": 0}
    failures: List[str] = []
    done = 0
    with concurrent.futures.ThreadPoolExecutor(max_workers=max(1, args.jobs)) as pool:
        futures = [pool.submit(downloader.fetch, item) for item in plan]
        try:
            for future in concurrent.futures.as_completed(futures):
                status, item, size, message = future.result()
                done += 1
                counts[status] += 1
                if status == "fail":
                    failures.append(f"{item.target}: {message}")
                if status == "fail" or not args.quiet:
                    tag = {"ok": "ok  ", "skip": "skip", "fail": "FAIL"}[status]
                    print(f"[{done:3d}/{len(plan)}] {tag} {item.target} ({human_size(size)}) {message if status != 'ok' else ''}".rstrip(),
                          flush=True)
        except KeyboardInterrupt:
            for future in futures:
                future.cancel()
            print("\nCancelled - completed files are kept, re-run to resume.", file=sys.stderr)
            return 130

    license_path = write_license(dest, manifest.get("commit", ""))
    elapsed = time.monotonic() - started
    print(f"Done in {elapsed:.1f} s: {counts['ok']} downloaded ({human_size(downloader.bytes_downloaded)}), "
          f"{counts['skip']} skipped, {counts['fail']} failed. License: {license_path}")
    if failures:
        print("Failures:", file=sys.stderr)
        for line in failures:
            print("  " + line, file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except BrokenPipeError:
        # Output piped into e.g. `head`: exit quietly.
        try:
            sys.stdout.close()
        except OSError:
            pass
        sys.exit(0)
