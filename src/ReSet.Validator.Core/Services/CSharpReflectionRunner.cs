using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using ReSet.Validator.Core.Abstractions;
using ReSet.Validator.Core.Models;

namespace ReSet.Validator.Core.Services
{
    public class CSharpReflectionRunner : IRuntimeRunner
    {
        public string SupportedLanguage => "C#";

        public async Task<string> ExecuteAsync(string targetPath, string testInputsJson, string connectionString, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                throw new ArgumentException("타겟 경로가 지정되지 않았습니다.", nameof(targetPath));
            }

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var inputData = JsonSerializer.Deserialize<TestInputsDto>(testInputsJson, options);
            if (inputData == null || string.IsNullOrEmpty(inputData.ProcedureName))
            {
                throw new ArgumentException("유효하지 않은 테스트 파라미터 JSON 형식입니다.");
            }

            var outputResult = new ExecutionOutputDto
            {
                ProcedureName = inputData.ProcedureName,
                ExecutionResults = new List<TestCaseResultDto>()
            };

            // 1. 어셈블리 로드
            Assembly assembly;
            try
            {
                assembly = LoadAssembly(targetPath);
            }
            catch (Exception ex)
            {
                return CreateConnectionErrorResult(inputData, $"어셈블리 로드 실패: {ex.Message}");
            }

            // 2. 타겟 클래스 탐색
            // dbo.CustOrderHist 또는 CustOrderHist 등
            var className = Path.GetFileNameWithoutExtension(targetPath);
            if (className.Contains('.'))
            {
                className = className.Substring(className.LastIndexOf('.') + 1);
            }

            var targetType = assembly.GetTypes()
                .FirstOrDefault(t => t.Name.Equals(className, StringComparison.OrdinalIgnoreCase) && t.IsClass && !t.IsAbstract);

            if (targetType == null)
            {
                // 차선책: SP명으로 클래스 탐색
                var spCleanName = inputData.ProcedureName;
                if (spCleanName.Contains('.'))
                {
                    spCleanName = spCleanName.Substring(spCleanName.LastIndexOf('.') + 1);
                }
                targetType = assembly.GetTypes()
                    .FirstOrDefault(t => t.Name.Equals(spCleanName, StringComparison.OrdinalIgnoreCase) && t.IsClass && !t.IsAbstract);
            }

            if (targetType == null)
            {
                return CreateConnectionErrorResult(inputData, $"타겟 클래스 '{className}' 또는 '{inputData.ProcedureName}'을 어셈블리에서 찾을 수 없습니다.");
            }

            // 3. 테스트 케이스 반복 실행
            foreach (var testCase in inputData.TestCases)
            {
                if (cancellationToken.IsCancellationRequested) break;

                var caseResult = new TestCaseResultDto
                {
                    CaseId = testCase.CaseId,
                    ResultSets = new List<List<Dictionary<string, object>>>()
                };

                SqlConnection? conn = null;
                SqlTransaction? transaction = null;

                try
                {
                    // 커넥션 및 트랜잭션 명시적 생성
                    conn = new SqlConnection(connectionString);
                    await conn.OpenAsync(cancellationToken);
                    transaction = conn.BeginTransaction();

                    // 4. 클래스 인스턴스 생성 (의존성 주입 시도)
                    object? instance = CreateInstance(targetType, conn, transaction, connectionString);
                    if (instance == null)
                    {
                        throw new InvalidOperationException($"인스턴스를 생성할 수 없습니다: {targetType.FullName}");
                    }

                    // 5. 실행 메서드 탐색 (Execute, ExecuteAsync, Run, Invoke 등)
                    var method = FindExecuteMethod(targetType);
                    if (method == null)
                    {
                        throw new InvalidOperationException($"실행 가능한 메서드(Execute, ExecuteAsync, Run 등)를 찾을 수 없습니다: {targetType.FullName}");
                    }

                    // 6. 파라미터 매핑 및 호출
                    var parameters = MapMethodParameters(method, testCase.Parameters, conn, transaction);
                    object? invokeResult = null;
                    var rawResult = method.Invoke(instance, parameters);

                    if (rawResult != null)
                    {
                        var returnType = method.ReturnType;
                        if (typeof(Task).IsAssignableFrom(returnType))
                        {
                            var task = (Task)rawResult;
                            await task;
                            var resultProperty = task.GetType().GetProperty("Result");
                            if (resultProperty != null)
                            {
                                invokeResult = resultProperty.GetValue(task);
                            }
                            else
                            {
                                invokeResult = rawResult;
                            }
                        }
                        else if (returnType == typeof(ValueTask) || (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(ValueTask<>)))
                        {
                            var asTaskMethod = returnType.GetMethod("AsTask");
                            if (asTaskMethod != null)
                            {
                                var task = (Task?)asTaskMethod.Invoke(rawResult, null);
                                if (task != null)
                                {
                                    await task;
                                    var resultProperty = task.GetType().GetProperty("Result");
                                    if (resultProperty != null)
                                    {
                                        invokeResult = resultProperty.GetValue(task);
                                    }
                                }
                            }
                        }
                        else
                        {
                            invokeResult = rawResult;
                        }
                    }

                    // 7. 결과 덤프 (ResultSets 추출)
                    ExtractResultSets(invokeResult, caseResult.ResultSets);
                    caseResult.Status = "SUCCESS";
                }
                catch (Exception ex)
                {
                    caseResult.Status = "FAIL";
                    caseResult.ErrorCode = ex.InnerException?.Message ?? ex.Message;
                }
                finally
                {
                    // 8. 트랜잭션 강제 롤백으로 DB 변경 격리
                    if (transaction != null)
                    {
                        try { await transaction.RollbackAsync(cancellationToken); } catch { }
                        transaction.Dispose();
                    }
                    if (conn != null)
                    {
                        try { await conn.CloseAsync(); } catch { }
                        conn.Dispose();
                    }
                }

                outputResult.ExecutionResults.Add(caseResult);
            }

