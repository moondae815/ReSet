using System;
using System.IO;
using Xunit;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 코퍼스 경로 판정 두 단계를 임시 트리로 고정한다.
    ///
    /// [왜 이 시험이 필요한가 - 2026-09-07]
    /// 예전 <c>CorpusPaths.RepoRoot()</c>는 저장소 루트를
    /// <c>output/Procedures/dbo.UP_UTIL_SETTLE_EXCEPTION_PROC/raw/metadata.json</c> 하나로
    /// 잡았다. 그 파일은 <b>재생성 산출물</b>이라, 재생성 창에 테스트가 돌면 루트가 빈
    /// 문자열이 되고 그것을 전건으로 쓰는 <c>Skip.If</c> 17 자리가 통째로 조용해진다.
    /// 저장소 밖 워크트리에서 그 파일 하나만 없애고 재니 <b>건너뜀 20 · 실패 0 ·
    /// 「통과!」</b>가 나왔다(사전선언 §1).
    ///
    /// 남은 탐지기가 「건너뜀 0」뿐인데 이 고장이 만드는 것이 바로 그 건너뜀이다 -
    /// 탐지기가 폭발 반경 안에 있었다. 그래서 이 시험은 <b>코퍼스 유무와 무관하게</b>
    /// 도는 임시 트리에서 판정한다. 반경 밖이라야 탐지기다.
    /// </summary>
    public sealed class CorpusPathsTests : IDisposable
    {
        private readonly string _temp;

        public CorpusPathsTests()
        {
            _temp = Path.Combine(Path.GetTempPath(), "reset-corpus-paths-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_temp);
        }

        public void Dispose()
        {
            if (Directory.Exists(_temp))
            {
                Directory.Delete(_temp, recursive: true);
            }
        }

        /// <summary>커밋된 표지를 놓는다 - 이것이 새 ① 단계의 자다.</summary>
        private string MakeRepo(string name)
        {
            var root = Path.Combine(_temp, name);
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "ReSet.slnx"), "<Solution />");
            return root;
        }

        [Fact]
        public void 요구1_루트는_output이_아예_없어도_커밋된_표지로_잡힌다()
        {
            var root = MakeRepo("no-output");

            Assert.Equal(root, CorpusPaths.RepoRoot(root));
        }

        [Fact]
        public void 요구2_옛_정박_파일이_없어도_코퍼스는_있다로_판정된다()
        {
            // 재생성 창의 모양: 코퍼스는 그대로 있고 그 한 파일만 아직 안 쓰였다.
            var root = MakeRepo("regenerating");
            Directory.CreateDirectory(Path.Combine(root, "output", "Procedures", "dbo.SOMETHING_ELSE"));

            Assert.Equal(root, CorpusPaths.RepoRootIfCorpusPresent(root));
        }

        [Fact]
        public void 요구3_output이_없으면_코퍼스는_없다로_판정된다()
        {
            // 건너뜀이 정당한 유일한 경우 - 심링크를 안 건 워크트리다.
            var root = MakeRepo("no-corpus");

            Assert.Equal(string.Empty, CorpusPaths.RepoRootIfCorpusPresent(root));
        }

        [Fact]
        public void 요구4_bin_아래_얕은_스크래치_output에서_멈추지_않는다()
        {
            var root = MakeRepo("with-scratch");
            Directory.CreateDirectory(Path.Combine(root, "output", "Procedures", "dbo.REAL"));
            var bin = Path.Combine(root, "tests", "ReSet.Core.Tests", "bin", "Debug", "net10.0");
            Directory.CreateDirectory(Path.Combine(bin, "output", "Procedures", "dbo.USP_Root"));

            Assert.Equal(root, CorpusPaths.RepoRootIfCorpusPresent(bin));
        }

        [Fact]
        public void 요구5_루트_탐색은_RepoPaths와_같은_자를_쓴다()
        {
            Assert.Equal(RepoPaths.FindRepoRoot(), CorpusPaths.RepoRoot());
        }
    }
}
