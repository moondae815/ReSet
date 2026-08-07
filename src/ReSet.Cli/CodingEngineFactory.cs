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

        public ICodingEngine CreateEngine(string engineName)
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
            var arguments = section["Arguments"] ?? string.Empty;

            if (string.IsNullOrEmpty(command))
            {
                throw new InvalidOperationException($"코딩 엔진 '{engineName}'의 실행 파일명(Command)이 누락되었습니다.");
            }

            // isHeadless 분기(대화형/배치 인자 분리)는 Task 4에서 다룬다. 지금은
            // 생성자 시그니처 변경에 맞춰 기존 대화형 동작만 유지한다.
            return new ExternalCliCodingEngine(engineName, command, arguments, isHeadless: false);
        }
    }
}
