#!/usr/bin/env sh
#
# 문서의 상대 링크가 실제 파일을 가리키는지 확인한다.
#
# 이 검사의 함정은 "발화 0"이 두 가지를 뜻한다는 것이다 — 링크가 다 성하거나,
# 검사가 아무것도 안 보고 있거나. 그래서 마지막 줄에 **확인한 링크 수**를 함께 낸다.
# 종전 검사는 `docs/architecture.md`와 `](../src/`를 동시에 못박아, 문서가 갈리면
# 0개를 검사하고 조용히 통과했다.
#
# 경로 해석은 **각 파일의 dirname 기준**이다. 깊이를 가정하지 않으므로 문서가
# docs/ 아래 몇 겹으로 들어가도 따라간다.
set -eu

ROOT=$(git rev-parse --show-toplevel)
cd "$ROOT"

FILES="README.md AGENTS.md docs/architecture.md"
for f in docs/architecture/*.md; do
  [ -e "$f" ] && FILES="$FILES $f"
done

pairs=$(mktemp)
trap 'rm -f "$pairs"' EXIT

for f in $FILES; do
  [ -e "$f" ] || continue
  grep -oE '\]\([^)]+\)' "$f" 2>/dev/null |
    sed -E 's/^\]\(//; s/\)$//' |
    while IFS= read -r p; do
      case "$p" in
        http://*|https://*|mailto:*|'#'*) continue ;;
      esac
      target=${p%%#*}
      [ -z "$target" ] && continue
      printf '%s\t%s\n' "$f" "$target"
    done >> "$pairs"
done

total=$(wc -l < "$pairs" | tr -d ' ')
broken=0

while IFS="$(printf '\t')" read -r f target; do
  dir=$(dirname "$f")
  if [ ! -e "$dir/$target" ]; then
    echo "BROKEN $f -> $target"
    broken=$((broken + 1))
  fi
done < "$pairs"

echo "링크 검사 완료: ${total}개 확인, ${broken}개 깨짐"
[ "$broken" -eq 0 ]
