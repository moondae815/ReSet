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

promoted=0
while IFS= read -r src; do
  # <...>/<객체>/raw/l1-attempts.json → <객체>
  object_dir="$(dirname "$(dirname "$src")")"
  object_name="$(basename "$object_dir")"
  dest_dir="$corpus/$object_name"
  mkdir -p "$dest_dir"
  cp "$src" "$dest_dir/attempts.json"
  echo "  승격: $object_name ($(wc -c < "$src") 바이트)"
  promoted=$((promoted + 1))
done < <(find -L "$output_dir" -type f -name 'l1-attempts.json' | sort)

echo "승격한 객체 $promoted 개 → $corpus"
echo "다음: git add tests/ReSet.Core.Tests/Fixtures/rejected-attempts 후"
echo "      dotnet test --filter 'FullyQualifiedName~SelfReinforcingCheckTests'"
