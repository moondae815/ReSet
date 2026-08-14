#!/usr/bin/env sh
#
# AGENTS.md의 한 구간을 훑어, 각 줄이 서술하는 내용의 근거가 이미 어디에 있는지 찾는다.
#
# 삭제 대장을 손으로 쓰면 72개 항목에서 반드시 틀린다. "이 줄은 중복이니 지워도
# 된다"는 주장은 근거 위치를 댈 수 있을 때만 참이고, 그 확인은 기계가 해야 한다.
#
#   ./scripts/doc-audit.sh 20 133     §2 카탈로그 구간을 감사한다
#
# 출력: 줄번호 | 바이트 | 대상식별자 | architecture.md 분량 | 코드주석 분량 | 판정
set -eu

START=${1:?시작 행 번호가 필요합니다}
END=${2:?끝 행 번호가 필요합니다}
ROOT=$(git rev-parse --show-toplevel)
cd "$ROOT"

printf '%-6s %-7s %-34s %-9s %-9s %s\n' LINE BYTES SYMBOL ARCH_MD SUMMARY VERDICT

awk -v s="$START" -v e="$END" 'NR>=s && NR<=e {print NR"\t"$0}' AGENTS.md |
while IFS="$(printf '\t')" read -r ln body; do
  [ -z "$body" ] && continue

  bytes=$(printf '%s' "$body" | LC_ALL=C wc -c | tr -d ' ')

  # 그 줄이 다루는 대상: 첫 번째 [Xxx.cs] 링크의 클래스명
  sym=$(printf '%s' "$body" | grep -oE '\[[A-Za-z][A-Za-z0-9]+\.cs\]' | head -1 |
        tr -d '[].' | sed 's/cs$//')
  [ -z "$sym" ] && { printf '%-6s %-7s %-34s %-9s %-9s %s\n' "$ln" "$bytes" "-" "-" "-" "산문(수동판정)"; continue; }

  arch=$(LC_ALL=C grep -h "$sym" docs/architecture.md 2>/dev/null | LC_ALL=C wc -c | tr -d ' ')

  f=$(find src -name "$sym.cs" | head -1)
  if [ -n "$f" ]; then
    doc=$(awk '/\/\/\//{c+=length($0)} END{print c+0}' "$f")
  else
    doc=0
  fi

  if [ "$arch" -ge "$bytes" ]; then
    verdict="중복:architecture.md"
  elif [ "$doc" -ge "$bytes" ]; then
    verdict="중복:$sym.cs <summary>"
  else
    verdict="근거없음(이동필요)"
  fi

  printf '%-6s %-7s %-34s %-9s %-9s %s\n' "$ln" "$bytes" "$sym" "$arch" "$doc" "$verdict"
done
