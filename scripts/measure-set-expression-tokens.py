#!/usr/bin/env python3
"""CheckSpecSetExpressions 의 토큰 후보별 발화/오탐을 세 정의역에서 잰다.

합격 기준은 발화 수가 아니라 판정이 갈리는 것이다 - 결함 판에서 늘고
현행 판·코퍼스 전역에서 오탐이 없는 후보만 채택 가능하다.

[Fix Round 1(2026-09-05) - 정의역이 좁았다] Task 2 는 「오탐 0」을 POQSettleBatch1
한 Job 에서만 쟀다. 그런데 이 검사는 L1 이 모든 Job 의 모든 단계에 돌린다. 리뷰가
코퍼스 전역에서 정확히 구현된 코드(별칭만 다름)를 오탐 고발하는 자리를 실물로
확인했다(POQSettleProc1/S04 등) - 그래서 세 번째 정의역(코퍼스 전역)을 추가한다.

사용법:  python3 scripts/measure-set-expression-tokens.py
"""
import glob
import json
import os
import re
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFECTIVE = os.path.join(REPO, "output.bak-batch1-preregen-20260904/Jobs/POQSettleBatch1/agent/steps")
CURRENT = os.path.join(REPO, "output/Jobs/POQSettleBatch1/docs/BatchMigrationPlan.md")
SPECS = os.path.join(REPO, "output/Procedures/*/docs/Spec.md")
JOBS_ROOT = os.path.join(REPO, "output", "Jobs")

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


def is_alias_column_token(token):
    """토큰이 별칭.컬럼 모양인가(점 뒤가 문자·밑줄) - 소수(`0.1`)는 점 뒤가 숫자라 제외."""
    dot = token.find(".")
    return 0 < dot < len(token) - 1 and (token[dot + 1].isalpha() or token[dot + 1] == "_")


def token_hit(token, body):
    """토큰 하나가 본문에 있는가.

    [Fix Round 1 - 별칭 불문 대조] 별칭.컬럼 모양(예: `A.TxAmt`)은 별칭 문자를 떼고
    `.컬럼`만 대조한다 - 생성기가 명세서와 다른 별칭을 고를 수 있다(실물:
    POQSettleProc1/S04 - 명세서 `A`/`B`, 생성본 `S`/`P`). 점 뒤가 숫자면(`0.1`)
    별칭.컬럼이 아니라 소수이므로 이 경로를 타지 않는다 - C# 쪽 수정
    (MechanicalValidator.ContainsSetExpressionToken)과 같은 판별자를 쓴다.
    """
    if not is_alias_column_token(token):
        return token.lower() in body.lower()

    column = token[token.find(".") + 1:]
    return re.search(r"[A-Za-z0-9_]\." + re.escape(column) + r"(?!\w)", body, re.IGNORECASE) is not None


