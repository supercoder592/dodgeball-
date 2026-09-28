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
