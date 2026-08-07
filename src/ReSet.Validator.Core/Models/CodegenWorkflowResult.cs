namespace ReSet.Validator.Core.Models
{
    /// <summary>
    /// 자가 수정 워크플로우의 최종 결과.
    ///
    /// bool 하나만 돌려주면 호출부가 "검증 실패"와 "에이전트가 아예 못 돌았음"을
    /// 구분하지 못한다. 무인 배치에서 가장 알아야 할 정보가 로그 파일에만 남는다.
    /// </summary>
    /// <param name="Succeeded">모든 검증을 통과했는가</param>
    /// <param name="AbortReason">재시도 불가 실패로 루프를 끊었을 때의 안내문. 그 외에는 null</param>
    public sealed record CodegenWorkflowResult(bool Succeeded, string? AbortReason);
}
