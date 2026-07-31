using System.Collections.Generic;

namespace ReSet.Core.Models
{
    public class SpDefinition
    {
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
