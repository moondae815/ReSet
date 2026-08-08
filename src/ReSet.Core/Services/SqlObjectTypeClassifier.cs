using System;
using ReSet.Core.Models;

namespace ReSet.Core.Services
{
    /// <summary>
    /// sys 카탈로그의 타입 문자열이 테이블/뷰인지 코드 객체인지 판정한다.
    ///
    /// 부분 문자열 판정을 한곳에 모으는 이유: "SQL_TABLE_VALUED_FUNCTION"은
    /// "TABLE"을 포함한다. 호출부마다 따로 판정하면 한쪽만 가드를 갖게 되고,
    /// 실제로 그렇게 되어 TVF의 DDL이 수집되지 않았다.
    /// </summary>
    public static class SqlObjectTypeClassifier
    {
        public static bool IsCodeObject(string? sqlObjectType) =>
            sqlObjectType?.Contains("FUNCTION", StringComparison.OrdinalIgnoreCase) == true ||
            sqlObjectType?.Contains("PROCEDURE", StringComparison.OrdinalIgnoreCase) == true;

        public static bool IsTableOrView(string? sqlObjectType) =>
            !IsCodeObject(sqlObjectType) &&
            (sqlObjectType?.Contains("TABLE", StringComparison.OrdinalIgnoreCase) == true ||
             sqlObjectType?.Contains("VIEW", StringComparison.OrdinalIgnoreCase) == true);

        public static CodeObjectType ResolveCodeObjectType(string? sqlObjectType)
        {
            if (sqlObjectType?.Contains("FUNCTION", StringComparison.OrdinalIgnoreCase) == true)
            {
                return CodeObjectType.Function;
            }

            if (sqlObjectType?.Contains("PROCEDURE", StringComparison.OrdinalIgnoreCase) == true)
            {
                return CodeObjectType.Procedure;
            }

            return CodeObjectType.Unresolved;
        }
    }
}
