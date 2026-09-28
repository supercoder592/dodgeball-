#!/usr/bin/env bash
# Publishes docs/ (the web build) to the gh-pages branch, which GitHub Pages serves at
# https://<owner>.github.io/<repo>/ . Usage: Tools/web/deploy_pages.sh "commit message"
set -euo pipefail
REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
WT="${TMPDIR:-/tmp}/du-gh-pages"
MSG="${1:-Deploy web build}"
cd "$REPO"
git fetch -q origin gh-pages || true
rm -rf "$WT"
if git show-ref -q --verify refs/remotes/origin/gh-pages; then
  git worktree add -f "$WT" origin/gh-pages >/dev/null 2>&1 || { git worktree prune; git worktree add -f "$WT" origin/gh-pages >/dev/null; }
  (cd "$WT" && git checkout -q -B gh-pages origin/gh-pages)
else
  git worktree add -f --detach "$WT" >/dev/null && (cd "$WT" && git checkout -q --orphan gh-pages)
fi
cd "$WT"
git rm -rfq . >/dev/null 2>&1 || true
git clean -fdxq
cp -r "$REPO/docs/." .
rm -f ARCHITECTURE.md
touch .nojekyll
# Build stamp: lets you (and the smoke test) confirm which build GitHub Pages is serving.
printf '{ "commit": "%s", "built": "%s" }\n' "$(git -C "$REPO" rev-parse --short HEAD)" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" > version.json
git add -A
git commit -q -m "$MSG" || echo "nothing to deploy"
git push -q origin gh-pages
cd "$REPO" && git worktree remove -f "$WT"
echo "deployed to gh-pages"