def is_fired(tk, body, rule):
    """토큰 목록 하나(갱신 하나)가 규칙 아래서 발화하는가.

    [Fix Round 3 Critical - 2026-09-05] `majority`(절반 이상)를 전체 토큰에 공용
    풀로 적용하면, 흔한 별칭.컬럼 토큰이 문서의 무관한 다른 문장에 우연히 걸려
    절반을 채워 결정적인 하드 사실(리터럴·UF_·숫자)이 전부 빠졌는데도 침묵한다
    (실물: POQSettleProc9/S06 갱신10, 감사 🔴 - UF_GET_PGCommOption·0.1 이 문서
    어디에도 없는데 A.PGCOMM·A.PGNAME 이 무관한 곳에 걸려 4개 중 2개로 침묵했다).

    `hybrid`는 하드 사실과 별칭.컬럼을 **각자 별도 풀로** 절반 이상 요구한다 -
    한쪽 풀의 부족을 다른 쪽 풀이 못 메운다. 처음엔 하드 사실을 "전부" 요구하는
    안을 시험했으나, 그러면 현행판(POQSettleBatch1) S08 갱신5(하드 토큰 9개 -
    2005·04·06 세 개가 이행 중 다른 표현으로 재구성돼 빠짐, 6/9는 있음)가 새
    오탐이 됐다(재서 확인, 2026-09-05) - "전부"는 Task 2가 이미 `all` 규칙에서
    증명한 함정을 하드 사실 쪽에서 재현한다. "절반 이상"으로 낮추면 그 자리는
    침묵하고 Proc9/S06 갱신10(하드 0/2)은 여전히 발화한다.
    MechanicalValidator.CheckSpecSetExpressions(Fix Round 3)와 같은 규칙이다.
    """
    hits = sum(1 for t in tk if token_hit(t, body))
    if rule == "any":
        return hits == 0
    if rule == "all":
        return hits < len(tk)
    if rule == "majority":
        return hits * 2 < len(tk)
    if rule == "hybrid":
        hard = [t for t in tk if not is_alias_column_token(t)]
        alias = [t for t in tk if is_alias_column_token(t)]
        hard_ok = not hard or sum(1 for t in hard if token_hit(t, body)) * 2 >= len(hard)
        alias_ok = not alias or sum(1 for t in alias if token_hit(t, body)) * 2 >= len(alias)
        return not (hard_ok and alias_ok)
    raise ValueError(f"알 수 없는 규칙: {rule}")


def parse_plan_structure_steps(plan_structure_path):
    """raw/PlanStructure.md 의 ```json 블록에서 Steps 배열을 읽는다."""
    text = open(plan_structure_path, encoding="utf-8").read()
    m = re.search(r"```json\s*\n(.*?)\n```", text, re.DOTALL)
    if not m:
        return None
    try:
        data = json.loads(m.group(1))
    except (json.JSONDecodeError, ValueError):
        return None
    return data.get("Steps")


def corpus_step_records():
    """모든 Job 의 (Job, Code, body, LegacyProcedures) 를 낸다 - L1 이 실제로 도는 정의역.

    `spec_for`(UP_ 토큰 스캔 휴리스틱)에 기대지 않는다 - 목차의 LegacyProcedures를
    직접 읽어 SweepCommand·MechanicalValidator가 조회하는 것과 같은 경로를 쓴다.
    """
    for job_dir in sorted(glob.glob(os.path.join(JOBS_ROOT, "*"))):
        plan_path = os.path.join(job_dir, "raw", "PlanStructure.md")
        if not os.path.isfile(plan_path):
            continue
        steps = parse_plan_structure_steps(plan_path)
        if not steps:
            continue
        for step in steps:
            code = step.get("Code")
            procs = step.get("LegacyProcedures") or []
            if not code or not procs:
                continue
            step_path = os.path.join(job_dir, "agent", "steps", f"{code}.md")
            if not os.path.isfile(step_path):
                continue
            body = open(step_path, encoding="utf-8").read()
            yield os.path.basename(job_dir), code, body, procs


def evaluate_corpus(patterns, rule):
    """코퍼스 전역(모든 Job 의 agent/steps 번들)에서 발화를 잰다.

    [정의역이 「현재판」과 다른 이유] `output/Jobs/POQSettleBatch1/docs/
    BatchMigrationPlan.md`는 그 Job 하나의 결합 문서다. 실제로 L1 이 도는 자리는
    Job마다의 `agent/steps/{Code}.md`다(SweepCommand.cs 가 읽는 자리와 같다) - 이
    함수는 그 정의역 전체를 훑는다. POQSettleBatch1 도 자신의 agent/steps 번들로
    다시 포함된다 - 「현재판」측정과 표본이 겹치지 않는다(파일이 다르다).
    """
    spec_index = {}
    for sp in sorted(glob.glob(SPECS)):
        proc_dir_name = os.path.basename(os.path.dirname(os.path.dirname(sp)))
        spec_index[bare(proc_dir_name)] = sp

    fired = comparable = zero = 0
    fired_sites = set()
    for job, code, body, procs in corpus_step_records():
        spec_paths = [spec_index[bare(p)] for p in procs if bare(p) in spec_index]
        for sp in spec_paths:
            for ordinal, tk in read_targets(sp, patterns):
                if not tk:
                    zero += 1
                    continue
                comparable += 1
                if is_fired(tk, body, rule):
                    fired += 1
                    fired_sites.add(f"{job}/{code}(갱신{ordinal})")
    return fired, comparable, zero, fired_sites


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
        for _, tk in read_targets(sp, patterns):
            if not tk:
                zero += 1
                continue
            comparable += 1
            if is_fired(tk, body, rule):
                fired += 1
    return fired, comparable, zero


