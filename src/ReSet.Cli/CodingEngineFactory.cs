using Microsoft.Extensions.Configuration;
using ReSet.Core.Services;
using System;

namespace ReSet.Cli
{
    public class CodingEngineFactory
    {
        private readonly IConfiguration _configuration;

        public CodingEngineFactory(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        public ICodingEngine CreateEngine(string engineName, bool isBatchMode)
        {
            if (string.IsNullOrEmpty(engineName))
            {
                throw new ArgumentException("코딩 엔진명이 지정되지 않았습니다.", nameof(engineName));
            }

            var section = _configuration.GetSection($"CodegenSettings:Engines:{engineName}");
            if (!section.Exists())
            {
                throw new InvalidOperationException($"설정 파일에서 코딩 엔진 '{engineName}'의 구성을 찾을 수 없습니다.");
            }

            var command = section["Command"];
            if (string.IsNullOrEmpty(command))
            {
                throw new InvalidOperationException($"코딩 엔진 '{engineName}'의 실행 파일명(Command)이 누락되었습니다.");
            }

            var interactiveArguments = section["Arguments"] ?? string.Empty;
            var batchArguments = section["BatchArguments"] ?? string.Empty;

            // 대화형 인자로 폴백하지 않는다. 대화형 형식은 무인 실행에서 TTY를 열지 못해
            // 종료 코드 0인 채로 조용히 실패한다.
            if (isBatchMode && string.IsNullOrWhiteSpace(batchArguments))
            {
                throw new InvalidOperationException(
                    $"'{engineName}' 엔진은 무인 배치 모드를 지원하지 않습니다(BatchArguments 미지정). " +
                    $"CodegenSettings:Engine을 배치를 지원하는 엔진으로 변경하거나, " +
                    $"CodegenSettings:Engines:{engineName}:BatchArguments를 채우십시오.");
            }

            var arguments = isBatchMode ? batchArguments : interactiveArguments;

            return new ExternalCliCodingEngine(engineName, command, arguments, isHeadless: isBatchMode);
        }
    }
}
