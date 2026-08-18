using System.Collections.Generic;

namespace ReSet.Core.Models
{
    public class SpDefinition
    {
        public CodeObjectKey? ObjectKey { get; set; }
        public CodeObjectType ObjectType { get; set; } = CodeObjectType.Procedure;
        public FunctionReturnInfo? FunctionReturn { get; set; }
        public string Schema { get; set; } = "dbo";
        public string Name { get; set; } = string.Empty;
        public string DdlText { get; set; } = string.Empty;
        public List<DependencyInfo> Dependencies { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        public string? RawPromptContext { get; set; }
        public SpStaticAnalysisResult StaticAnalysis { get; set; } = new();
        public DeconstructedSpLogic DeconstructedLogic { get; set; } = new();
    }

    public class AstInsertMapping
    {
        public string TargetTable { get; set; } = string.Empty;
        public List<string> TargetColumns { get; set; } = new();
        public string SourceQueryBlock { get; set; } = string.Empty;
    }

    public class AstUpdateMapping
    {
        public string TargetTable { get; set; } = string.Empty;

        /// <summary>이 SP 안에서 같은 TargetTable에 대한 몇 번째 UPDATE 문장인가. 1부터 센다.</summary>
        public int StatementOrdinal { get; set; }

        /// <summary>
        /// 이 SP 안에서 대상 테이블과 무관하게 몇 번째 UPDATE 문장인가. 1부터 센다.
        ///
        /// StatementOrdinal은 <b>대상 테이블별</b> 채번이라 같은 SP 안에서 리셋된다 -
        /// 대상 표기가 "TSettleMst"와 "dbo.TSettleMst"로 갈리면 카운터도 갈린다.
        /// 2026-08-18 축 A 감사 실측(EXPECT_PROC): 라인 182와 245가 둘 다 "문장 1"이
        /// 되는데 같은 문서의 본문은 그것들을 "갱신 8"·"갱신 11"로 세어, 절 제목으로
        /// 문장을 지목하면 다른 UPDATE가 열렸다. 명세서 절 제목은 이 값을 쓴다.
        /// </summary>
        public int GlobalStatementOrdinal { get; set; }

        /// <summary>
        /// 원본 DDL에서 이 UPDATE 문장이 시작하는 줄 번호(1부터). 파싱 실패 시 0.
        ///
        /// StatementOrdinal이 앵커로 못 쓰이기 때문에 있다 - 채번이 대상 테이블별이고
        /// 청킹 경로가 파서를 여러 번 돌려 리셋되므로 "문장 1"이 여러 번 나온다.
        /// 라인은 청킹과 무관하게 유일하고 object_definition.sql로 사람이 대조한다.
        /// </summary>
        public int SourceLine { get; set; }

        public List<AstUpdateAssignment> Assignments { get; set; } = new();

        /// <summary>FROM 절 원문. 없으면 null이며, 자기참조 의미 경고가 붙지 않는다.</summary>
        public string? FromClauseText { get; set; }

        /// <summary>SET 우변이 같은 문장의 타겟 컬럼을 참조하는 컬럼들. 동시평가 경고의 근거다.</summary>
        public List<string> SelfReferencedColumns { get; set; } = new();

        /// <summary>
        /// UPDATE 대상의 원문 표기. TargetTable은 정규화된 3부 이름이라 원본이
        /// 실제로 몇 부로 썼는지 잃는다. 명세서가 정규화 이름을 원문처럼 서술해
        /// "3부 식별자 크로스 DB 참조" 같은 없는 사실을 단언한 실측이 있다.
        /// 정규화기는 이 값을 canonicalize하지 않고 그대로 옮긴다.
        /// </summary>
        public string? RawTargetText { get; set; }
    }

    public class AstUpdateAssignment
    {
        /// <summary>테이블 한정을 걷어낸 순수 컬럼명.</summary>
        public string Column { get; set; } = string.Empty;

        /// <summary>SET 우변 원문. 파서도 정규화기도 손대지 않는다.</summary>
        public string SourceExpression { get; set; } = string.Empty;
    }

    public class SpStaticAnalysisResult
    {
        public bool IsParsedSuccessfully { get; set; }
        public string? ParserWarningMessage { get; set; }
        public List<string> ReferencedTables { get; set; } = new();
        public List<string> CreatedTempTables { get; set; } = new();
        public List<string> ControlFlowSummary { get; set; } = new();
        public List<string> SelectTables { get; set; } = new();
        public List<string> InsertTables { get; set; } = new();
        public List<AstInsertMapping> AstInsertMappings { get; set; } = new();
        public List<string> UpdateTables { get; set; } = new();
        public List<AstUpdateMapping> AstUpdateMappings { get; set; } = new();
        public List<string> DeleteTables { get; set; } = new();
        public List<string> LinkedServerReferences { get; set; } = new();

        /// <summary>
        /// 원본이 3부 이상으로 표기한 오브젝트 참조의 원문. 테이블만이 아니다 - 3부로
        /// 호출된 테이블 반환 함수(TVF)도 포함한다. 둘 다 엄연한 크로스 DB 참조이고,
        /// 함수 호출을 빼면 그 사실을 정직하게 서술하는 명세서가 L1에서 오탐으로
        /// 걸린다(이 목록이 비었다고 잘못 판정하므로). 비어 있으면 이 SP에 3부 이상
        /// 오브젝트 참조가 전혀 없다는 뜻이며, L1이 명세서의 표기 주장을 이것으로
        /// 반증한다. 정규화 대상이 아니다 - 원문이어야 근거가 된다.
        /// </summary>
        public List<string> ThreePartObjectReferences { get; set; } = new();
        public List<string> ReferencedFunctions { get; set; } = new();
        public List<string> ProcedureParameters { get; set; } = new();
        public List<string> DeclaredVariables { get; set; } = new();
        public Dictionary<string, List<string>> ReferencedColumnsPerTable { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);
    }

    public class ChunkAnalysisResult
    {
        public string StatementText { get; set; } = string.Empty;
        public List<string> ReferencedTables { get; set; } = new();
        public List<string> ReferencedFunctions { get; set; } = new();
    }
}
