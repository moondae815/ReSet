using System.Linq;
using Xunit;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class SpecAnchorIndexTests
    {
        [Fact]
        public void Build_ShouldFindLineColumnByHeaderName_NotByPosition()
        {
            // CASE 분기 표는 '라인'이 1번째 칸이다. 위치를 상수로 박으면 '순서' 값
            // (1, 2, 3...)을 라인 번호로 줍는다 - 설계서 첫 판이 실제로 낸 오류다.
            const string md = @"### CASE 분기 (기계 확정 — 수정 금지)

| 라인 | 순서 | 조건 원문 | 결과 원문 |
| :--- | :--- | :--- | :--- |
| 412 | WHEN 1 | @x > 3 | 7 |
| 415 | ELSE | (그 외 전부) | 0 |
";

            var lines = SpecAnchorIndex.Build(md).Select(a => a.Line).ToList();

            Assert.Equal(new[] { 412, 415 }, lines);
            Assert.DoesNotContain(1, lines);
            Assert.DoesNotContain(2, lines);
        }

        [Fact]
        public void Build_SetPredicateTable_ShouldReadSecondColumn()
        {
            const string md = @"### 집합 술어 (기계 확정 — 수정 금지)

| 문장 | 라인 | 컬럼 | 연산 |
| :--- | :--- | :--- | :--- |
| DELETE 1 | 38 | PGNAME | IN |
";

            var anchor = Assert.Single(SpecAnchorIndex.Build(md));
            Assert.Equal(38, anchor.Line);
            Assert.False(anchor.IsCommentAnchor);
            Assert.Contains("집합 술어", anchor.Source);
            Assert.Contains("PGNAME", anchor.RowText);
        }

        [Theory]
        [InlineData("### 원본 주석 기록", "라인", "원본 주석")]
        [InlineData("### 원본 주석 보존", "라인", "원본 주석")]
        [InlineData("### 원본 주석 보존 내역", "라인", "원본 주석")]
        [InlineData("### 원본 주석 및 이력", "라인", "원문 주석 또는 선언")]
        [InlineData("### 원본 주석 및 구현 대조", "라인", "원문 주석")]
        [InlineData("### 원본 주석 및 실제 구현 대조", "라인", "원문 주석")]
        public void Build_CommentTableVariants_ShouldAllBeMarkedAsCommentAnchors(
            string heading, string firstColumn, string secondColumn)
        {
            // 실측: 주석 표 제목이 여섯으로 갈리고 컬럼명도 셋으로 갈린다.
            // 제목이나 컬럼명 하나로 식별하면 반드시 샌다.
            var md = $@"{heading}

| {firstColumn} | {secondColumn} |
| :--- | :--- |
| 77 | -- 정산 보류 처리 |
";

            var anchor = Assert.Single(SpecAnchorIndex.Build(md));
            Assert.Equal(77, anchor.Line);
            Assert.True(anchor.IsCommentAnchor, $"'{heading}'가 주석 앵커로 표시되지 않았다");
        }

        [Fact]
        public void Build_CommentTableWithoutItsOwnHeading_ShouldStillBeMarked()
        {
            // EXCEPTION_PROC 실측: '## 로직 흐름 요약' 아래 산문 뒤에 제목 없이 붙는다.
            const string md = @"## 로직 흐름 요약

원본 주석 및 이력은 다음과 같습니다.

| 라인 | 원본 주석 |
| :--- | :--- |
| 91 | -- 부가세 계산 |
";

            var anchor = Assert.Single(SpecAnchorIndex.Build(md));
            Assert.Equal(91, anchor.Line);
            Assert.True(anchor.IsCommentAnchor);
        }

        [Fact]
        public void Build_SectionHeading_ShouldPickOriginalDdlLine()
        {
            const string md =
                "### UPDATE 대상 테이블: SETTLE_POQ_DB.dbo.TSettleMst (갱신 1 · 원본 DDL 라인 38 · 원문 표기: TSettleMst)\n";

            var anchor = Assert.Single(SpecAnchorIndex.Build(md));
            Assert.Equal(38, anchor.Line);
            Assert.Equal("절 제목", anchor.Source);
            Assert.False(anchor.IsCommentAnchor);
        }

        [Fact]
        public void Build_ReferencedFunctionCell_ShouldPickParenthesizedLine()
        {
            const string md = @"### 참조 함수 (기계 확정 — 수정 금지)

| 함수 | 호출 문장 | 호출식 | 명세서 |
| :--- | :--- | :--- | :--- |
| dbo.UF_GET_ROUND4VAT | UPDATE 3 (라인 110) | dbo.UF_GET_ROUND4VAT(X) | [Spec](../a.md) |
";

            var anchors = SpecAnchorIndex.Build(md);
            Assert.Contains(anchors, a => a.Line == 110);
        }

        [Fact]
        public void Build_TableInsideCodeFence_ShouldBeIgnored()
        {
            const string md = @"### 예시

```
| 문장 | 라인 |
| :--- | :--- |
| DELETE 1 | 999 |
```
";

            Assert.Empty(SpecAnchorIndex.Build(md));
        }

        [Fact]
        public void Build_EscapedPipeInPrecedingColumn_ShouldNotShiftLineColumnIndex()
        {
            // '문장' 칸에 이스케이프된 파이프(\|)가 들어 있으면, 이를 셀 경계로 오인해
            // 단순 Split('|')하는 구현은 그 뒤 칸들이 통째로 밀린다 - '라인' 칸에서
            // 38 대신 그 앞뒤 조각("4" 등)을 줍게 된다. MarkdownTableCellCodec.SplitRow가
            // \|를 복원해 셀 경계로 보지 않아야 헤더·데이터 행의 칸 인덱스가 어긋나지 않는다.
            const string md = @"### 집합 술어 (기계 확정 — 수정 금지)

| 문장 | 라인 | 컬럼 | 연산 |
| :--- | :--- | :--- | :--- |
| DELETE FLAGS \| 4 | 38 | PGNAME | IN |
";

            var anchor = Assert.Single(SpecAnchorIndex.Build(md));
            Assert.Equal(38, anchor.Line);
        }

        [Fact]
        public void Build_RowWithEscapedPipe_ShouldKeepRowTextVerbatim()
        {
            // 근거 패널은 표 원문 그대로를 요구한다(설계서 §3). 셀 단위 언이스케이프
            // 결과가 RowText로 새면 안 되고, 소스 마크다운 그대로(백슬래시-파이프 포함)
            // 남아야 한다.
            const string md = @"### 원본 주석 기록

| 라인 | 원본 주석 |
| :--- | :--- |
| 77 | -- SET FLAGS \| 4 |
";

            var anchor = Assert.Single(SpecAnchorIndex.Build(md));
            Assert.Equal(77, anchor.Line);
            Assert.True(anchor.IsCommentAnchor);
            Assert.Contains(@"FLAGS \| 4", anchor.RowText);
        }

        [Fact]
        public void Build_FreeTextAnchorPatternsInsideCodeFence_ShouldBeIgnored()
        {
            // 표 스캔은 이미 펜스를 건너뛴다(Build_TableInsideCodeFence_ShouldBeIgnored).
            // 자유 텍스트 스캔('(라인 N)'·'원본 DDL 라인 N')이 펜스를 안 걸러내면 실제
            // Spec.md에 흔한 mermaid 다이어그램 안의 우연한 문자열도 앵커로 잡힌다.
            const string md = @"### 예시

```
그림 설명 (라인 999) 원본 DDL 라인 123
```
";

            Assert.Empty(SpecAnchorIndex.Build(md));
        }

        [Fact]
        public void CountLineBearingTables_ShouldCountDistinctTablesWithLineColumn()
        {
            const string md = @"### DML 범위 (기계 확정 — 수정 금지)

| 문장 | 라인 | 대상 |
| :--- | :--- | :--- |
| DELETE 1 | 35 | T |

### 파생 테이블 정의 (기계 확정 — 수정 금지)

| 별칭 | 컬럼 | 정의 표현식 |
| :--- | :--- | :--- |
| X | A | SUM(B) |
";

            // 파생 테이블 정의 표에는 '라인' 칸이 없다(실측). 1종만 세어야 한다.
            Assert.Equal(1, SpecAnchorIndex.CountLineBearingTables(md));
        }

        [Fact]
        public void Build_NullOrBlank_ShouldReturnEmpty()
        {
            Assert.Empty(SpecAnchorIndex.Build(null));
            Assert.Empty(SpecAnchorIndex.Build("   "));
        }

        [Fact]
        public void Build_CommentTableWithNonCommentColumnNames_ShouldBeMarkedByHeading()
        {
            // 실측(UF_GET_COMM4PG4INTEREST): 제목엔 '주석'이 있는데 칸 이름은
            // '라인'|'원문'뿐이라 컬럼명 판정("주석"이 든 칸)만으로는 안 걸린다.
            // 이 표의 행이 일반 사실 앵커로 새면 허위 🟩(진짜 결함을 가림)이 된다.
            const string md = @"### 원본 주석 기록

| 라인 | 원문 |
| :--- | :--- |
| 27 | -- 이자 계산 보정 |
";

            var anchor = Assert.Single(SpecAnchorIndex.Build(md));
            Assert.Equal(27, anchor.Line);
            Assert.True(anchor.IsCommentAnchor, "제목에 '주석'이 있으면 칸 이름과 무관하게 주석 앵커여야 한다");
        }

        [Fact]
        public void Build_FreeTextSectionHeadingUnderCommentHeading_ShouldBeMarkedAsCommentAnchor()
        {
            // 자유 텍스트 스캔('원본 DDL 라인 N'· '(라인 N)')이 주석 절 아래인지
            // 안 가리면, 그 절의 사실이 일반 앵커로 새 판정에 영향을 준다.
            const string md = @"### 원본 주석 기록

원본 DDL 라인 47 관련 처리 내용을 기록한다.
";

            var anchor = Assert.Single(SpecAnchorIndex.Build(md));
            Assert.Equal(47, anchor.Line);
            Assert.True(anchor.IsCommentAnchor, "주석 절 아래의 자유 텍스트 앵커는 주석 앵커로 표시돼야 한다");
        }
    }
}
