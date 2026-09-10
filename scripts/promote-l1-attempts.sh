#!/usr/bin/env bash
# 거부된 시도 발화를 커밋되는 코퍼스로 승격한다.
#
# output/ 은 gitignore 라 CI 도 다음 세션도 못 본다. 이 스크립트가 산출물의
# raw/l1-attempts.json 을 tests/ReSet.Core.Tests/Fixtures/rejected-attempts/ 로 옮긴다.
#
# 2026-09-09 이전에는 이 일을 사람이 3 만 줄짜리 로그에서 손으로 했다(하루 다섯 번).
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output_dir="$repo_root/output"
corpus="$repo_root/tests/ReSet.Core.Tests/Fixtures/rejected-attempts"

# [왜 여기서 먼저 죽는가 - 2026-09-10 리뷰 발견] 이 저장소의 표준 워크트리에서
# output/ 은 심링크다(재생성 산출물을 공유하려고 각 워크트리가 건다). BSD find
# (이 저장소의 개발 플랫폼)는 -L 없이는 심링크인 시작 디렉터리를 따라가지 않고도
# 종료 코드 0 을 낸다 - 그러면 진짜 데이터가 있어도 「승격한 객체 0 개」가 조용히
# 성공으로 찍힌다. 「없다」와 「못 봤다」가 갈리게 두 가지를 고친다:
#   1. output/ 자체가 없으면 여기서 큰 소리로 실패한다(2>/dev/null 로 삼키지 않는다).
#   2. find 에 -L 을 붙여 심링크를 따라간다.
if [ ! -d "$output_dir" ]; then
  echo "오류: $output_dir 가 없습니다 - 이 워크트리에 산출물이 없습니다." >&2
  echo "      (표준 설정이면 output 을 실물 산출물 디렉터리로 심링크하십시오.)" >&2
  exit 1
fi

mkdir -p "$corpus"

# [왜 큰 소리로 죽는가 - 2026-09-10 감사 §4(a)] 종전에는 그냥 cp 였다. 키가 객체
# 이름뿐이라 같은 객체를 두 번 재생성하면 **앞 판 기록을 덮었다.** 그리고 덮어도
# 게이트는 안 빨개진다 - unrecorded = found - recorded 라 found 가 줄면 조용하고,
# 원장 줄만 근거를 잃는다(게이트가 단방향이라 고아 원장 줄을 안 본다).
#
# 조건이 **둘**이어야 한다. dest 존재 검사만으로는 2026-09-10 상태에서 안 걸렸다 -
# 코퍼스에 1 판이 있고 output 에는 2 판만 보이니 그대로 덮는다. 사람이 앞 판 파일을
# l1-attempts.run1-20260910.json 으로 개명해 뒀고, 승격기는 그 파일에 눈이 멀어 있었다.
promoted=0
blocked=0
hidden_total=0
while IFS= read -r src; do
  # <...>/<객체>/raw/l1-attempts.json → <객체>
  raw_dir="$(dirname "$src")"
  object_dir="$(dirname "$raw_dir")"
  object_name="$(basename "$object_dir")"
  dest_dir="$corpus/$object_name"
  dest="$dest_dir/attempts.json"

  # (2) 같은 raw/ 에 승격기가 안 보는 판이 있는가.
  #     find 는 'l1-attempts.json' 만 보므로, 개명된 판은 조용히 빠진다.
  hidden="$(find -L "$raw_dir" -maxdepth 1 -type f -name 'l1-attempts*.json' \
            ! -name 'l1-attempts.json' | sort)"
  if [ -n "$hidden" ]; then
    hidden_count="$(printf '%s\n' "$hidden" | wc -l | tr -d ' ')"
    hidden_total=$((hidden_total + hidden_count))
    echo "오류: $object_name - 승격기가 안 보는 판이 $hidden_count 개 있습니다:" >&2
    printf '        %s\n' $hidden >&2
    echo "      승격하면 그 판들이 코퍼스에 영영 안 들어갑니다." >&2
    echo "      할 일: 그 판들을 l1-attempts.json 에 합쳐 Run 이 붙게 하거나," >&2
    echo "             들이지 않기로 정했으면 raw/ 밖으로 옮기십시오." >&2
    blocked=$((blocked + 1))
    continue
  fi

  # (1) 이미 있는 코퍼스 파일과 내용이 다른가.
  if [ -f "$dest" ] && ! cmp -s "$src" "$dest"; then
    echo "오류: $object_name - 코퍼스에 이미 다른 내용이 있습니다." >&2
    echo "      기존 $(wc -c < "$dest" | tr -d ' ') 바이트 / 새것 $(wc -c < "$src" | tr -d ' ') 바이트" >&2
    echo "      덮으면 앞 판 기록이 사라지고, 원장이 그 판 서명을 근거로 쓰고 있으면" >&2
    echo "      그 근거가 끊깁니다(게이트는 그것을 안 잡습니다)." >&2
    echo "      할 일: 두 판을 합쳐 Run 이 갈리게 하거나, 어느 판을 남길지 정하십시오." >&2
    echo "      대상: $dest" >&2
    blocked=$((blocked + 1))
    continue
  fi

  mkdir -p "$dest_dir"
  cp "$src" "$dest"
  echo "  승격: $object_name ($(wc -c < "$src" | tr -d ' ') 바이트)"
  promoted=$((promoted + 1))
done < <(find -L "$output_dir" -type f -name 'l1-attempts.json' | sort)

# 분모를 함께 찍는다 - 「승격한 객체 N 개」만 보면 막힌 것이 몇인지 안 보인다.
echo "승격한 객체 $promoted 개 · 막힌 객체 $blocked 개 · 안 보이던 판 $hidden_total 개 → $corpus"

if [ "$blocked" -gt 0 ]; then
  echo "막힌 객체가 있어 실패로 끝냅니다 - 위 「할 일」을 처리하고 다시 도십시오." >&2
  exit 1
fi

echo "다음: git add tests/ReSet.Core.Tests/Fixtures/rejected-attempts 후"
echo "      dotnet test --filter 'FullyQualifiedName~SelfReinforcingCheckTests'"
