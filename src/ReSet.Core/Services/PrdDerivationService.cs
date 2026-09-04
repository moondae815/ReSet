using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 완성된 Spec.md 하나를 요구사항 문서로 옮긴다.
    ///
    /// [왜 DB에 접속하지 않는가] 입력이 파일 하나뿐이어야 이미 쌓인 산출물에
    /// 재분석 없이 소급 적용된다. 그것이 이 기능을 1번 분석에 붙이지 않고
    /// 별도로 기동하기로 한 실질적 이유다.
    ///
    /// [왜 재호출이 한 번인가] 이 문서에는 L2 Actor-Critic 보정 루프가 없다.
    /// 수렴하지 않는 루프를 새로 만드는 대신, 한 번 되돌리고 남은 결함은
    /// 배너에 박아 사람 검토로 넘긴다.
    /// </summary>
    public sealed class PrdDerivationService : IPrdDerivationService
    {
        private readonly IAiService _aiService;

        public PrdDerivationService(IAiService aiService) =>
            _aiService = aiService ?? throw new ArgumentNullException(nameof(aiService));

        public async Task<PrdDerivationOutcome> DeriveAsync(
            string docsDirectory,
            string objectLabel,
            string? effort,
            CancellationToken cancellationToken = default)
        {
            var specPath = Path.Combine(docsDirectory, OutputPathResolver.SpecFileNamePublic);
            if (!File.Exists(specPath))
            {
                throw new FileNotFoundException("근거가 될 명세서가 없습니다.", specPath);
            }

            var specMarkdown = await File.ReadAllTextAsync(specPath, cancellationToken);

            var draft = await _aiService.GeneratePrdFromSpecAsync(
                objectLabel, specMarkdown, null, effort, cancellationToken);
            var body = draft.Content ?? string.Empty;
            var validation = PrdAttributionValidator.Validate(body, specMarkdown);

            if (!validation.IsValid)
            {
                Log.Information(
                    "PRD 귀속 검사 미통과 - 대상: {Object}, 결함 {Count}건. 교정 재호출 1회를 시도합니다.",
                    objectLabel, validation.Defects.Count);

                var retry = await _aiService.GeneratePrdFromSpecAsync(
                    objectLabel, specMarkdown, PrdAttributionReport.BuildPromptFix(validation), effort, cancellationToken);
                var retryBody = retry.Content ?? string.Empty;
                var retryValidation = PrdAttributionValidator.Validate(retryBody, specMarkdown);

                // 재시도가 더 나빠졌으면 첫 초안을 지킨다 - 결함 수가 유일하게
                // 비교 가능한 척도다.
                if (retryValidation.Defects.Count <= validation.Defects.Count)
                {
                    body = retryBody;
                    validation = retryValidation;
                }
            }

            // [순서 주의] 배너를 FormatUnverifiedDocument의 반환값 앞에 이어붙이지 않는다.
            // FormatUnverifiedDocument는 YAML 프런트매터로 시작하는데, 그 블록은 파일의
            // 맨 앞(오프셋 0)에 있을 때만 프런트매터로 파싱된다 - 앞에 배너 블록쿼트가
            // 붙으면 "---"가 프런트매터가 아니라 그냥 가로줄로 렌더링된다. 그래서
            // 배너를 body 인자 쪽으로 넣어, 프런트매터 → 메타 블록쿼트 → 귀속 배너
            // 블록쿼트 → 본문 순서가 되게 한다. 메타 블록쿼트와 배너 블록쿼트 사이의
            // 빈 줄은 MetadataHeader가 이미 자기 끝에 내고 있고, 배너 자신도 끝에
            // 빈 줄을 내므로 별도 삽입 없이 두 콜아웃이 합쳐지지 않고 갈라져 렌더링된다.
            var banner = PrdAttributionReport.BuildBanner(validation);
            var document = VerificationDocumentFormatter.FormatUnverifiedDocument(
                banner + body, null, _aiService.ProviderName, _aiService.ModelName, effort, DateTime.Now);

            var prdPath = Path.Combine(docsDirectory, OutputPathResolver.PrdFileName);
            await File.WriteAllTextAsync(prdPath, document, cancellationToken);

            Log.Information("PRD 저장 완료 - {Path} (귀속 결함 {Count}건)", prdPath, validation.Defects.Count);

            return new PrdDerivationOutcome(prdPath, validation.IsValid, validation.Defects);
        }
    }
}
