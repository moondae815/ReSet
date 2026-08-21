using System.Collections.Generic;
using ReSet.Core.Models;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class SpecExpectationsTests
    {
        private static SpDefinition BuildSp()
        {
            var dep = new DependencyInfo
            {
                Name = "TSettleMst", Schema = "dbo", Database = "SETTLE_POQ_DB", Type = "USER_TABLE"
            };
            dep.Columns.Add(new ColumnInfo { ColumnName = "CLINTCOMM", DataType = "int" });
            dep.Columns.Add(new ColumnInfo { ColumnName = "CLETC", DataType = "int" });

            var sp = new SpDefinition
            {
                Schema = "dbo",
                Name = "UP_PROBE",
                ObjectKey = new CodeObjectKey("SETTLE_POQ_DB", "dbo", "UP_PROBE", CodeObjectType.Procedure),
                StaticAnalysis = new SpStaticAnalysisResult
                {
                    IsParsedSuccessfully = true,
                    ReferencedColumnsPerTable = new Dictionary<string, List<string>>(
                        System.StringComparer.OrdinalIgnoreCase)
                    {
                        ["SETTLE_POQ_DB.dbo.TSettleMst"] = new List<string> { "CLINTCOMM", "CLETC" }
                    }
                }
            };
            sp.Dependencies.Add(dep);
            return sp;
        }

        [Fact]
        public void From_ShouldExposePromptSchemaColumnsKeyedByCanonicalName()
        {
            // Act
            var expectations = SpecExpectations.From(BuildSp());

            // Assert
            Assert.NotNull(expectations);
            var columns = Assert.Contains("SETTLE_POQ_DB.dbo.TSettleMst", expectations!.PromptSchemaColumns);
            Assert.Contains("CLINTCOMM", columns);
            Assert.Contains("CLETC", columns);
        }

        [Fact]
        public void From_WhenDependencyHasNoColumns_ShouldNotCreateAnEntry()
        {
            // Arrange - 스키마 표가 아예 렌더링되지 않는 의존성은 대조 기준이 될 수 없다.
            // 여기에 빈 항목을 만들면 "제공되지 않았습니다"라는 참인 진술이
            // 대조 대상으로 잘못 올라간다.
            var sp = BuildSp();
            sp.Dependencies[0].Columns.Clear();

            // Act
            var expectations = SpecExpectations.From(sp);

            // Assert
            Assert.DoesNotContain("SETTLE_POQ_DB.dbo.TSettleMst", expectations?.PromptSchemaColumns ?? new Dictionary<string, IReadOnlySet<string>>());
        }

        [Fact]
        public void From_WithNullSpDefinition_ShouldReturnNull()
        {
            Assert.Null(SpecExpectations.From(null));
        }

        [Fact]
        public void From_ShouldCarryInputDefects()
        {
            // Arrange - 정식 비교가 어긋나 컬럼이 유실되는 구성.
            var sp = BuildSp();
            sp.StaticAnalysis.ReferencedColumnsPerTable.Clear();
            sp.StaticAnalysis.ReferencedColumnsPerTable["OtherDb.dbo.TSettleMst"] =
                new List<string> { "CLINTCOMM" };

            // Act
            var expectations = SpecExpectations.From(sp);

            // Assert
            Assert.NotNull(expectations);
            Assert.NotEmpty(expectations!.InputDefects);
        }

        [Fact]
        public void InputDefects_ShouldNotBecomeValidationErrors()
        {
            // Arrange - A 위반이 있는 기대값으로 정상 명세서를 검증한다.
            var sp = BuildSp();
            sp.StaticAnalysis.ReferencedColumnsPerTable.Clear();
            sp.StaticAnalysis.ReferencedColumnsPerTable["OtherDb.dbo.TSettleMst"] =
                new List<string> { "CLINTCOMM" };
            var expectations = SpecExpectations.From(sp);
            Assert.NotEmpty(expectations!.InputDefects);

            // DatabasePlacementExtractor는 파싱에 성공한 SP마다 DB 배치 확정 문장을
            // 낸다(Task 3의 실행 의미 표). 그 표를 markdown에도 그대로 실어야 이
            // 테스트가 검증하려는 것(InputDefects는 오류가 되지 않는다)과 무관한
            // ExecutionSemanticsTableMissing이 섞여 들지 않는다.
            var executionSemanticsRow = Assert.Single(expectations.ExecutionSemantics);
            var markdown = string.Join("\n", new[]
            {
                "## 개요", "내용", "## 파라미터 목록", "내용", "## CRUD 분석", "내용",
                "## 로직 흐름 요약", "내용", "## 비즈니스 흐름 시각화",
                "```mermaid", "flowchart TD", "A[\"시작\"] --> B[\"끝\"]", "```",
                "",
                ExecutionSemanticsFacts.TableHeading,
                "",
                "| 종류 | 라인 | 대상 | 확정 사실 |",
                "| :--- | :--- | :--- | :--- |",
                $"| {executionSemanticsRow.Kind} | {executionSemanticsRow.Line} | "
                    + $"{executionSemanticsRow.Target} | {executionSemanticsRow.Fact} |"
            });

            // Act
            var result = new MechanicalValidator().Validate(markdown, expectations);

            // Assert - 입력 결함은 재생성 루프에 들어가면 안 된다.
            Assert.True(result.IsValid);
            Assert.Empty(result.Errors);
        }
    }
}
