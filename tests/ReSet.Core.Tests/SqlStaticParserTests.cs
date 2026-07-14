using System;
using Xunit;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class SqlStaticParserTests
    {
        [Fact]
        public void Analyze_WithValidStoredProcedure_ShouldExtractTablesAndControlFlow()
        {
            // Arrange
            var ddlText = @"
CREATE PROCEDURE dbo.CalculateBonus
    @EmployeeID INT,
    @Year INT
AS
BEGIN
    SET NOCOUNT ON;

    -- 임시 테이블 생성 및 사용
    CREATE TABLE #TempBonus (
        EmpID INT,
        BonusAmount DECIMAL(18,2)
    );

    IF @Year >= 2026
    BEGIN
        INSERT INTO dbo.AuditLog (LogDate, Details) VALUES (GETDATE(), 'Year check passed');

        -- Nested IF (중첩 조건)
        IF @EmployeeID > 100
        BEGIN
            INSERT INTO #TempBonus (EmpID, BonusAmount)
            SELECT e.EmployeeID, 1000.00
            FROM dbo.Employees e
            JOIN dbo.Departments d ON e.DeptID = d.ID
            WHERE e.EmployeeID = @EmployeeID;
        end
    END
    ELSE
    BEGIN
        DELETE FROM dbo.ArchiveLog WHERE TargetYear < @Year;

        INSERT INTO #TempBonus (EmpID, BonusAmount)
        SELECT EmployeeID, 500.00
        FROM dbo.Employees
        WHERE EmployeeID = @EmployeeID;
    END

    -- WHILE 루프 예시
    DECLARE @Counter INT = 0;
    WHILE @Counter < 5
    BEGIN
        UPDATE dbo.AuditLog
        SET LogDate = GETDATE()
        WHERE ID = @Counter;

        SET @Counter = @Counter + 1;
    END

    SELECT * FROM #TempBonus;
    DROP TABLE #TempBonus;
END;
";
            var parser = new SqlStaticParser();

            // Act
            var result = parser.Analyze(ddlText);

            // Assert
            Assert.True(result.IsParsedSuccessfully);
            Assert.Null(result.ParserWarningMessage);

            // 참조 테이블 검증
            Assert.Contains("dbo.Employees", result.ReferencedTables);
            Assert.Contains("dbo.Departments", result.ReferencedTables);
            Assert.Contains("dbo.AuditLog", result.ReferencedTables);
            Assert.Contains("dbo.ArchiveLog", result.ReferencedTables);

            // CRUD 분류 검증
            Assert.Contains("dbo.Employees", result.SelectTables);
            Assert.Contains("dbo.Departments", result.SelectTables);
            Assert.Contains("dbo.AuditLog", result.InsertTables);
            Assert.Contains("dbo.AuditLog", result.UpdateTables);
            Assert.Contains("dbo.ArchiveLog", result.DeleteTables);

            // 임시 테이블 검증
            Assert.Contains("#TempBonus", result.CreatedTempTables);

            // 제어 흐름 및 중첩 들여쓰기 검증
            Assert.NotEmpty(result.ControlFlowSummary);
            // Outer IF (들여쓰기 없음)
            Assert.Contains(result.ControlFlowSummary, s => s.StartsWith("Line") && s.Contains("IF") && s.Contains("@Year >= 2026"));
            // Inner IF (공백 2칸 들여쓰기 존재)
            Assert.Contains(result.ControlFlowSummary, s => s.StartsWith("  Line") && s.Contains("IF") && s.Contains("@EmployeeID > 100"));
            // WHILE (들여쓰기 없음)
            Assert.Contains(result.ControlFlowSummary, s => s.StartsWith("Line") && s.Contains("WHILE") && s.Contains("@Counter < 5"));
        }

        [Fact]
        public void Analyze_WithInvalidSqlSyntax_ShouldSoftFailAndReturnErrors()
        {
            // Arrange
            var invalidDdl = @"
CREATE PROCEDURE dbo.BadProc
AS
BEGIN
    -- 의도적인 문법 에러 (SELECT 절에 FROM 생략 및 콤마 오류)
    SELECT Col1 Col2 dbo.MyTable;
END;
";
            var parser = new SqlStaticParser();

            // Act
            var result = parser.Analyze(invalidDdl);

            // Assert
            Assert.False(result.IsParsedSuccessfully);
            Assert.NotNull(result.ParserWarningMessage);
            Assert.Contains("T-SQL 구문 오류 감지", result.ParserWarningMessage);
            Assert.Empty(result.ReferencedTables);
        }

        [Fact]
        public void Analyze_WithEmptyDdl_ShouldSoftFailGracefully()
        {
            // Arrange
            var parser = new SqlStaticParser();

            // Act
            var result = parser.Analyze(string.Empty);

            // Assert
            Assert.False(result.IsParsedSuccessfully);
            Assert.Equal("DDL 텍스트가 비어 있습니다.", result.ParserWarningMessage);
        }

        [Fact]
        public void Analyze_WithDynamicSql_ShouldDetectAndReportWarnings()
        {
            // Arrange
            var ddlText = @"
CREATE PROCEDURE dbo.ExecuteDynamic
    @Query NVARCHAR(MAX)
AS
BEGIN
    -- 1. sp_executesql 감지
    EXEC sp_executesql @Query;

    -- 2. EXEC (@Query) 감지
    EXEC (@Query);
END;
";
            var parser = new SqlStaticParser();

            // Act
            var result = parser.Analyze(ddlText);

            // Assert
            Assert.True(result.IsParsedSuccessfully);
            Assert.NotEmpty(result.ControlFlowSummary);
            Assert.Contains(result.ControlFlowSummary, s => s.Contains("sp_executesql 동적 SQL 실행 감지됨"));
            Assert.Contains(result.ControlFlowSummary, s => s.Contains("EXEC (@SQL) 동적 SQL 문자열 실행 감지됨"));
        }

        [Fact]
        public void Analyze_WithDifferentCompatibilityLevels_ShouldGenerateParserCorrectly()
        {
            // Arrange
            var ddlText = @"
CREATE PROCEDURE dbo.SimpleProc
AS
BEGIN
    SELECT 1;
END;
";
            var parser = new SqlStaticParser();

            // Act & Assert
            // 1. 구버전 호환성 수준 (Version110 - SQL Server 2012)
            var result110 = parser.Analyze(ddlText, 110);
            Assert.True(result110.IsParsedSuccessfully);

            // 2. 신버전 호환성 수준 (Version160 - SQL Server 2022)
            var result160 = parser.Analyze(ddlText, 160);
            Assert.True(result160.IsParsedSuccessfully);
        }

        [Fact]
        public void Analyze_WithLinkedServerAndUdf_ShouldDetectThemCorrectly()
        {
            // Arrange
            var ddlText = @"
CREATE PROCEDURE dbo.ProcessRemoteOrder
AS
BEGIN
    -- 1. UDF 함수 호출 (dbo.fn_CalculateTax)
    DECLARE @Tax DECIMAL(18,2);
    SET @Tax = dbo.fn_CalculateTax(100);

    -- 2. Linked Server 원격 참조 테이블 액세스 (MyServer.RemoteDb.dbo.Orders)
    SELECT * 
    FROM MyServer.RemoteDb.dbo.Orders 
    WHERE OrderID = 1001;
END;
";
            var parser = new SqlStaticParser();

            // Act
            var result = parser.Analyze(ddlText);

            // Assert
            Assert.True(result.IsParsedSuccessfully);
            
            // UDF 감지 검증
            Assert.Contains("dbo.fn_CalculateTax", result.ReferencedFunctions);
            
            // Linked Server 감지 검증
            Assert.Contains("MyServer.RemoteDb.dbo.Orders", result.LinkedServerReferences);
            Assert.Contains(result.ControlFlowSummary, s => s.Contains("Linked Server 원격 테이블 참조 감지됨") && s.Contains("MyServer.RemoteDb.dbo.Orders"));
        }

        [Fact]
        public void Analyze_WithMultiLevelNestedControlFlow_ShouldApplyCorrectIndenting()
        {
            // Arrange
            var ddlText = @"
CREATE PROCEDURE dbo.ComplexControl
AS
BEGIN
    IF (1 = 1)
    BEGIN
        WHILE (2 = 2)
        BEGIN
            IF (3 = 3)
            BEGIN
                SELECT 1;
            END
        END
    END
END;
";
            var parser = new SqlStaticParser();

            // Act
            var result = parser.Analyze(ddlText);

            // Assert
            Assert.True(result.IsParsedSuccessfully);
            Assert.Equal(3, result.ControlFlowSummary.Count);

            // 들여쓰기 깊이 검증 (0칸, 2칸, 4칸)
            Assert.Contains("IF", result.ControlFlowSummary[0]);
            Assert.StartsWith("  ", result.ControlFlowSummary[1]); // 2칸 들여쓰기
            Assert.Contains("WHILE", result.ControlFlowSummary[1]);
            Assert.StartsWith("    ", result.ControlFlowSummary[2]); // 4칸 들여쓰기
            Assert.Contains("IF", result.ControlFlowSummary[2]);
        }

        [Fact]
        public void Analyze_WithAliasesAndInsertTarget_ShouldResolveColumnsCorrectly()
        {
            // Arrange
            var ddlText = @"
CREATE PROCEDURE dbo.TestColumnResolution
AS
BEGIN
    INSERT INTO dbo.TSettleMst (YMD, AYMD, TxAmt)
    SELECT @pi_strYMD, A.YMD, B.TxAmt
    FROM PaymentDB.dbo.TTxMst A
    JOIN SETTLE_POQ_DB.dbo.TSettleMst B ON A.PLTID = B.PLTID;
END;
";
            var parser = new SqlStaticParser();

            // Act
            var result = parser.Analyze(ddlText);

            // Assert
            Assert.True(result.IsParsedSuccessfully);
            
            // Check that SELECT columns with aliases are correctly resolved and associated
            Assert.True(result.ReferencedColumnsPerTable.ContainsKey("PaymentDB.dbo.TTxMst"));
            var tTxMstCols = result.ReferencedColumnsPerTable["PaymentDB.dbo.TTxMst"];
            Assert.Contains("YMD", tTxMstCols);
            Assert.Contains("PLTID", tTxMstCols);

            Assert.True(result.ReferencedColumnsPerTable.ContainsKey("SETTLE_POQ_DB.dbo.TSettleMst"));
            var tSettleMstCols = result.ReferencedColumnsPerTable["SETTLE_POQ_DB.dbo.TSettleMst"];
            Assert.Contains("TxAmt", tSettleMstCols);
            Assert.Contains("PLTID", tSettleMstCols);

            // Check that INSERT columns are correctly associated with the insert target
            Assert.True(result.ReferencedColumnsPerTable.ContainsKey("dbo.TSettleMst"));
            var targetCols = result.ReferencedColumnsPerTable["dbo.TSettleMst"];
            Assert.Contains("YMD", targetCols);
            Assert.Contains("AYMD", targetCols);
            Assert.Contains("TxAmt", targetCols);
        }
    }
}
