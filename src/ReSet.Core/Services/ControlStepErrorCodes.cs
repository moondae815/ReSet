using System.Text.RegularExpressions;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 레거시 출신이 없는 단계가 쓰는 오류코드 대역.
    ///
    /// [왜 필요한가]
    /// 규칙 6-1은 "각 DML 앞에서 원본 오류코드로 상태 변수를 갱신하라"고 말하는데,
    /// 원본이 없는 단계에는 지킬 대상이 없다. 규약이 없으니 모델이 자기 체계를
    /// 지어냈다 - 실측(POQSettleBatch1)에서 목차가 B100·B101·B110·B120·B121·
    /// B160·B161을 발급했고 계획서에 54회 등장했다. 등장 검사는 그것들이 본문에
    /// 있는지 확인하고 통과시켰으므로, 검사가 지어낸 어휘를 인증하고 있었다.
    /// 그 어휘가 SQL로 새어 `DECLARE @v_currentStepId INT = B161`이 4회 나왔는데,
    /// B161은 해석되지 않는 식별자라 컴파일되지 않는다.
    ///
    /// [왜 이 모양인가]
    /// 모델의 `B&lt;단계번호&gt;&lt;일련&gt;`은 구조적으로 합리적이었다 - 값만 보고 어느
    /// 단계에서 죽었는지 알 수 있다. 규약도 같은 구조로 주되 T-SQL INT에 들어가는
    /// 값으로 만든다. 코퍼스 전수에서 레거시 반환 코드는 -1 ~ -201이므로 대역을
    /// 그 아래로 충분히 띄운다.
    /// </summary>
    public static class ControlStepErrorCodes
    {
        /// <summary>한 단계에 주어지는 코드 개수. `POQSettleBatch1` 하나만 봤을 때는
        /// 단계당 2개(B160·B161)였으나, 코퍼스 전체(20개 Job)로 다시 재면 최대는
        /// `POQSettleProc19/S17`의 3개(B170·B171·B172)다 - 여전히 10에는 크게
        /// 못 미친다.</summary>
        public const int BlockSize = 10;

        /// <summary>대역의 시작. 이 값 이하가 예약이다.</summary>
        private const int ReservedCeiling = -9000;

        private static readonly Regex StepNumberRegex =
            new(@"^\s*S(?<n>\d{1,3})\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// 그 단계의 블록 시작 코드. 단계 코드가 <c>S&lt;숫자&gt;</c>가 아니면 null -
        /// 번호를 못 읽으면 발급하지 않는다.
        /// </summary>
        public static int? BlockStart(string? stepCode)
        {
            if (string.IsNullOrWhiteSpace(stepCode))
            {
                return null;
            }

            var match = StepNumberRegex.Match(stepCode);
            if (!match.Success)
            {
                return null;
            }

            var n = int.Parse(match.Groups["n"].Value);
            return ReservedCeiling - (n * BlockSize);
        }

        /// <summary>그 코드가 이 단계의 블록 안인가.</summary>
        public static bool IsInBlock(string? stepCode, int code)
        {
            var start = BlockStart(stepCode);
            if (start == null)
            {
                return false;
            }

            return code <= start.Value && code > start.Value - BlockSize;
        }

        /// <summary>예약 대역에 속하는 값인가. 레거시 코드와 겹치는지 볼 때 쓴다.</summary>
        public static bool IsReserved(int code) => code <= ReservedCeiling;

        /// <summary>프롬프트에 싣는 문구. 규칙 6-1과 제어 계약 표가 함께 쓴다.</summary>
        public const string PromptClause =
            "[Control Step Error Codes] A step with NO legacy origin has no original error code to preserve. " +
            "It MUST NOT invent one - instead it uses the reserved block this document assigns to it. " +
            "Each such step owns a block of 10 negative integers derived from its step code: " +
            "block start = -9000 - (N * 10), where N is the number in `S<N>`. S01 owns -9010..-9019, " +
            "S16 owns -9160..-9169. The block start (-9160 for S16) is that step's GENERAL failure code and " +
            "MUST appear in the section; use block start minus 1, 2, ... only to distinguish further failure " +
            "points within the same step - initialize the state variable to `0`, not to the block start; " +
            "`0` means 'no failure point reached yet', and initializing to a real code makes the step report " +
            "a failure it never had. " +
            "The status code is an integer status code: declare the state variable as INT and assign only " +
            "integers from the block. NEVER assign a string code such as `N'B161'` or `N'BATCH-LOCK-001'` - " +
            "an invented string vocabulary is exactly what this rule exists to prevent, and a non-numeric " +
            "bare token such as `B161` does not even compile (`DECLARE @v INT = B161` has no such identifier). " +
            "One string stays a string: the step identifier written to `batch.BatchStepJournal.StepCode` stays a string " +
            "(`N'S01'`), because the control contract declares that column `nvarchar(10)`. That is identity, not a code. " +
            "Checkpoint/execution status values are not step error codes either: `Running`, `Succeeded`, `Failed`, " +
            "`Skipped`, `Pending`, `Held`, `Released` (the vocabulary the Batch Control Table Contract defines) " +
            "describe run state, not why a step failed, and stay as the strings that contract already defines. " +
            "Steps that DO replace a legacy procedure keep that procedure's exact original codes and MUST NOT " +
            "use this reserved band.";
    }
}
