using System.Collections.Generic;
using System.IO;
using System.Linq;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 시도별 L1 발화를 객체 산출물에 누적해 남긴다.
    ///
    /// [왜 필요한가 - 2026-09-09] 지금 이 정보는 gitignore 된 3 만 줄짜리 실행 로그에만
    /// 있다. 검사 결함 하나를 진단할 때마다 사람이 그 로그에서 실물을 오려 왔다
    /// (하루에 다섯 번). 산출물로 남으면 그 일이 사라지고, 승격기가 커밋되는 코퍼스로
    /// 옮길 수 있게 된다.
    /// </summary>
    public class L1AttemptLogTests
    {
        private static string NewTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "l1attempt-" + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Fact]
        public void Append_WritesFiringsUnderRaw()
        {
            var dir = NewTempDir();

            L1AttemptLog.Append(dir, 1, new[] { new L1Firing("CheckA", "메시지 하나") });

            var path = Path.Combine(dir, "raw", "l1-attempts.json");
            Assert.True(File.Exists(path));
            var read = L1AttemptLog.Read(path);
            var only = Assert.Single(read);
            Assert.Equal(1, only.Attempt);
            Assert.Equal("CheckA", only.CheckKey);
            Assert.Equal("메시지 하나", only.Message);
        }

        [Fact]
        public void Append_AccumulatesAcrossAttempts()
        {
            // 연속 시도 서명을 세려면 시도 1 의 발화가 시도 2 를 쓸 때 사라지면 안 된다.
            // 이 자리를 덮어쓰기로 만들면 게이트가 통째로 눈이 먼다.
            var dir = NewTempDir();

            L1AttemptLog.Append(dir, 1, new[] { new L1Firing("CheckA", "첫 판") });
            L1AttemptLog.Append(dir, 2, new[] { new L1Firing("CheckA", "둘째 판") });

            var read = L1AttemptLog.Read(Path.Combine(dir, "raw", "l1-attempts.json"));

            Assert.Equal(2, read.Count);
            Assert.Equal(new[] { 1, 2 }, read.Select(f => f.Attempt).ToArray());
        }

        // [명명 계약 - 2026-09-10 최종 리뷰 발견] Read 는 기본 옵션(대소문자 구분)으로
        // 역직렬화한다. camelCase 로 쓰인 파일을 먹이면 예외를 안 던지고 Attempt=0·
        // CheckKey=null 인 레코드를 그대로 낸다 - 실측: Pascal(count=1 Attempt=2
        // CheckKey=K) vs camel(count=1 Attempt=0 CheckKey=<null>). 나중에 쓰기 쪽
        // 명명 정책이 바뀌면 FindSelfReinforcing 이 모든 시도를 0 으로 보고 gap==1 을
        // 하나도 못 찾아 - 통째로 망가진 코퍼스에 대해 「발견 없음」을 보고한다.
        // Attempt 는 실물에서 언제나 1 이상이고 CheckKey 는 언제나 있다 - 이 계약을
        // 어기면 이 파일을 다른 손상 케이스와 같게(빈 목록) 취급해야 한다.
        [Fact]
        public void Read_DoesNotSilentlyZeroOutWhenTheFileUsesCamelCase()
        {
            var dir = NewTempDir();
            var rawDir = Path.Combine(dir, "raw");
            Directory.CreateDirectory(rawDir);
            var path = Path.Combine(rawDir, "l1-attempts.json");
            File.WriteAllText(path, "[{\"attempt\":2,\"checkKey\":\"K\",\"message\":\"m\"}]");

            var read = L1AttemptLog.Read(path);

            Assert.Empty(read);
        }

        [Fact]
        public void Append_DoesNotThrowWhenTheDirectoryCannotBeWritten()
        {
            // 관측이 파이프라인을 죽이면 안 된다. 이 저장소의 소프트 페일 관례를 따른다
            // (MechanicalValidator.Validate 가 자기 오류에 소프트 패스하는 것과 같다).
            // xunit 내장 Record 를 쓴다 - AgentProgressStoreTests.cs:170 과 같은 관례다.
            var exception = Record.Exception(() =>
                L1AttemptLog.Append("\0invalid\0", 1, new[] { new L1Firing("CheckA", "x") }));

            Assert.Null(exception);
        }
    }
}