            return JsonSerializer.Serialize(outputResult, new JsonSerializerOptions { WriteIndented = true });
        }

        private Assembly LoadAssembly(string targetPath)
        {
            if (Path.GetExtension(targetPath).Equals(".dll", StringComparison.OrdinalIgnoreCase))
            {
                return Assembly.LoadFrom(targetPath);
            }

            // 만약 소스코드(.cs) 경로가 넘어왔다면, 해당 프로젝트 폴더나 상위 폴더의 bin 디렉토리에서 컴파일된 DLL 탐색
            var fileName = Path.GetFileNameWithoutExtension(targetPath);
            var searchDir = Path.GetDirectoryName(targetPath);

            // 소스코드 디렉토리 근처 혹은 상위 bin/Debug/ 폴더 탐색
            while (!string.IsNullOrEmpty(searchDir))
            {
                var binDir = Path.Combine(searchDir, "bin");
                if (Directory.Exists(binDir))
                {
                    var dllFiles = Directory.GetFiles(binDir, $"{fileName}.dll", SearchOption.AllDirectories);
                    if (dllFiles.Length > 0)
                    {
                        return Assembly.LoadFrom(dllFiles[0]);
                    }
                    
                    // 프로젝트 산출물 전체 .dll 중 유력한 후보 탐색
                    var anyDlls = Directory.GetFiles(binDir, "*.dll", SearchOption.AllDirectories)
                        .Where(f => !f.Contains("ref/"))
                        .ToArray();
                    if (anyDlls.Length > 0)
                    {
                        // 일단 가장 최근에 쓰여진 dll 로딩
                        var latestDll = anyDlls.OrderByDescending(File.GetLastWriteTime).First();
                        return Assembly.LoadFrom(latestDll);
                    }
                }
                searchDir = Path.GetDirectoryName(searchDir);
            }

            throw new FileNotFoundException($"해당 소스코드와 매칭되는 빌드 어셈블리(DLL)를 찾을 수 없습니다. 프로젝트를 먼저 빌드(Build)해 주십시오: {targetPath}");
        }

        private object? CreateInstance(Type type, SqlConnection conn, SqlTransaction trans, string connStr)
        {
            // 1. Connection과 Transaction을 모두 받는 생성자
            var ctor = type.GetConstructor(new[] { typeof(SqlConnection), typeof(SqlTransaction) });
            if (ctor != null) return ctor.Invoke(new object[] { conn, trans });

            // 2. DbConnection과 DbTransaction을 받는 생성자
            ctor = type.GetConstructors().FirstOrDefault(c =>
            {
                var p = c.GetParameters();
                return p.Length == 2 && 
                       typeof(IDbConnection).IsAssignableFrom(p[0].ParameterType) && 
                       typeof(IDbTransaction).IsAssignableFrom(p[1].ParameterType);
            });
            if (ctor != null) return ctor.Invoke(new object[] { conn, trans });

            // 3. Connection만 받는 생성자
            ctor = type.GetConstructor(new[] { typeof(SqlConnection) });
            if (ctor != null) return ctor.Invoke(new object[] { conn });

            // 4. DbConnection만 받는 생성자
            ctor = type.GetConstructors().FirstOrDefault(c =>
            {
                var p = c.GetParameters();
                return p.Length == 1 && typeof(IDbConnection).IsAssignableFrom(p[0].ParameterType);
            });
            if (ctor != null) return ctor.Invoke(new object[] { conn });

            // 5. connectionString 문자열을 받는 생성자
            ctor = type.GetConstructor(new[] { typeof(string) });
            if (ctor != null) return ctor.Invoke(new object[] { connStr });

            // 6. 기본 생성자
            ctor = type.GetConstructor(Type.EmptyTypes);
            if (ctor != null) return ctor.Invoke(null);

            return null;
        }

