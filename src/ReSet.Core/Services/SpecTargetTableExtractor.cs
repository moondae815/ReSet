using System;
using System.Collections.Generic;
using ReSet.Core.Models;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 정적 분석에서 단계의 대상 테이블과 참조 원본을 뽑는다.
    ///
    /// 이 추출기가 존재하는 이유: 목차의 TargetTables는 AI가 채우는데, 같은 12개 SP를
    /// 두 제공자로 돌린 실측에서 7개와 17개가 나왔다. 같은 입력에 2.4배가 흔들린다.
    /// 두 회차 모두 S01을 빈 배열로 냈는데, 그 SP의 정적 분석에는 INSERT 대상 5개와
    /// DELETE 대상 5개가 들어 있었다 - 재료는 있고 목차까지 도달하지 않을 뿐이다.
    ///
    /// 오류코드와 달리 명세서 산문에서 뽑지 않는다. 대상 테이블은 파서가 AST에서
    /// 확정한 구조화된 데이터로 이미 존재하므로, 산문을 다시 해석하는 것은 정확도를
    /// 낮추기만 한다.
    /// </summary>
    public static class SpecTargetTableExtractor
    {
        /// <summary>
        /// 한 프로시저의 테이블 집합.
        /// </summary>
        /// <param name="WriteTables">INSERT/UPDATE/DELETE 대상. 하한 검사의 대조 기준이 된다.</param>
        /// <param name="ReadTables">SELECT 원본. 회차 지시서의 DDL 스코프에만 쓰인다.</param>
        public sealed record StepTableSets(
            IReadOnlyList<string> WriteTables,
            IReadOnlyList<string> ReadTables);

        public static IReadOnlyDictionary<string, StepTableSets> Extract(
            IEnumerable<SpDefinition>? definitions)
        {
            var result = new Dictionary<string, StepTableSets>(StringComparer.OrdinalIgnoreCase);
            if (definitions == null)
            {
                return result;
            }

            foreach (var definition in definitions)
            {
                if (definition == null || string.IsNullOrWhiteSpace(definition.Name))
                {
                    continue;
                }

                var analysis = definition.StaticAnalysis;
                if (analysis == null)
                {
                    continue;
                }

                var write = new List<string>();
                var writeSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                AddAll(analysis.InsertTables, write, writeSeen);
                AddAll(analysis.UpdateTables, write, writeSeen);
                AddAll(analysis.DeleteTables, write, writeSeen);

                var read = new List<string>();
                var readSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                AddAll(analysis.SelectTables, read, readSeen);

                // 둘 다 비면 키를 만들지 않는다. 빈 집합과 "그런 프로시저 없음"이
                // 같아지면 보강기가 "대상이 없는 단계"로 오해해 기존값을 지운다.
                if (write.Count == 0 && read.Count == 0)
                {
                    continue;
                }

                var key = SpecReturnCodeExtractor.BareName(definition.Name);

                // 같은 맨이름이 두 번 들어오면 덮어쓰지 않고 합친다. 덮어쓰면 앞
                // 항목의 대상이 조용히 사라진다.
                if (result.TryGetValue(key, out var existing))
                {
                    var mergedWrite = new List<string>(existing.WriteTables);
                    var mergedWriteSeen = new HashSet<string>(mergedWrite, StringComparer.OrdinalIgnoreCase);
                    AddAll(write, mergedWrite, mergedWriteSeen);

                    var mergedRead = new List<string>(existing.ReadTables);
                    var mergedReadSeen = new HashSet<string>(mergedRead, StringComparer.OrdinalIgnoreCase);
                    AddAll(read, mergedRead, mergedReadSeen);

                    result[key] = new StepTableSets(mergedWrite, mergedRead);
                    continue;
                }

                result[key] = new StepTableSets(write, read);
            }

            return result;
        }

        /// <summary>
        /// 목차의 짧은 표기("TSettleMst")와 정적 분석의 정식 표기
        /// ("SETTLE_POQ_DB.dbo.TSettleMst")를 대조하기 위한 맨 이름.
        ///
        /// 중복 제거에는 쓰지 않는다 - dbo.TPGProperty와 PaymentDB.dbo.TPGProperty는
        /// 맨 이름이 같아도 서로 다른 물리 테이블이다. 이 함수는 "모델이 선언한 이름이
        /// 추출 결과에 있는가"라는 관대한 비교에만 쓴다.
        /// </summary>
        public static string BareTableName(string tableName)
        {
            if (string.IsNullOrWhiteSpace(tableName))
            {
                return string.Empty;
            }

            var text = tableName.Trim();
            var index = text.LastIndexOf('.');
            if (index >= 0)
            {
                text = text[(index + 1)..];
            }

            return text.Trim('[', ']', ' ').ToLowerInvariant();
        }

        private static void AddAll(IEnumerable<string>? source, List<string> target, HashSet<string> seen)
        {
            if (source == null)
            {
                return;
            }

            foreach (var name in source)
            {
                if (!IsPhysicalTable(name))
                {
                    continue;
                }

                var trimmed = name.Trim();
                if (seen.Add(trimmed))
                {
                    target.Add(trimmed);
                }
            }
        }

        /// <summary>
        /// 임시 테이블(#, ##)과 테이블 변수(@)를 걸러낸다. 물리 테이블이 아니라 DDL이
        /// 없고, 검증에 걸면 존재하지 않는 요건이 되어 재생성으로 고칠 수 없다.
        /// </summary>
        private static bool IsPhysicalTable(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            var first = name.Trim()[0];
            return first != '#' && first != '@';
        }
    }
}
