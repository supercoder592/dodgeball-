#!/usr/bin/env bash
# Publishes docs/ (the web build) to the gh-pages branch, which GitHub Pages serves at
# https://<owner>.github.io/<repo>/ .
#   Tools/web/deploy_pages.sh "commit message"      build + commit + push gh-pages
#   Tools/web/deploy_pages.sh --dry-run <dir>        build the same site into <dir> only (no git); test it with
#       node Tools/web/serve.mjs --root <dir> --pages   /  smoke.mjs --root <dir>  /  audit.mjs (DU_ROOT=<dir>)  /
#       DU_ROOT=<dir> loadprobe.mjs --query "seed=7&sw=1"
# The published site is docs/ plus the production build from build_site.mjs: one minified bundle
# (bundle/<hash>/main.js, loaded by index.html instead of the import map + js/main.js) and the service worker's
# version / file hashes (sw.js). js/ and vendor/ stay published for the dev pages (dev/*.html). The previous
# deploy's bundle directory is kept for one more deploy, so a page served from a stale HTTP cache still finds it.
set -euo pipefail
REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
DRY=""
if [ "${1:-}" = "--dry-run" ]; then
  DRY="${2:?usage: Tools/web/deploy_pages.sh --dry-run <dir>}"
  case "$DRY" in /*) ;; *) DRY="$PWD/$DRY" ;; esac
fi
WT="${TMPDIR:-/tmp}/du-gh-pages"
MSG="${1:-Deploy web build}"
cd "$REPO"
[ -x Tools/web/node_modules/.bin/esbuild ] || (cd Tools/web && npm install --no-audit --no-fund)
COMMIT="$(git rev-parse --short HEAD)"

# stage_site <dir>: docs/ + version stamp + production build (bundle, index.html, sw.js) into <dir>.
stage_site() {
  cp -r "$REPO/docs/." "$1/"
  rm -f "$1/ARCHITECTURE.md"
  touch "$1/.nojekyll"
  # Build stamp: lets you (and the smoke test) confirm which build GitHub Pages is serving.
  printf '{ "commit": "%s", "built": "%s" }\n' "$COMMIT" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" > "$1/version.json"
  node "$REPO/Tools/web/build_site.mjs" "$1" --version "$COMMIT"
}

if [ -n "$DRY" ]; then
  rm -rf "$DRY" && mkdir -p "$DRY"
  stage_site "$DRY"
  echo "dry run: site built in $DRY (nothing committed or pushed)"
  exit 0
fi

git fetch -q origin gh-pages || true
rm -rf "$WT"
if git show-ref -q --verify refs/remotes/origin/gh-pages; then
  git worktree add -f "$WT" origin/gh-pages >/dev/null 2>&1 || { git worktree prune; git worktree add -f "$WT" origin/gh-pages >/dev/null; }
  (cd "$WT" && git checkout -q -B gh-pages origin/gh-pages)
else
  git worktree add -f --detach "$WT" >/dev/null && (cd "$WT" && git checkout -q --orphan gh-pages)
fi
cd "$WT"
# The bundle directory the currently published index.html loads survives this deploy (see header).
KEEP="$(mktemp -d)"
PREV="$(grep -o 'bundle/[0-9a-f]*/' index.html 2>/dev/null | head -n1 || true)"
if [ -n "$PREV" ] && [ -d "$PREV" ]; then mkdir -p "$KEEP/bundle" && cp -r "$PREV" "$KEEP/bundle/"; fi
git rm -rfq . >/dev/null 2>&1 || true
git clean -fdxq
stage_site "$WT"
if [ -d "$KEEP/bundle" ]; then cp -rn "$KEEP/bundle/." bundle/; fi
rm -rf "$KEEP"
# Pushes made with an app/installation token do not start the branch-based Pages build, so the published branch
# carries a workflow that deploys itself through the Pages Actions API on every push (or on manual dispatch).
mkdir -p .github/workflows
cat > .github/workflows/deploy-pages.yml <<'YAML'
name: deploy-pages
on:
  push:
    branches: [gh-pages]
  workflow_dispatch:
permissions:
  contents: read
  pages: write
  id-token: write
concurrency:
  group: pages
  cancel-in-progress: true
jobs:
  deploy:
    runs-on: ubuntu-latest
    environment:
      name: github-pages
      url: ${{ steps.deployment.outputs.page_url }}
    steps:
      - uses: actions/checkout@v4
      - name: Pages configuration (diagnostic)
        continue-on-error: true
        env:
          GH_TOKEN: ${{ secrets.GITHUB_TOKEN }}
        run: gh api "repos/${{ github.repository }}/pages" || true
      - uses: actions/upload-pages-artifact@v3
        with:
          path: .
      - id: deployment
        uses: actions/deploy-pages@v4
YAML
git add -A
git commit -q -m "$MSG" || echo "nothing to deploy"
git push -q origin gh-pages
cd "$REPO" && git worktree remove -f "$WT"
echo "deployed to gh-pages"
