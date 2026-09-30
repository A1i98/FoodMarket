#!/usr/bin/env bash
# Bump the shared .NET version, verify, commit, and create an annotated SemVer tag.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"
version_file=Directory.Build.props
pattern='^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-((0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)(\.(0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*))?$'
fail() { printf 'release-version: %s\n' "$*" >&2; exit 1; }
usage() {
  printf 'Usage: scripts/release-version.sh <major|minor|patch|release|alpha|beta|rc|prepatch|preminor|premajor|prerelease|X.Y.Z[-channel.N]> [--channel alpha|beta|rc] [--base patch|minor|major] [--dry-run] [--push]\n'
}
parse() {
  [[ "$1" =~ $pattern ]] || fail "invalid SemVer: $1"
  [[ "$1" =~ ^([0-9]+)\.([0-9]+)\.([0-9]+)(-(.*))?$ ]]
  major="${BASH_REMATCH[1]}" minor="${BASH_REMATCH[2]}" patch="${BASH_REMATCH[3]}"
  prerelease="${BASH_REMATCH[5]:-}"
  core="$major.$minor.$patch"
}
stable_bump() {
  case "$1" in
    major) next="$((major + 1)).0.0" ;;
    minor) next="$major.$((minor + 1)).0" ;;
    patch) next="$major.$minor.$((patch + 1))" ;;
  esac
}

[[ $# -gt 0 ]] || { usage; exit 2; }
bump="$1"; shift
push=false dry_run=false channel=alpha base=patch
while [[ $# -gt 0 ]]; do
  case "$1" in
    --push) push=true ;;
    --dry-run) dry_run=true ;;
    --channel) [[ $# -ge 2 ]] || fail '--channel requires a value'; channel="$2"; shift ;;
    --base) [[ $# -ge 2 ]] || fail '--base requires a value'; base="$2"; shift ;;
    -h|--help) usage; exit 0 ;;
    *) fail "unknown option: $1" ;;
  esac
  shift
done
[[ "$channel" =~ ^(alpha|beta|rc)$ ]] || fail 'channel must be alpha, beta, or rc'
[[ "$base" =~ ^(patch|minor|major)$ ]] || fail 'base must be patch, minor, or major'
[[ -z "$(git status --porcelain)" ]] || fail 'working tree must be clean before a release'
current="$(dotnet msbuild FoodMarket/FoodMarket.csproj -getProperty:Version)"
parse "$current"
case "$bump" in
  major|minor|patch) stable_bump "$bump" ;;
  prepatch|preminor|premajor) stable_bump "${bump#pre}"; next="$next-$channel.1" ;;
  alpha|beta|rc|prerelease)
    [[ "$bump" == prerelease ]] || channel="$bump"
    if [[ -z "$prerelease" ]]; then
      stable_bump "$base"; next="$next-$channel.1"
    elif [[ "$prerelease" =~ ^(alpha|beta|rc)\.([0-9]+)$ ]]; then
      if [[ "${BASH_REMATCH[1]}" == "$channel" ]]; then
        next="$core-$channel.$((10#${BASH_REMATCH[2]} + 1))"
      else
        next="$core-$channel.1"
      fi
    else
      fail 'unsupported prerelease; supply an explicit version'
    fi ;;
  release) [[ -n "$prerelease" ]] || fail 'release requires a prerelease'; next="$core" ;;
  *) [[ "$bump" =~ $pattern ]] || fail "unsupported version: $bump"; next="$bump" ;;
esac
[[ "$next" != "$current" ]] || fail 'target equals current version'
git rev-parse -q --verify "refs/tags/v$next" >/dev/null && fail "tag v$next already exists"
if "$dry_run"; then
  printf 'Current: %s\nNext: %s\nTag: v%s\n' "$current" "$next" "$next"
  exit 0
fi
parse "$next"
backup="$(mktemp)"; cp "$version_file" "$backup"; committed=false
cleanup() {
  if [[ $? -ne 0 && "$committed" == false ]]; then cp "$backup" "$version_file"; fi
  rm -f "$backup"
}
trap cleanup EXIT
python3 - "$version_file" "$next" "$core.0" <<'PY'
import pathlib, sys
path = pathlib.Path(sys.argv[1])
text = path.read_text()
import re
for name, value in [('Version', sys.argv[2]), ('AssemblyVersion', sys.argv[3]), ('FileVersion', sys.argv[3])]:
    text, count = re.subn(r'(<%s>)[^<]*(</%s>)' % (name, name), lambda match: match[1] + value + match[2], text)
    if count != 1:
        raise SystemExit('Expected exactly one ' + name)
path.write_text(text)
PY
dotnet test FoodMarket.Tests/FoodMarket.Tests.csproj --configuration Release
dotnet build FoodMarket/FoodMarket.csproj --configuration Release
git add -- "$version_file"
git diff --cached --quiet && fail 'version file did not change'
git commit -m "chore(release): v$next"
committed=true
git tag -a "v$next" -m "Release v$next"
if "$push"; then
  branch="$(git branch --show-current)"
  [[ -n "$branch" ]] || fail 'cannot push from detached HEAD'
  git push origin "$branch"
  git push origin "v$next"
fi
printf 'Created release commit and tag v%s\n' "$next"
