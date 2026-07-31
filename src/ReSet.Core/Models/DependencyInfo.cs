using System.Collections.Generic;

namespace ReSet.Core.Models
{
    public class DependencyInfo
    {
        public CodeObjectKey? SourceObjectKey { get; set; }
        public bool IsDynamicSqlCandidate { get; set; }
        public string? Database { get; set; } = null;
        public string Schema { get; set; } = "dbo";
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public int DiscoveryDepth { get; set; }
        public List<ColumnInfo> Columns { get; set; } = new();
        public string? ReferencedDdlText { get; set; }
        public string Description { get; set; } = string.Empty;
        public List<TableIndexInfo> Indexes { get; set; } = new();
    }
}