        private MethodInfo? FindExecuteMethod(Type type)
        {
            var candidateNames = new[] { "ExecuteAsync", "Execute", "Run", "Invoke", "Query", "Process" };
            foreach (var name in candidateNames)
            {
                var method = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (method != null) return method;
            }
            // 그 외 public 인스턴스 메서드 중 첫 번째
            return type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.DeclaringType == type);
        }

        private object?[] MapMethodParameters(MethodInfo method, Dictionary<string, object?>? inputParams, SqlConnection conn, SqlTransaction trans)
        {
            var methodParams = method.GetParameters();
            var args = new object?[methodParams.Length];

            for (int i = 0; i < methodParams.Length; i++)
            {
                var param = methodParams[i];
                
                // 1. Connection / Transaction 주입
                if (typeof(IDbConnection).IsAssignableFrom(param.ParameterType))
                {
                    args[i] = conn;
                    continue;
                }
                if (typeof(IDbTransaction).IsAssignableFrom(param.ParameterType))
                {
                    args[i] = trans;
                    continue;
                }

                // 2. 일반 입력 파라미터 매핑
                if (inputParams != null)
                {
                    // 이름 매칭 (대소문자 및 @ 무시)
                    var cleanName = param.Name?.Replace("@", "") ?? string.Empty;
                    var matchKey = inputParams.Keys.FirstOrDefault(k => k.Replace("@", "").Equals(cleanName, StringComparison.OrdinalIgnoreCase));

                    if (matchKey != null)
                    {
                        var rawVal = inputParams[matchKey];
                        if (rawVal == null)
                        {
                            args[i] = null;
                        }
                        else
                        {
                            try
                            {
                                // 타입 변환
                                var targetType = Nullable.GetUnderlyingType(param.ParameterType) ?? param.ParameterType;
                                if (rawVal is JsonElement element)
                                {
                                    args[i] = ConvertJsonElement(element, targetType);
                                }
                                else
                                {
                                    args[i] = Convert.ChangeType(rawVal, targetType);
                                }
                            }
                            catch
                            {
                                args[i] = rawVal;
                            }
                        }
                        continue;
                    }
                }

                // 매핑 실패 시 디폴트 값
                args[i] = param.HasDefaultValue ? param.DefaultValue : null;
            }

            return args;
        }

        private object? ConvertJsonElement(JsonElement element, Type targetType)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    if (targetType == typeof(Guid)) return Guid.Parse(element.GetString()!);
                    if (targetType == typeof(DateTime)) return DateTime.Parse(element.GetString()!);
                    return element.GetString();
                case JsonValueKind.Number:
                    if (targetType == typeof(int)) return element.GetInt32();
                    if (targetType == typeof(long)) return element.GetInt64();
                    if (targetType == typeof(double)) return element.GetDouble();
                    if (targetType == typeof(decimal)) return element.GetDecimal();
                    if (targetType == typeof(float)) return element.GetSingle();
                    return element.GetDouble();
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                case JsonValueKind.Null:
                    return null;
                default:
                    return element.GetRawText();
            }
        }

        private void ExtractResultSets(object? invokeResult, List<List<Dictionary<string, object>>> resultSets)
        {
            if (invokeResult == null) return;

            // 1. DataSet 처리
            if (invokeResult is DataSet ds)
            {
                foreach (DataTable table in ds.Tables)
                {
                    resultSets.Add(ConvertDataTable(table));
                }
                return;
            }

            // 2. DataTable 처리
            if (invokeResult is DataTable dt)
            {
                resultSets.Add(ConvertDataTable(dt));
                return;
            }

            // 3. IEnumerable 처리 (List<DTO> 등)
            if (invokeResult is System.Collections.IEnumerable enumerable && !(invokeResult is string))
            {
                var rows = new List<Dictionary<string, object>>();
                foreach (var item in enumerable)
                {
                    if (item == null) continue;
                    
                    var row = new Dictionary<string, object>();
                    var properties = item.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
                    foreach (var prop in properties)
                    {
                        row[prop.Name] = prop.GetValue(item) ?? null!;
                    }
                    rows.Add(row);
                }
                resultSets.Add(rows);
                return;
            }

            // 4. 단일 객체 (DTO) 처리
            var singleRow = new Dictionary<string, object>();
            var props = invokeResult.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
            foreach (var prop in props)
            {
                singleRow[prop.Name] = prop.GetValue(invokeResult) ?? null!;
            }
            resultSets.Add(new List<Dictionary<string, object>> { singleRow });
        }

        private List<Dictionary<string, object>> ConvertDataTable(DataTable table)
        {
            var rows = new List<Dictionary<string, object>>();
            foreach (DataRow dr in table.Rows)
            {
                var row = new Dictionary<string, object>();
                foreach (DataColumn col in table.Columns)
                {
                    row[col.ColumnName] = dr[col] == DBNull.Value ? null! : dr[col];
                }
                rows.Add(row);
            }
            return rows;
        }

        private string CreateConnectionErrorResult(TestInputsDto inputData, string message)
        {
            var output = new ExecutionOutputDto
            {
                ProcedureName = inputData.ProcedureName,
                ExecutionResults = inputData.TestCases.Select(tc => new TestCaseResultDto
                {
                    CaseId = tc.CaseId,
                    Status = "FAIL",
                    ErrorCode = message
                }).ToList()
            };
            return JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true });
        }
    }
}
