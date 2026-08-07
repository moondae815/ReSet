using System.Threading;
using System.Threading.Tasks;
using ReSet.Core.Models;

namespace ReSet.Core.Services
{
    public interface ICodingEngine
    {
        string Name { get; }

        /// <summary>실행 파일명 또는 절대 경로. 실패 안내문이 사용자에게 되짚어 줄 명령어다.</summary>
        string Command { get; }

        /// <summary>
        /// 외부 코딩 에이전트를 프로세스로 기동하여 마이그레이션 코드를 작성하도록 지시합니다.
        /// </summary>
        /// <param name="spDef">SP 정의 메타데이터</param>
        /// <param name="instructionsFilePath">마이그레이션 지시서 번들 경로 (*_MigrationInstructions.md)</param>
        /// <param name="targetProjectDir">코드가 구현될 대상 프로젝트 디렉터리</param>
        /// <param name="cancellationToken">작업 취소 토큰</param>
        /// <returns>기동 결과. 성공/실패 판단은 호출자가 한다.</returns>
        Task<CodegenRunResult> GenerateCodeAsync(
            SpDefinition? spDef,
            string instructionsFilePath,
            string targetProjectDir,
            CancellationToken cancellationToken);
    }
}
