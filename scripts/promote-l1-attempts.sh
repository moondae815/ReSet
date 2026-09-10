#!/usr/bin/env bash
# 거부된 시도 발화를 커밋되는 코퍼스로 승격한다.
#
# output/ 은 gitignore 라 CI 도 다음 세션도 못 본다. 이 스크립트가 산출물의
# raw/l1-attempts.json 을 tests/ReSet.Core.Tests/Fixtures/rejected-attempts/ 로 옮긴다.
#
# 2026-09-09 이전에는 이 일을 사람이 3 만 줄짜리 로그에서 손으로 했다(하루 다섯 번).
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
corpus="$repo_root/tests/ReSet.Core.Tests/Fixtures/rejected-attempts"
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
done < <(find "$repo_root/output" -type f -name 'l1-attempts.json' 2>/dev/null | sort)

echo "승격한 객체 $promoted 개 → $corpus"
echo "다음: git add tests/ReSet.Core.Tests/Fixtures/rejected-attempts 후"
echo "      dotnet test --filter 'FullyQualifiedName~SelfReinforcingCheckTests'"
