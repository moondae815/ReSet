using System;
using System.IO;
using System.Linq;
using Xunit;
using ReSet.Cli;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// CoverageMapCommand의 참조 폐포 dedupe 회귀 테스트.
    ///
    /// [왜 임시 디렉터리인가] 실물 output/은 .gitignore 대상이라 CI에서
    /// 비결정적이다. 여기서는 output/ 구조를 최소로 흉내 낸 픽스처를 매 테스트마다
    /// 새로 만들어 결정적으로 돌린다.
    /// </summary>
    public class CoverageMapCommandTests : IDisposable
    {
        private readonly string _tempOutputDir;

        public CoverageMapCommandTests()
        {
            _tempOutputDir = Path.Combine(Path.GetTempPath(), "ReSetCoverageMapTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempOutputDir);
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempOutputDir))
            {
                try { Directory.Delete(_tempOutputDir, true); }
                catch { /* 무시 */ }
            }
        }

        private void WriteObject(string relativeDir, string manifestJson)
        {
            var objectDir = Path.Combine(_tempOutputDir, relativeDir);
            Directory.CreateDirectory(Path.Combine(objectDir, "raw"));
            Directory.CreateDirectory(Path.Combine(objectDir, "docs"));

            File.WriteAllText(
                Path.Combine(objectDir, "raw", "metadata.json"),
                """{"Schema":"dbo","Name":"X","DdlText":"CREATE PROCEDURE dbo.X AS BEGIN PRINT 'x' END"}""");
            File.WriteAllText(Path.Combine(objectDir, "docs", "Spec.md"), "## 개요\n");
            File.WriteAllText(Path.Combine(objectDir, "raw", "dependency-manifest.json"), manifestJson);
        }

        /// <summary>
        /// 실측(POQSettlePrco20)에서 난 그 모양을 그대로 재현한다: 소비 SP 둘(dbo.X,
        /// dbo.Y)이 있고, dbo.Y의 dependency-manifest.json이 dbo.X를 DB 접두 Key
        /// ("SETTLE_POQ_DB.dbo.X.Procedure")로 참조한다. dbo.X 자신은 맨 이름
        /// ("dbo.X")으로 등록된다 - 같은 물리 디렉터리가 서로 다른 두 문자열 키
        /// 아래 들어올 수 있다는 뜻이다. 문자열 키로만 dedupe하면 폐포가 2가 아니라
        /// 3으로 부풀어 오른다(dbo.X가 두 번 실린다).
        /// </summary>
        [Fact]
        public void ResolveObjectClosure_SameObjectReferencedByTwoKeys_ShouldCountOnce()
        {
            WriteObject("Procedures/dbo.X", """{"Nodes":[]}""");
            WriteObject(
                "Procedures/dbo.Y",
                """
                {
                  "Nodes": [
                    {
                      "Key": "SETTLE_POQ_DB.dbo.X.Procedure",
                      "Status": "Succeeded",
                      "SpecPath": "../dbo.X/docs/Spec.md"
                    }
                  ]
                }
                """);

            var closure = CoverageMapCommand.ResolveObjectClosure(_tempOutputDir, new[] { "dbo.X", "dbo.Y" });

            Assert.Equal(2, closure.Count);

            var xDir = Path.GetFullPath(Path.Combine(_tempOutputDir, "Procedures", "dbo.X"));
            Assert.Single(closure, entry => Path.GetFullPath(entry.Dir) == xDir);
        }
    }
}
