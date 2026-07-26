using System;
using System.Collections.Generic;

namespace ReSet.Core.Models
{
    public class DbSnapshot
    {
        public DateTime ExportedAt { get; set; }
        public string Server { get; set; } = string.Empty;
        public string Database { get; set; } = string.Empty;
        public Dictionary<string, SpDefinition> StoredProcedures { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
