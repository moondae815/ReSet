using System;
using System.IO;
using System.Linq;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 「<c>Append</c> 가 <b>못 읽은</b> 파일을 덮어쓰지 않는가.」
    ///
    /// [무엇이 결함이었나 - 2026-09-10] <c>Append</c> 는 기존 파일을 <c>Read</c> 로 읽어
    /// 누적하는데, <c>Read</c> 는 명명 계약 위반을 만나면 <b>빈 목록</b>을 낸다. 그래서
    /// <c>Append</c> 가 「없어서 비었다」와 「못 읽어서 비었다」를 구분하지 못하고, 후자에서
    /// 새 시도만 담아 덮어썼다 - <b>계약 위반 전의 시도들이 사라진다.</b>
    ///
    /// 이 클래스의 주석이 그 자리를 미리 적어 뒀고(「도달 경로가 생기면 이 문단을 다시 읽고
    /// 고쳐라」), <c>Run</c> 을 명명 계약에 넣은 <c>17fe7e56</c> 이 그 도달 경로를 만들었다.
    /// 실물에서 실제로 났다 - <c>EXCEPTION_PROC</c> 의 아침 판 여섯 항목이 사라졌다.
    ///
    /// [★ 오라클 - 비순환] 「못 읽는 파일」을 지어내지 않는다. 재료는 <b>2026-09-10 오전까지
    /// 파이프라인이 실제로 쓰던 형식</b>(<c>Run</c> 항이 없는 레코드)이고,
    /// <c>docs/audit-reports/evidence/</c> 의 두 원본이 그 형식이다.
    ///
    /// 사전선언: <c>docs/audit-reports/2026-09-10-누적-소실-사전선언.md</c>
    /// </summary>
    public class L1AttemptLogOverwriteGuardTests
    {
        /// <summary>2026-09-10 오전까지 쓰이던 실물 형식 — <c>Run</c> 항이 없다.</summary>
        private const string LegacyShapeWithoutRun =
            """[{"Attempt":1,"CheckKey":"CheckA","Message":"옛 판 발화"}]""";

        private static string NewTempObjectDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "reset-overwrite-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(dir, "raw"));
            return dir;
        }

        private static string RawPath(string objectDir) =>
            Path.Combine(objectDir, "raw", L1AttemptLog.FileName);

        [Fact]
        public void TheLegacyShapeIsReallyUnreadable()
        {
            // 정박 - 아래 시험의 전건이다. 이 파일이 읽히게 되면 아래 시험이 무엇을
            // 재는지 모른 채 초록이 된다.
            var dir = NewTempObjectDir();
            try
            {
                File.WriteAllText(RawPath(dir), LegacyShapeWithoutRun);

                Assert.Empty(L1AttemptLog.Read(RawPath(dir)));
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void Append_DoesNotOverwriteAFileItCouldNotRead()
        {
            // ★ 이 판의 본론. 못 읽는 파일이면 아무것도 쓰지 않고 그대로 둔다.
            var dir = NewTempObjectDir();
            try
            {
                File.WriteAllText(RawPath(dir), LegacyShapeWithoutRun);

                L1AttemptLog.Append(dir, 1, new[] { new L1Firing("CheckB", "새 발화") });

                Assert.Equal(LegacyShapeWithoutRun, File.ReadAllText(RawPath(dir)));
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        // ── 부작용 축: 가드가 과하면 정상 경로를 막는다. 셋을 갈라 둔다. ──

        [Fact]
        public void Append_StillWritesWhenTheFileDoesNotExist()
        {
            var dir = NewTempObjectDir();
            try
            {
                L1AttemptLog.Append(dir, 1, new[] { new L1Firing("CheckA", "첫 발화") });

                var only = Assert.Single(L1AttemptLog.Read(RawPath(dir)));
                Assert.Equal(1, only.Run);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void Append_StillWritesWhenTheFileIsAnEmptyArray()
        {
            // ★ 이 판의 진짜 위험. 「읽은 게 0」만으로 판정하면 빈 파일에서 영영 못 쓰게
            // 된다 - 「비었다」는 계약 위반이 아니다.
            var dir = NewTempObjectDir();
            try
            {
                File.WriteAllText(RawPath(dir), "[]");

                L1AttemptLog.Append(dir, 1, new[] { new L1Firing("CheckA", "첫 발화") });

                Assert.Single(L1AttemptLog.Read(RawPath(dir)));
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void Append_StillAccumulatesOnAWellFormedFile()
        {
            var dir = NewTempObjectDir();
            try
            {
                L1AttemptLog.Append(dir, 1, new[] { new L1Firing("CheckA", "첫째") });
                L1AttemptLog.Append(dir, 2, new[] { new L1Firing("CheckA", "둘째") });

                var read = L1AttemptLog.Read(RawPath(dir));
                Assert.Equal(2, read.Count);
                Assert.Equal(new[] { 1, 2 }, read.Select(f => f.Attempt));
                Assert.All(read, f => Assert.Equal(1, f.Run));
            }
            finally { Directory.Delete(dir, recursive: true); }
        }
    }
}
