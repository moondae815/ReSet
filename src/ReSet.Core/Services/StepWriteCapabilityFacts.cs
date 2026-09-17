using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Serilog;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 단계 본문이 <b>무엇이든 쓸 수 있는가</b>. 「목차가 대상 표를 비웠다」를 결함으로 들기 전에 묻는 자다
    /// (<see cref="MechanicalValidator"/>).
    ///
    /// [왜 필요한가 - 2026-09-17 실측] 빈 <c>TargetTables</c> 는 일곱 판(B11~B18) 전부 S01 하나였고, 일곱 다
    /// 읽기 전용이라 쓰기 문장이 없었다(SQL 펜스 18 · 쓰기 키워드 0 · <c>INTO</c> 0 · <c>EXEC</c> 0 · 동적 SQL 0).
    /// 「아무것도 쓰지 않는다」는 선언이 본문과 일치하면 대조할 것이 없는 것이지 검증이 막힌 것이 아니다.
    ///
    /// [보수적으로 넓다 - 모르면 「쓸 수 있다」]
    /// ① <c>INSERT</c>·<c>UPDATE</c>·<c>DELETE</c>·<c>MERGE</c>·<c>TRUNCATE</c>·<c>SELECT … INTO</c> 는 쓰기다.
    /// ② <c>EXEC</c>·<c>EXECUTE</c>(프로시저 호출·<c>sp_executesql</c>·문자열 실행)는 <b>무엇을 쓰는지 알 수 없어</b> 쓰기로 센다.
    /// ③ 파싱에 실패한 펜스도 쓰기로 센다 - 모르는 것을 「안 쓴다」로 읽으면 이 축이 느슨해지는 방향으로 기운다.
    /// ④ SQL 펜스가 하나도 없으면 쓸 수 없다고 본다(의사코드만 있는 절 - 그 절의 SQL 은 다른 검사가 요구한다).
    /// 판독: docs/audit-reports/2026-09-17-대상없는-단계-배너-사전선언.md
    /// </summary>
    internal static class StepWriteCapabilityFacts
    {
        private static readonly Regex SqlFence = new(
            @"```sql[^\n]*\n(?<body>.*?)```", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>이 단계 본문의 SQL 이 무엇이든 쓸 수 있는가.</summary>
        internal static bool CanWrite(string? stepMarkdown)
        {
            if (string.IsNullOrWhiteSpace(stepMarkdown)) return false;

            foreach (Match fence in SqlFence.Matches(stepMarkdown))
            {
                var sql = fence.Groups["body"].Value;
                if (string.IsNullOrWhiteSpace(sql)) continue;

                try
                {
                    var parser = new TSql160Parser(true);
                    using var reader = new StringReader(sql);
                    var fragment = parser.Parse(reader, out var errors);
                    if (fragment == null || (errors != null && errors.Count > 0))
                    {
                        // 모르는 펜스다(③).
                        return true;
                    }

                    var visitor = new WriteFinder();
                    fragment.Accept(visitor);
                    if (visitor.Found) return true;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[대상 없는 단계] SQL 읽기 실패 - 쓸 수 있다고 보고 진행합니다.");
                    return true;
                }
            }

            return false;
        }

        private sealed class WriteFinder : TSqlFragmentVisitor
        {
            internal bool Found { get; private set; }

            public override void Visit(InsertStatement node) => Found = true;

            public override void Visit(UpdateStatement node) => Found = true;

            public override void Visit(DeleteStatement node) => Found = true;

            public override void Visit(MergeStatement node) => Found = true;

            public override void Visit(TruncateTableStatement node) => Found = true;

            /// <summary><c>SELECT … INTO t</c> 는 표를 만든다 - ScriptDom 은 그 자리를 SelectStatement 의 Into 로 준다.</summary>
            public override void Visit(SelectStatement node)
            {
                if (node.Into != null)
                {
                    Found = true;
                }
            }

            /// <summary>프로시저 호출·동적 SQL 은 무엇을 쓰는지 알 수 없다(②).</summary>
            public override void Visit(ExecuteStatement node) => Found = true;

            public override void Visit(ExecuteAsStatement node) => Found = true;
        }
    }
}
