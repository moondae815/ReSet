#!/usr/bin/env python3
"""CheckSpecSetExpressions 의 토큰 후보별 발화/오탐을 두 판에서 잰다.

합격 기준은 발화 수가 아니라 판정이 갈리는 것이다 - 결함 판에서 늘고
현행 판에서 0 을 지키는 후보만 채택 가능하다.

사용법:  python3 scripts/measure-set-expression-tokens.py
"""
import glob
import os
import re
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFECTIVE = os.path.join(REPO, "output.bak-batch1-preregen-20260904/Jobs/POQSettleBatch1/agent/steps")
CURRENT = os.path.join(REPO, "output/Jobs/POQSettleBatch1/docs/BatchMigrationPlan.md")
SPECS = os.path.join(REPO, "output/Procedures/*/docs/Spec.md")

UPDATE_SECTION = re.compile(
    r"^###\s+UPDATE\s+대상 테이블:\s*([^\(]+?)\s*\(\s*갱신\s*(\d+)")

# 후보. 이름 -> 표현식에서 토큰을 뽑는 함수.
BASE = [
    (r"'([^']{2,})'", "인용 리터럴"),
    (r"\b(UF_[A-Za-z0-9_]+)", "UF_ 함수"),
    (r"(?<![\w.])(\d+\.\d+|\d{2,})(?![\w])", "2자리+ 숫자"),
]
CANDIDATES = {
    "base (현행)": BASE,
    "base + 별칭.컬럼": BASE + [(r"\b([A-Za-z]\.[A-Za-z_][A-Za-z0-9_]*)", "별칭.컬럼")],
    "base + 구조토큰": BASE + [(r"\b(CAST|ISNULL|IIF|ROUND)\s*\(", "구조토큰")],
    "base + 부호반전": BASE + [(r"(\*\s*\(?\s*-\s*1\s*\)?)", "부호반전")],
}


def tokens(expressions, patterns):
    out = []
    for expr in expressions:
        for pat, _ in patterns:
            for m in re.findall(pat, expr, re.IGNORECASE):
                t = (m if isinstance(m, str) else m[0]).strip()
                if t and t.lower() not in [x.lower() for x in out]:
                    out.append(t)
    return out


def read_targets(spec_path, patterns):
    lines = open(spec_path, encoding="utf-8").read().split("\n")
    rows = []
    for i, line in enumerate(lines):
        m = UPDATE_SECTION.match(line)
        if not m:
            continue
        end = next((j for j in range(i + 1, len(lines))
                    if lines[j].startswith("### ")), len(lines))
        blk = lines[i + 1:end]
        hdr = next((j for j, x in enumerate(blk) if x.strip().startswith("|")), None)
        if hdr is None:
            continue
        cols = [c.strip() for c in blk[hdr].strip("|").split("|")]
        ic = next((k for k, c in enumerate(cols) if "컬럼명" in c), -1)
        ie = next((k for k, c in enumerate(cols) if "원천 표현식" in c), -1)
        if ic < 0 or ie < 0:
            continue
        exprs = []
        for x in blk[hdr + 2:]:
            if not x.strip().startswith("|"):
                break
            c = [y.strip() for y in x.strip("|").split("|")]
            if ic < len(c) and c[ic] and ie < len(c):
                exprs.append(c[ie])
        rows.append((int(m.group(2)), tokens(exprs, patterns)))
    return rows


def bare(name):
    return name.strip().split(".")[-1].lower()


def spec_for(body, spec_paths):
    """단계 본문의 UP_ 토큰으로 명세서 하나를 고른다. 하나로 안 좁혀지면 None."""
    ups = {u.lower() for u in re.findall(r"\bUP_[A-Za-z_0-9]+", body)}
    cand = [p for p in spec_paths
            if bare(os.path.basename(os.path.dirname(os.path.dirname(p)))) in ups]
    return cand[0] if len(cand) == 1 else None


def step_bodies_defective():
    for f in sorted(glob.glob(os.path.join(DEFECTIVE, "*.md"))):
        yield os.path.basename(f)[:-3], open(f, encoding="utf-8").read()


def step_bodies_current():
    text = open(CURRENT, encoding="utf-8").read().split("\n")
    idx = [(i, l) for i, l in enumerate(text) if re.match(r"^### S\d\d", l)]
    for k, (i, l) in enumerate(idx):
        e = idx[k + 1][0] if k + 1 < len(idx) else len(text)
        yield re.match(r"^### (S\d\d)", l).group(1), "\n".join(text[i:e])


def evaluate(bodies, patterns, rule):
    spec_paths = sorted(glob.glob(SPECS))
    fired = comparable = zero = 0
    for _, body in bodies:
        sp = spec_for(body, spec_paths)
        if sp is None:
            continue
        low = body.lower()
        for _, tk in read_targets(sp, patterns):
            if not tk:
                zero += 1
                continue
            comparable += 1
            hits = sum(1 for t in tk if t.lower() in low)
            if rule == "any" and hits == 0:
                fired += 1
            elif rule == "all" and hits < len(tk):
                fired += 1
            elif rule == "majority" and hits * 2 < len(tk):
                fired += 1
    return fired, comparable, zero


def main():
    if not os.path.isdir(DEFECTIVE) or not os.path.isfile(CURRENT):
        print("고정 오라클이 없다. 경로를 확인하라.", file=sys.stderr)
        return 1

    print(f"{'후보':22} {'규칙':9} {'결함판 발화':>10} {'현행판 오탐':>10} {'대조가능':>8} {'토큰0':>6}")
    print("-" * 74)
    for name, patterns in CANDIDATES.items():
        for rule in ("any", "all", "majority"):
            df, dc, dz = evaluate(step_bodies_defective(), patterns, rule)
            cf, cc, cz = evaluate(step_bodies_current(), patterns, rule)
            print(f"{name:22} {rule:9} {df:>10} {cf:>10} {dc:>8} {dz:>6}")
    print()
    print("채택 조건: 결함판 발화 > base(any) 이고 현행판 오탐 == 0")
    return 0


if __name__ == "__main__":
    sys.exit(main())
