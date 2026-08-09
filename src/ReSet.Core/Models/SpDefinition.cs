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

        public List<AstUpdateAssignment> Assignments { get; set; } = new();

        /// <summary>FROM 절 원문. 없으면 null이며, 자기참조 의미 경고가 붙지 않는다.</summary>
        public string? FromClauseText { get; set; }

        /// <summary>SET 우변이 같은 문장의 타겟 컬럼을 참조하는 컬럼들. 동시평가 경고의 근거다.</summary>
        public List<string> SelfReferencedColumns { get; set; } = new();
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
