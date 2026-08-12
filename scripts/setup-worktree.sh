#!/usr/bin/env sh
#
# 새 git 워크트리에 버전 관리되지 않는 로컬 설정을 심볼릭 링크로 연결한다.
#
# .gitignore 대상 파일은 `git worktree add`로 만든 새 작업 트리에 존재하지 않는다.
# API 키가 담긴 appsettings.local.json이 대표적이라, 링크 없이는 CLI가 실행되지 않는다.
# 정본은 항상 주 워크트리(`git worktree list`의 첫 항목)의 파일이다.
#
#   ./scripts/setup-worktree.sh                 현재 워크트리에 링크를 건다(멱등)
#   ./scripts/setup-worktree.sh --install-hook  post-checkout 훅으로 등록해 이후 자동화
#
set -eu

LINKED_PATHS='src/ReSet.Cli/appsettings.local.json
.claude/settings.local.json'

HERE=$(git rev-parse --show-toplevel)
# --porcelain의 첫 레코드가 주 워크트리다. 경로에 공백이 있어도 잘리지 않도록 cut 대신 sed를 쓴다.
MAIN=$(git worktree list --porcelain | sed -n '1s/^worktree //p')

install_hook() {
    hook="$(git rev-parse --git-common-dir)/hooks/post-checkout"
    mkdir -p "$(dirname "$hook")"
    cat > "$hook" <<'HOOK'
#!/usr/bin/env sh
# git worktree add / git clone에서만 동작한다: 그때만 이전 HEAD가 널 OID다.
# 일반 git checkout에서는 즉시 빠져나가므로 평상시 작업에 영향이 없다.
case "$1" in *[!0]*) exit 0 ;; esac
script="$(git rev-parse --show-toplevel)/scripts/setup-worktree.sh"
[ -x "$script" ] || exit 0
exec "$script"
HOOK
    chmod +x "$hook"
    echo "setup-worktree: 훅 설치 완료 -> $hook"
}

# 훅 설치는 주 워크트리에서 실행하는 것이 정상이므로 아래 조기 종료보다 먼저 처리한다.
if [ "${1:-}" = "--install-hook" ]; then
    install_hook
    exit 0
fi

if [ "$HERE" = "$MAIN" ]; then
    echo "setup-worktree: 주 워크트리이므로 연결할 것이 없습니다 ($HERE)"
    exit 0
fi

for rel in $LINKED_PATHS; do
    src="$MAIN/$rel"
    dst="$HERE/$rel"

    if [ ! -e "$src" ]; then
        echo "setup-worktree: 건너뜀 (정본 없음): $rel"
        continue
    fi

    if [ -L "$dst" ]; then
        echo "setup-worktree: 이미 연결됨: $rel"
        continue
    fi

    # 실제 파일이 이미 있으면 사용자가 의도적으로 분리한 것일 수 있으므로 덮어쓰지 않는다.
    if [ -e "$dst" ]; then
        echo "setup-worktree: 경고 - 실제 파일이 존재해 링크하지 않음: $rel"
        continue
    fi

    mkdir -p "$(dirname "$dst")"
    ln -s "$src" "$dst"
    echo "setup-worktree: 연결함: $rel"
done
