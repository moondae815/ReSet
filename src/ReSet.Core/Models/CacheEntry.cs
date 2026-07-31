using System;
using System.Collections.Generic;

namespace ReSet.Core.Models
{
    public class CacheEntry
    {
        public string ProcedureName { get; set; } = string.Empty;
        public CodeObjectKey? ObjectKey { get; set; }
        public DateTime LastAnalyzed { get; set; }
        public string SourceHash { get; set; } = string.Empty;
        public Dictionary<string, string> DependencyHashes { get; set; } = new();
        public string CompositeHash { get; set; } = string.Empty;
    }

    public class CacheIndex
    {
        public Dictionary<string, CacheEntry> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