def main():
    if not os.path.isdir(DEFECTIVE) or not os.path.isfile(CURRENT):
        print("고정 오라클이 없다. 경로를 확인하라.", file=sys.stderr)
        return 1

    print(f"{'후보':22} {'규칙':9} {'결함판 발화':>10} {'현행판 오탐':>10} "
          f"{'코퍼스 전역 발화':>14} {'대조가능':>8} {'토큰0':>6}")
    print("-" * 96)
    corpus_sites_by_row = {}
    for name, patterns in CANDIDATES.items():
        for rule in ("any", "all", "majority", "hybrid"):
            df, dc, dz = evaluate(step_bodies_defective(), patterns, rule)
            cf, cc, cz = evaluate(step_bodies_current(), patterns, rule)
            gf, gc, gz, gsites = evaluate_corpus(patterns, rule)
            corpus_sites_by_row[(name, rule)] = gsites
            print(f"{name:22} {rule:9} {df:>10} {cf:>10} {gf:>14} {dc:>8} {dz:>6}")
    print()
    print("채택 축(Fix Round 3 개정):")
    print("  축 −1  옛 규칙(base×any)이 잡던 코퍼스 전역 발화 중 새 규칙이 놓치는 것 == 0  (맨 앞. 못 넘으면 즉시 기각)")
    print("  축 0   코퍼스 전역 오탐 == 0")
    print("  축 1   현행판(POQSettleBatch1 단일 Job) 오탐 == 0")
    print("  축 2   결함판 발화 > base(현행)×any")
    print("  축 3   토큰0 잔량 최소")
    print()
    print("[주의] '코퍼스 전역 발화'는 발화 수이지 감사로 확인한 오탐 수가 아니다 -")
    print("전수 감사가 없어 발화 전부의 참/오탐을 가릴 수 없다. 리뷰어가 실물로 확인한")
    print("자리(POQSettleProc1/S04 등)가 이 열에서 사라졌는지로 방향만 확인한다.")
    print()
    print("리뷰어 확인 오탐 자리(POQSettleProc1/S04, 갱신1)가 아래 목록에 있으면 그")
    print("후보×규칙은 그 자리를 여전히 오탐 고발한다:")
    for key, sites in corpus_sites_by_row.items():
        hit = [s for s in sites if s.startswith("POQSettleProc1/S04")]
        if hit:
            print(f"  {key}: {hit}")

    print()
    print("=" * 96)
    print("축 −1 — 양방향 비교 (옛 규칙 base(현행)×any 대 후보×규칙)")
    print("=" * 96)
    old_sites = corpus_sites_by_row[("base (현행)", "any")]
    print(f"옛 규칙(base×any) 코퍼스 전역 발화: {len(old_sites)}건")
    print()
    print(f"{'후보':22} {'규칙':9} {'새 발화':>8} {'교집합':>8} {'신규발화':>8} {'사라진발화':>10}")
    print("-" * 72)
    for name, patterns in CANDIDATES.items():
        for rule in ("any", "all", "majority", "hybrid"):
            new_sites = corpus_sites_by_row[(name, rule)]
            both = old_sites & new_sites
            gained = new_sites - old_sites
            lost = old_sites - new_sites
            marker = "  <- 축 -1 위반" if lost else ""
            print(f"{name:22} {rule:9} {len(new_sites):>8} {len(both):>8} "
                  f"{len(gained):>8} {len(lost):>10}{marker}")
            if lost:
                print(f"    사라진 자리: {sorted(lost)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
