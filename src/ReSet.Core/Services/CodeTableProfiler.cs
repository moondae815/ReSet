using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ReSet.Core.Models;
using Serilog;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 의존 테이블 중 행 수가 작은 것만 읽는다.
    ///
    /// [왜 이름으로 고르지 않는가] 종전 구현은 이름에 Code|Master|Policy|Setting|Map|
    /// Type|Group|Rate가 든 테이블을 골랐는데, 이 코퍼스에서 그 조건에 걸리는 9개가
    /// 전부 요율표였다. 'S02'를 '정산보류'로 번역하겠다는 이 기능의 간판이 0건을
    /// 겨누고 있었다는 뜻이다. 코드·마스터 테이블은 「작다」는 성질로 판정하는 편이
    /// 이름 규칙이 다른 조직에서도 선다.
    ///
    /// [부수 효과] 거래 테이블이 크기에서 걸러지므로 실거래 100행을 무조건 긁어
    /// 프롬프트에 싣던 일이 사라진다.
    /// </summary>
    public sealed class CodeTableProfiler : ICodeTableProfiler
    {
        private readonly IDbMetadataService _dbService;
        private readonly string _connectionString;
        private readonly int _rowThreshold;

        public CodeTableProfiler(IDbMetadataService dbService, string connectionString, int rowThreshold = 500)
        {
            _dbService = dbService ?? throw new ArgumentNullException(nameof(dbService));
            _connectionString = connectionString;
            _rowThreshold = rowThreshold;
        }

        public async Task<IReadOnlyList<ProfiledTable>> ProfileAsync(
            IReadOnlyList<DependencyInfo> dependencies,
            CancellationToken cancellationToken = default)
        {
            var results = new List<ProfiledTable>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var dependency in dependencies.Where(d => SqlObjectTypeClassifier.IsTableOrView(d.Type)))
            {
                var key = string.IsNullOrEmpty(dependency.Database)
                    ? $"{dependency.Schema}.{dependency.Name}"
                    : $"[{dependency.Database}].[{dependency.Schema}].[{dependency.Name}]";

                if (!seen.Add(key))
                {
                    continue;
                }

                try
                {
                    var rowCount = await _dbService.GetTableRowCountAsync(
                        _connectionString, dependency.Database, dependency.Schema, dependency.Name, cancellationToken);

                    if (rowCount > _rowThreshold)
                    {
                        continue;
                    }

                    var rows = await _dbService.GetTableDataPreviewAsync(
                        _connectionString, dependency.Database, dependency.Schema, dependency.Name,
                        _rowThreshold, cancellationToken);

                    results.Add(new ProfiledTable(key, rows.Select(ToStringRow).ToList()));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 테이블 하나를 못 읽는다고 정책 도출을 세우지 않는다. 그 값은
                    // 「의미 미상」으로 문서에 나가고, 그 사실이 배너에 실린다.
                    Log.Warning(ex, "코드 테이블 프로파일링 실패 - {Table}", key);
                }
            }

            return results;
        }

        /// <summary>매칭은 문자열 대조이므로 모든 칸을 문화권 불변 문자열로 정규화한다.</summary>
        private static IReadOnlyDictionary<string, string> ToStringRow(Dictionary<string, object> row)
        {
            var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (column, value) in row)
            {
                normalized[column] = value switch
                {
                    null or DBNull => string.Empty,
                    IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                    _ => value.ToString() ?? string.Empty,
                };
            }

            return normalized;
        }
    }
}
