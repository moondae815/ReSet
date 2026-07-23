using System.Collections.Generic;

namespace ReSet.Core.Models
{
    public class DeconstructedSpLogic
    {
        public SpOverviewInfo Overview { get; set; } = new();
        public List<SpParameterInfo> Parameters { get; set; } = new();
        public SpCrudInfo Crud { get; set; } = new();
        public SpLogicInfo Logic { get; set; } = new();
        public SpVisualizationInfo Visualization { get; set; } = new();
    }

    public class SpOverviewInfo
    {
        public string SpName { get; set; } = string.Empty;
        public string Purpose { get; set; } = string.Empty;
        public string BusinessRole { get; set; } = string.Empty;
        public string ResultStyle { get; set; } = string.Empty;
    }

    public class SpParameterInfo
    {
        public string Name { get; set; } = string.Empty;
        public string DataType { get; set; } = string.Empty;
        public string Nullability { get; set; } = string.Empty;
        public string Purpose { get; set; } = string.Empty;
        public bool IsOutput { get; set; }
    }

    public class SpCrudInfo
    {
        public List<SpSelectTableInfo> SelectTables { get; set; } = new();
        public List<SpInsertMappingInfo> InsertTables { get; set; } = new();
        public List<SpUpdateMappingInfo> UpdateTables { get; set; } = new();
        public List<SpDeleteTableInfo> DeleteTables { get; set; } = new();
        public List<SpUdfInfo> Udfs { get; set; } = new();
        public bool HasTempTables { get; set; }
        public string TempTablesUsage { get; set; } = string.Empty;
        public bool HasLinkedServers { get; set; }
        public string LinkedServersUsage { get; set; } = string.Empty;
    }

    public class SpSelectTableInfo
    {
        public string TableName { get; set; } = string.Empty;
        public List<string> ReferencedColumns { get; set; } = new();
        public List<string> JoinAndFilterConditions { get; set; } = new();
    }

    public class SpInsertMappingInfo
    {
        public string TargetTable { get; set; } = string.Empty;
        public string BranchName { get; set; } = string.Empty; // e.g. "전체거래건", "부분취소건", "환불건"
        public List<ColumnMappingInfo> Mappings { get; set; } = new();
    }

    public class SpUpdateMappingInfo
    {
        public string TargetTable { get; set; } = string.Empty;
        public string BranchName { get; set; } = string.Empty; // e.g. "전체거래건", "부분취소건", "환불건"
        public List<ColumnMappingInfo> Mappings { get; set; } = new();
    }

    public class ColumnMappingInfo
    {
        public string TargetColumn { get; set; } = string.Empty;
        public string SourceExpression { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
    }

    public class SpDeleteTableInfo
    {
        public string TableName { get; set; } = string.Empty;
        public string BranchName { get; set; } = string.Empty; // e.g. "전체거래건", "부분취소건", "환불건"
        public List<string> FilterConditions { get; set; } = new();
    }

    public class SpUdfInfo
    {
        public string UdfName { get; set; } = string.Empty;
        public string CallingLocation { get; set; } = string.Empty;
        public string Purpose { get; set; } = string.Empty;
        public string ComputationLogic { get; set; } = string.Empty;
    }

    public class SpLogicInfo
    {
        public string TransactionControl { get; set; } = string.Empty;
        public List<SpLogicStep> Steps { get; set; } = new();
        public List<SpExceptionVulnerability> ExceptionVulnerabilities { get; set; } = new();
        public List<SpIsolationImplication> IsolationImplications { get; set; } = new();
        public List<string> ReturnCodes { get; set; } = new();
        public List<string> ParameterValidation { get; set; } = new();
    }

    public class SpLogicStep
    {
        public int StepNumber { get; set; }
        public string StepName { get; set; } = string.Empty;
        public string StepDescription { get; set; } = string.Empty;
    }

    public class SpExceptionVulnerability
    {
        public string VulnerabilityType { get; set; } = string.Empty;
        public string Details { get; set; } = string.Empty;
    }

    public class SpIsolationImplication
    {
        public string RiskType { get; set; } = string.Empty;
        public string Details { get; set; } = string.Empty;
    }

    public class SpVisualizationInfo
    {
        public List<MermaidNode> Nodes { get; set; } = new();
        public List<MermaidLink> Links { get; set; } = new();
    }

    public class MermaidNode
    {
        public string Id { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
    }

    public class MermaidLink
    {
        public string FromId { get; set; } = string.Empty;
        public string ToId { get; set; } = string.Empty;
        public string Condition { get; set; } = string.Empty;
    }
}
