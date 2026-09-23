#!/usr/bin/env sh
set -eu

repository=$1
if ! command -v git >/dev/null 2>&1; then
  echo '[update] Git is not installed; starting the installed version.'
  exit 0
fi
if [ ! -e "$repository/.git" ]; then
  echo '[update] This is not a Git checkout (for example, a ZIP download); starting the installed version.'
  exit 0
fi
root=$(git -C "$repository" rev-parse --show-toplevel 2>/dev/null) || root=
if [ "$root" != "$repository" ]; then
  echo '[update] Git checkout could not be verified; starting the installed version.'
  exit 0
fi
branch=$(git -C "$repository" symbolic-ref --quiet --short HEAD 2>/dev/null) || branch=
upstream=$(git -C "$repository" rev-parse --abbrev-ref --symbolic-full-name '@{upstream}' 2>/dev/null) || upstream=
remote=$(git -C "$repository" remote get-url origin 2>/dev/null) || remote=
case "$remote" in
  https://github.com/Aspectofthe/relicframe.git|https://github.com/Aspectofthe/relicframe|git@github.com:Aspectofthe/relicframe.git) ;;
  *) echo '[update] Origin is not the expected RelicFrame GitHub repository; starting without updating.'; exit 0 ;;
esac
if [ "$branch" != csharp-rewrite ] || [ "$upstream" != origin/csharp-rewrite ]; then
  echo '[update] Branch or upstream is not csharp-rewrite; starting without updating.'
  exit 0
fi
changes=$(git -C "$repository" status --porcelain --untracked-files=normal) || changes=unknown
if [ -n "$changes" ]; then
  echo '[update] Local changes or untracked files are present; skipping update to protect them.'
  exit 0
fi

echo '[update] Checking GitHub for a newer csharp-rewrite commit...'
if command -v timeout >/dev/null 2>&1; then
  if ! timeout 20s env GIT_TERMINAL_PROMPT=0 git -C "$repository" -c http.lowSpeedLimit=1000 -c http.lowSpeedTime=10 fetch --quiet --no-tags origin csharp-rewrite; then
    echo '[update] GitHub could not be reached; starting the installed version.' >&2
    exit 0
  fi
elif ! GIT_TERMINAL_PROMPT=0 git -C "$repository" -c http.lowSpeedLimit=1000 -c http.lowSpeedTime=10 fetch --quiet --no-tags origin csharp-rewrite; then
  echo '[update] GitHub could not be reached; starting the installed version.' >&2
  exit 0
fi
ahead=$(git -C "$repository" rev-list --count origin/csharp-rewrite..HEAD) || ahead=unknown
behind=$(git -C "$repository" rev-list --count HEAD..origin/csharp-rewrite) || behind=unknown
case "$ahead:$behind" in
  *[!0-9:]*|:*) echo '[update] Could not compare Git revisions; starting the installed version.' >&2; exit 0 ;;
esac
if [ "$ahead" -gt 0 ]; then
  echo '[update] Local commits are ahead or diverged; skipping update.'
  exit 0
fi
if [ "$behind" -eq 0 ]; then
  echo '[update] Already up to date.'
  exit 0
fi
if ! git -C "$repository" merge --ff-only origin/csharp-rewrite; then
  echo '[update] Fast-forward failed; local files were not overwritten.' >&2
  exit 0
fi
echo "[update] Applied $behind new commit(s); building the updated bot."
exit 10
