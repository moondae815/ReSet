using System;
using System.Collections.Generic;

namespace ReSet.Core.Models
{
    public class CacheEntry
    {
        public string ProcedureName { get; set; } = string.Empty;
        // 캐시 스키마 버전. 이 키가 없는 레거시 JSON은 0으로 역직렬화되어 무효 처리된다.
        // 수정 이전 코드는 검증 종료 상태와 무관하게 엔트리를 기록했고, 어느 것이
        // 미검증이었는지 판별할 정보가 저장되어 있지 않다.
        public int FormatVersion { get; set; }
        public CodeObjectKey? ObjectKey { get; set; }
        public DateTime LastAnalyzed { get; set; }
        public string SourceHash { get; set; } = string.Empty;
        public Dictionary<string, string> DependencyHashes { get; set; } = new();
        public string CompositeHash { get; set; } = string.Empty;
        public string SpecContentHash { get; set; } = string.Empty;
        public int SpecContentLength { get; set; }
        public string OriginalSpecPath { get; set; } = string.Empty;
    }

    public class CacheIndex
    {
        public Dictionary<string, CacheEntry> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
