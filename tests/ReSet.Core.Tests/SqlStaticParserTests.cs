using System;
using System.Linq;
using Xunit;
using ReSet.Core.Models;
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
        public void Parse_WithLowCompatibilityLevel_UsesTSql100Parser()
        {
            var ddlText = "CREATE PROCEDURE dbo.USP_Test AS SELECT 1";
            var parser = new SqlStaticParser();
            
            // Should fallback to TSql100Parser at line 89
            var result = parser.Analyze(ddlText, compatibilityLevel: 100);
            
            Assert.True(result.IsParsedSuccessfully);
        }

        [Fact]
        public void ExtractStatementChunks_SyntaxError_LogsWarningAndReturnsEmpty()
        {
            var ddlText = "CREATE PROCEDURE dbo.USP_Test AS SELECT FROM WHERE"; // Invalid SQL
            var parser = new SqlStaticParser();
            
            var result = parser.ExtractStatementChunks(ddlText);
            
            Assert.Empty(result);
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

        [Fact]
        public void Analyze_WithUnionAllAndDuplicateAliases_ShouldResolveColumnsIndependently()
        {
            // Arrange
            // UNION ALL의 서로 다른 절에서 동일한 별칭 E를 사용하여 서로 다른 테이블을 참조함
            var ddlText = @"
CREATE PROCEDURE dbo.TestUnionAllAliasCollision
AS
BEGIN
    SELECT E.Col1
    FROM dbo.TableA E
    UNION ALL
    SELECT E.Col2
    FROM dbo.TableB E;
END;
";
            var parser = new SqlStaticParser();

            // Act
            var result = parser.Analyze(ddlText);

            // Assert
            Assert.True(result.IsParsedSuccessfully);

            // TableA의 E.Col1은 TableA의 컬럼으로 정상 매핑되어야 함
            Assert.True(result.ReferencedColumnsPerTable.ContainsKey("dbo.TableA"));
            Assert.Contains("Col1", result.ReferencedColumnsPerTable["dbo.TableA"]);
            Assert.DoesNotContain("Col2", result.ReferencedColumnsPerTable["dbo.TableA"]);

            // TableB의 E.Col2는 TableB의 컬럼으로 정상 매핑되어야 함
            Assert.True(result.ReferencedColumnsPerTable.ContainsKey("dbo.TableB"));
            Assert.Contains("Col2", result.ReferencedColumnsPerTable["dbo.TableB"]);
            Assert.DoesNotContain("Col1", result.ReferencedColumnsPerTable["dbo.TableB"]);
        }

        [Fact]
        public void Analyze_WithQualifierlessColumnsInInsertSelect_ShouldResolveToSourceTable()
        {
            // Arrange
            var ddlText = @"
CREATE PROCEDURE dbo.TestQualifierlessColumns
AS
BEGIN
    INSERT INTO dbo.TargetTable (Col1, Col2)
    SELECT SourceCol1, SourceCol2
    FROM dbo.SourceTable WITH(NOLOCK);
END;
";
            var parser = new SqlStaticParser();

            // Act
            var result = parser.Analyze(ddlText);

            // Assert
            Assert.True(result.IsParsedSuccessfully);

            // SourceCol1, SourceCol2는 SourceTable의 참조 컬럼으로 수집되어야 함
            Assert.True(result.ReferencedColumnsPerTable.ContainsKey("dbo.SourceTable"));
            var sourceCols = result.ReferencedColumnsPerTable["dbo.SourceTable"];
            Assert.Contains("SourceCol1", sourceCols);
            Assert.Contains("SourceCol2", sourceCols);

            // TargetTable의 컬럼은 Col1, Col2로 수집되어야 함
            Assert.True(result.ReferencedColumnsPerTable.ContainsKey("dbo.TargetTable"));
            var targetCols = result.ReferencedColumnsPerTable["dbo.TargetTable"];
            Assert.Contains("Col1", targetCols);
            Assert.Contains("Col2", targetCols);
        }

        [Fact]
        public void Analyze_WithMultipleTablesAndSchemaMetadata_ShouldResolveToCorrectTable()
        {
            // Arrange
            var ddlText = @"
CREATE PROCEDURE dbo.TestMultipleTables
AS
BEGIN
    SELECT ColA, ColB
    FROM dbo.TableA A
    JOIN dbo.TableB B ON A.ID = B.ID;
END;
";
            // TableA는 ColA를 소유, TableB는 ColB를 소유하는 실제 DB 메타데이터 구성
            var schemaMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                { "dbo.TableA", new List<string> { "ID", "ColA" } },
                { "dbo.TableB", new List<string> { "ID", "ColB" } }
            };
            var parser = new SqlStaticParser();

            // Act
            var result = parser.Analyze(ddlText, 160, schemaMap);

            // Assert
            Assert.True(result.IsParsedSuccessfully);

            // ColA는 TableA의 컬럼으로 정상 해석
            Assert.True(result.ReferencedColumnsPerTable.ContainsKey("dbo.TableA"));
            Assert.Contains("ColA", result.ReferencedColumnsPerTable["dbo.TableA"]);
            Assert.DoesNotContain("ColB", result.ReferencedColumnsPerTable["dbo.TableA"]);

            // ColB는 TableB의 컬럼으로 정상 해석
            Assert.True(result.ReferencedColumnsPerTable.ContainsKey("dbo.TableB"));
            Assert.Contains("ColB", result.ReferencedColumnsPerTable["dbo.TableB"]);
            Assert.DoesNotContain("ColA", result.ReferencedColumnsPerTable["dbo.TableB"]);
        }

        [Fact]
        public void Analyze_WithParametersAndVariables_ShouldExtractSymbolsCorrectly()
        {
            // Arrange
            var ddlText = @"
CREATE PROCEDURE dbo.TestSymbols
    @Param1 INT,
    @Param2 VARCHAR(50) OUTPUT
AS
BEGIN
    DECLARE @Var1 DATETIME;
    DECLARE @Var2 DECIMAL(18,2) = 10.5;
    
    SET @Var1 = GETDATE();
END;
";
            var parser = new SqlStaticParser();

            // Act
            var result = parser.Analyze(ddlText);

            // Assert
            Assert.True(result.IsParsedSuccessfully);
            
            // Check Parameters
            Assert.Contains("@Param1 int", result.ProcedureParameters, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("@Param2 varchar(50)", result.ProcedureParameters, StringComparer.OrdinalIgnoreCase);

            // Check Variables
            Assert.Contains("@Var1 DATETIME", result.DeclaredVariables, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("@Var2 DECIMAL(18,2)", result.DeclaredVariables, StringComparer.OrdinalIgnoreCase);
        }

        [Fact]
        public void ExtractStatementChunks_ShouldReturnSeparateChunksForMultipleStatements()
        {
            // Arrange
            var ddlText = @"
CREATE PROCEDURE dbo.TestProc
AS
BEGIN
    UPDATE dbo.TableA SET Col1 = 1 WHERE ID = 1;

    IF 1 = 1
    BEGIN
        INSERT INTO dbo.TableB (ID) VALUES (2);
    END
    
    SELECT * FROM dbo.TableC;
END;
";
            var parser = new SqlStaticParser();

            // Act
            var chunks = parser.ExtractStatementChunks(ddlText);

            // Assert
            Assert.Equal(3, chunks.Count);
            
            // Chunk 1: UPDATE
            Assert.Contains("UPDATE dbo.TableA", chunks[0].StatementText);
            Assert.Contains("dbo.TableA", chunks[0].ReferencedTables);

            // Chunk 2: INSERT
            Assert.Contains("INSERT INTO dbo.TableB", chunks[1].StatementText);
            Assert.Contains("dbo.TableB", chunks[1].ReferencedTables);

            // Chunk 3: SELECT
            Assert.Contains("SELECT * FROM dbo.TableC", chunks[2].StatementText);
            Assert.Contains("dbo.TableC", chunks[2].ReferencedTables);
        }

        [Fact]
        public void Analyze_UpdateWithAliasTarget_ShouldRecordOnlyTheResolvedTarget()
        {
            // EXPECT_PROC 2-6절의 형태다. 예전에는 별칭 'A' 자체가 테이블로 등록되고
            // FROM 절 조인 원본까지 전부 UPDATE 대상이 됐다.
            var ddlText = @"
CREATE PROCEDURE dbo.TestUpdateTarget
AS
BEGIN
    UPDATE A
    SET    A.OutYMD = B.OutYMD
    FROM   SETTLE_POQ_DB.dbo.TSettleMst A
    JOIN   SETTLE_POQ_DB.dbo.TClientCMRate C ON A.ClientID = C.ClientID
    JOIN   SETTLE_POQ_DB.dbo.TSettleMst B ON A.MPLTID = B.PLTID;
END;
";
            var parser = new SqlStaticParser();

            var result = parser.Analyze(ddlText);

            Assert.True(result.IsParsedSuccessfully);
            Assert.Equal(new[] { "SETTLE_POQ_DB.dbo.TSettleMst" }, result.UpdateTables);
            Assert.DoesNotContain("A", result.ReferencedTables);
            Assert.Contains("SETTLE_POQ_DB.dbo.TClientCMRate", result.SelectTables);
        }

        [Fact]
        public void Analyze_UpdateWithFromSources_ShouldFileJoinSourcesAsReads()
        {
            // COMM_UPD의 지배적 형태. 대상은 TSettleMst 하나뿐이고 나머지는 읽기다.
            var ddlText = @"
CREATE PROCEDURE dbo.TestUpdateFrom
AS
BEGIN
    UPDATE TSettleMst
    SET    CLCOMM = B.CommissionAmt
    FROM   TSettleMst        A
          ,TClientSettleRate B
          ,TPGSettleRate     C
    WHERE  A.ClientID = B.ClientID
    AND    A.PGName   = C.PGName;
END;
";
            var parser = new SqlStaticParser();

            var result = parser.Analyze(ddlText);

            Assert.True(result.IsParsedSuccessfully);
            Assert.Equal(new[] { "TSettleMst" }, result.UpdateTables);
            Assert.Contains("TClientSettleRate", result.SelectTables);
            Assert.Contains("TPGSettleRate", result.SelectTables);
            Assert.DoesNotContain("TClientSettleRate", result.UpdateTables);
            Assert.DoesNotContain("TPGSettleRate", result.UpdateTables);
        }

        [Fact]
        public void Analyze_UpdateTargetAlsoInFromClause_ShouldAppearAsBothTargetAndRead()
        {
            // 대상이 FROM 절에도 나타나면 실제로 읽고 쓴다. 양쪽에 기록하는 게 사실이다.
            var ddlText = @"
CREATE PROCEDURE dbo.TestUpdateSelfRead
AS
BEGIN
    UPDATE TSettleMst
    SET    CLTotal = A.CLComm + A.CLVT
    FROM   TSettleMst A
    WHERE  A.YMD = '20260808';
END;
";
            var parser = new SqlStaticParser();

            var result = parser.Analyze(ddlText);

            Assert.True(result.IsParsedSuccessfully);
            Assert.Equal(new[] { "TSettleMst" }, result.UpdateTables);
            Assert.Contains("TSettleMst", result.SelectTables);
        }

        [Fact]
        public void Analyze_UpdateWithFromSources_ShouldStillCreateMapping()
        {
            // 라운드 2 회귀 수정 - Analyze_UpdateWithFromSources_ShouldFileJoinSourcesAsReads(:532)와
            // 같은 DDL(COMM_UPD의 지배적 형태). 대상 TSettleMst는 한정되지 않은 이름이지만
            // FROM 절에 "TSettleMst"라는 별칭 선언 자체가 없다(별칭은 A/B/C뿐). 별칭 미해결과
            // 혼동해 매핑을 안 만들면 이 SP의 16개 컬럼 * -1 표가 프롬프트에서 사라진다.
            var ddlText = @"
CREATE PROCEDURE dbo.TestUpdateFromMapping
AS
BEGIN
    UPDATE TSettleMst
    SET    CLCOMM = B.CommissionAmt
    FROM   TSettleMst        A
          ,TClientSettleRate B
          ,TPGSettleRate     C
    WHERE  A.ClientID = B.ClientID
    AND    A.PGName   = C.PGName;
END;
";
            var parser = new SqlStaticParser();

            var result = parser.Analyze(ddlText);

            Assert.True(result.IsParsedSuccessfully);
            var mapping = Assert.Single(result.AstUpdateMappings);
            Assert.Equal("TSettleMst", mapping.TargetTable);
        }

        [Fact]
        public void Analyze_UpdateTargetAlsoInFromClause_ShouldStillCreateMapping()
        {
            // 라운드 2 회귀 수정 - Analyze_UpdateTargetAlsoInFromClause_ShouldAppearAsBothTargetAndRead(:561)와
            // 같은 DDL. FROM 절의 별칭은 "A"이지 "TSettleMst"가 아니므로 대상은 별칭이 아니라
            // 평범한 물리 테이블명이다.
            var ddlText = @"
CREATE PROCEDURE dbo.TestUpdateSelfReadMapping
AS
BEGIN
    UPDATE TSettleMst
    SET    CLTotal = A.CLComm + A.CLVT
    FROM   TSettleMst A
    WHERE  A.YMD = '20260808';
END;
";
            var parser = new SqlStaticParser();

            var result = parser.Analyze(ddlText);

            Assert.True(result.IsParsedSuccessfully);
            var mapping = Assert.Single(result.AstUpdateMappings);
            Assert.Equal("TSettleMst", mapping.TargetTable);
        }

        [Fact]
        public void Analyze_DeleteWithAliasTarget_ShouldRecordOnlyTheResolvedTarget()
        {
            // 4PLCARD의 형태. DeleteTables가 ['A','TSettleMst','TPGProperty']였다.
            var ddlText = @"
CREATE PROCEDURE dbo.TestDeleteTarget
AS
BEGIN
    DELETE A
    FROM   TSettleMst A
    INNER JOIN TPGProperty AS PG ON A.PGName = PG.PGName
    WHERE  A.TxAmt = 0;
END;
";
            var parser = new SqlStaticParser();

            var result = parser.Analyze(ddlText);

            Assert.True(result.IsParsedSuccessfully);
            Assert.Equal(new[] { "TSettleMst" }, result.DeleteTables);
            Assert.DoesNotContain("A", result.ReferencedTables);
            Assert.Contains("TPGProperty", result.SelectTables);
        }

        [Fact]
        public void Analyze_DeleteWithQualifiedFromSource_ShouldNotDoubleCountTheTarget()
        {
            // AcqManual의 형태. 한정 없는 대상과 3파트 FROM 원본이 같은 테이블이다.
            // 표기 통일은 정규화기 몫이고, 여기서는 대상이 하나만 잡히면 된다.
            var ddlText = @"
CREATE PROCEDURE dbo.TestDeleteQualified
AS
BEGIN
    DELETE TSettleByOUT
    FROM   SETTLE_POQ_DB.dbo.TSettleByOUT
    WHERE  OutYMD = '20260808';
END;
";
            var parser = new SqlStaticParser();

            var result = parser.Analyze(ddlText);

            Assert.True(result.IsParsedSuccessfully);
            Assert.Equal(new[] { "TSettleByOUT" }, result.DeleteTables);
        }

        [Fact]
        public void Analyze_PlainUpdateWithoutFromClause_ShouldStillRecordTheTarget()
        {
            var ddlText = @"
CREATE PROCEDURE dbo.TestPlainUpdate
AS
BEGIN
    UPDATE dbo.TSettleMst
    SET    PGComm = 0
    WHERE  YMD = '20260808';
END;
";
            var parser = new SqlStaticParser();

            var result = parser.Analyze(ddlText);

            Assert.True(result.IsParsedSuccessfully);
            Assert.Equal(new[] { "dbo.TSettleMst" }, result.UpdateTables);
            Assert.Contains("dbo.TSettleMst", result.ReferencedTables);
        }

        [Fact]
        public void Analyze_UpdateTargetingTableVariable_ShouldFallBackToOldBehaviour()
        {
            // 대상을 해석할 수 없으면 그 문장에 한해 예전처럼 문맥 내 전체를 수집한다.
            // 대상을 통째로 잃는 것보다 과다 보고가 낫다.
            var ddlText = @"
CREATE PROCEDURE dbo.TestUpdateTableVariable
AS
BEGIN
    DECLARE @Buffer TABLE (Id INT, Amt INT);

    UPDATE @Buffer
    SET    Amt = S.TxAmt
    FROM   @Buffer B
    JOIN   TSettleMst S ON B.Id = S.ID;
END;
";
            var parser = new SqlStaticParser();

            var result = parser.Analyze(ddlText);

            Assert.True(result.IsParsedSuccessfully);
            Assert.Contains("TSettleMst", result.UpdateTables);
        }

        [Fact]
        public void Analyze_UpdateWithLinkedServerTarget_ShouldRecordTargetAndLinkedServerWarning()
        {
            // 대상 노드를 건너뛰는 가드가 앞쪽에 있으면 링크드 서버 감지 블록까지
            // 함께 건너뛴다. 대상이어도 링크드 서버 신호는 죽으면 안 된다.
            var ddlText = @"
CREATE PROCEDURE dbo.TestUpdateLinkedServerTarget
AS
BEGIN
    UPDATE MyServer.RemoteDb.dbo.Orders
    SET    OrderStatus = 1
    WHERE  OrderID = 1001;
END;
";
            var parser = new SqlStaticParser();

            var result = parser.Analyze(ddlText);

            Assert.True(result.IsParsedSuccessfully);
            Assert.Equal(new[] { "MyServer.RemoteDb.dbo.Orders" }, result.UpdateTables);
            Assert.Contains("MyServer.RemoteDb.dbo.Orders", result.LinkedServerReferences);
            Assert.Contains(result.ControlFlowSummary, s => s.Contains("Linked Server 원격 테이블 참조 감지됨") && s.Contains("MyServer.RemoteDb.dbo.Orders"));
        }

        [Fact]
        public void Analyze_DeleteWithLinkedServerTarget_ShouldRecordTargetAndLinkedServerWarning()
        {
            var ddlText = @"
CREATE PROCEDURE dbo.TestDeleteLinkedServerTarget
AS
BEGIN
    DELETE MyServer.RemoteDb.dbo.Orders
    WHERE  OrderID = 1001;
END;
";
            var parser = new SqlStaticParser();

            var result = parser.Analyze(ddlText);

            Assert.True(result.IsParsedSuccessfully);
            Assert.Equal(new[] { "MyServer.RemoteDb.dbo.Orders" }, result.DeleteTables);
            Assert.Contains("MyServer.RemoteDb.dbo.Orders", result.LinkedServerReferences);
            Assert.Contains(result.ControlFlowSummary, s => s.Contains("Linked Server 원격 테이블 참조 감지됨") && s.Contains("MyServer.RemoteDb.dbo.Orders"));
        }

        private static SpStaticAnalysisResult AnalyzeUpdate(string body)
        {
            var parser = new SqlStaticParser();
            return parser.Analyze($@"
CREATE PROCEDURE dbo.UpdateProbe
AS
BEGIN
{body}
END");
        }

        [Fact]
        public void Analyze_WithSimpleSetClause_ShouldExtractColumnsAndExpressions()
        {
            // Arrange & Act
            var result = AnalyzeUpdate("    UPDATE dbo.TCommMst SET CLVT = 100, PGVT = @amount;");

            // Assert
            var mapping = Assert.Single(result.AstUpdateMappings);
            Assert.Equal("dbo.TCommMst", mapping.TargetTable);
            Assert.Equal(1, mapping.StatementOrdinal);
            Assert.Collection(mapping.Assignments,
                a => { Assert.Equal("CLVT", a.Column); Assert.Equal("100", a.SourceExpression); },
                a => { Assert.Equal("PGVT", a.Column); Assert.Equal("@amount", a.SourceExpression); });
        }

        [Fact]
        public void Analyze_WithQualifiedSetTarget_ShouldStripTableQualifier()
        {
            // Arrange & Act
            var result = AnalyzeUpdate("    UPDATE T SET T.COMM = 0 FROM dbo.TCommMst T;");

            // Assert
            var mapping = Assert.Single(result.AstUpdateMappings);
            Assert.Equal("COMM", Assert.Single(mapping.Assignments).Column);
        }

        [Fact]
        public void Analyze_WithVariableAssignment_ShouldRecordOnlyColumnAssignments()
        {
            // Arrange & Act
            var result = AnalyzeUpdate(
                "    DECLARE @total INT;\r\n" +
                "    UPDATE dbo.TCommMst SET @total = CLVT, CLVT = 0;");

            // Assert
            var mapping = Assert.Single(result.AstUpdateMappings);
            Assert.Equal("CLVT", Assert.Single(mapping.Assignments).Column);
        }

        [Fact]
        public void Analyze_WithFromClause_ShouldCaptureFromTextAndResolveAlias()
        {
            // Arrange & Act
            var result = AnalyzeUpdate(
                "    UPDATE A SET A.CLVT = B.CLVT FROM dbo.TCommMst A INNER JOIN dbo.TStage B ON A.SEQ = B.SEQ;");

            // Assert
            var mapping = Assert.Single(result.AstUpdateMappings);
            Assert.Equal("dbo.TCommMst", mapping.TargetTable);
            Assert.NotNull(mapping.FromClauseText);
            Assert.Contains("dbo.TCommMst", mapping.FromClauseText!);
            Assert.Contains("dbo.TStage", mapping.FromClauseText!);
        }

        [Fact]
        public void Analyze_WithoutFromClause_ShouldLeaveFromTextNull()
        {
            // Arrange & Act
            var result = AnalyzeUpdate("    UPDATE dbo.TCommMst SET CLVT = 0 WHERE SEQ = 1;");

            // Assert
            Assert.Null(Assert.Single(result.AstUpdateMappings).FromClauseText);
        }

        [Fact]
        public void Analyze_UnqualifiedTargetWithoutFromClause_ShouldStillCreateMapping()
        {
            // 라운드 2 회귀 수정 - 대상이 한정되지 않았고(TCommMst) FROM 절 자체가 없으면
            // 별칭일 여지가 없다. 별칭 미해결과 혼동해 매핑을 안 만들면 안 된다.
            var result = AnalyzeUpdate("    UPDATE TCommMst SET CLVT = 0 WHERE SEQ = 1;");

            var mapping = Assert.Single(result.AstUpdateMappings);
            Assert.Equal("TCommMst", mapping.TargetTable);
        }

        [Fact]
        public void Analyze_WithSelfReferencingSet_ShouldReportSelfReferencedColumns()
        {
            // Arrange & Act
            var result = AnalyzeUpdate("    UPDATE dbo.TCommMst SET CLVT = CLVT * -1, PGVT = PGVT * -1;");

            // Assert
            var mapping = Assert.Single(result.AstUpdateMappings);
            Assert.Equal(new[] { "CLVT", "PGVT" }, mapping.SelfReferencedColumns);
        }

        [Fact]
        public void Analyze_WhenRightHandSideIsNotATarget_ShouldNotReportSelfReference()
        {
            // Arrange & Act
            var result = AnalyzeUpdate("    UPDATE dbo.TCommMst SET CLVT = PGVT * -1;");

            // Assert
            Assert.Empty(Assert.Single(result.AstUpdateMappings).SelfReferencedColumns);
        }

        [Fact]
        public void Analyze_WithTwoUpdatesOnSameTable_ShouldNumberStatements()
        {
            // Arrange & Act
            var result = AnalyzeUpdate(
                "    UPDATE dbo.TCommMst SET CLVT = 0;\r\n" +
                "    UPDATE dbo.TCommMst SET PGVT = 1;");

            // Assert
            Assert.Equal(2, result.AstUpdateMappings.Count);
            Assert.Equal(1, result.AstUpdateMappings[0].StatementOrdinal);
            Assert.Equal(2, result.AstUpdateMappings[1].StatementOrdinal);
        }

        [Fact]
        public void Analyze_WhenTargetIsUnresolvable_ShouldNotCreateMapping()
        {
            // Arrange & Act - 테이블 변수는 NamedTableReference가 아니므로 대상이 풀리지 않는다.
            var result = AnalyzeUpdate(
                "    DECLARE @T TABLE (CLVT INT);\r\n" +
                "    UPDATE @T SET CLVT = 0;");

            // Assert - 매핑이 비어 있는 것은 가드가 "건너뛰기"로 정상 처리했을 때와, 예외가
            // Analyze의 soft-fail 봉투에 삼켜졌을 때 둘 다에서 관찰된다. IsParsedSuccessfully가
            // true라는 것까지 같이 단언해야 후자(크래시)를 전자(정상 가드)로 오인하지 않는다.
            //
            // 여기에 더해, RecordUpdateMapping 내부의 방어 가드(빈 targetTable을 조용히
            // 건너뛰는 두 번째 방어선)가 존재하는 한, ExplicitVisit(UpdateSpecification)의
            // 호출부 가드만 제거해도 내부 방어 가드가 똑같이 "매핑 없음 + 파싱 성공"을
            // 만들어 내므로 위 두 단언만으로는 호출부 가드가 살아 있는지 구분할 수 없다.
            // 내부 방어 가드가 남기는 ControlFlowSummary 흔적이 없다는 것까지 확인해야
            // "정상적으로 가드가 통과를 막았다"와 "호출부 가드는 뚫렸지만 내부 방어 가드가
            // 대신 막았다"를 구분할 수 있다.
            Assert.True(result.IsParsedSuccessfully);
            Assert.Empty(result.AstUpdateMappings);
            Assert.DoesNotContain(result.ControlFlowSummary, s => s.Contains("내부 방어 가드 작동"));
        }

        [Fact]
        public void Analyze_UpdateTargetIsQueryDerivedTableAlias_ShouldNotCreateMapping()
        {
            // Arrange & Act - 대상 별칭 X가 파생 테이블(서브쿼리)을 가리켜서
            // AliasTargetFinder가 방문하는 NamedTableReference로는 못 찾는다.
            var result = AnalyzeUpdate(
                "    UPDATE X SET RN = 1 " +
                "FROM (SELECT ROW_NUMBER() OVER (ORDER BY SEQ) AS RN, SEQ FROM dbo.TSample) X;");

            // Assert - 별칭을 풀지 못했으므로 매핑은 만들지 않되, UpdateTables 과다 보고는
            // 이 브랜치 이전부터의 관용적 동작이므로 그대로 유지되어야 한다.
            Assert.True(result.IsParsedSuccessfully);
            Assert.Empty(result.AstUpdateMappings);
            Assert.Contains("X", result.UpdateTables);
        }

        [Fact]
        public void Analyze_UpdateTargetIsTableVariableAlias_ShouldNotCreateMapping()
        {
            // Arrange & Act - 대상 별칭 T가 테이블 변수를 가리켜서
            // AliasTargetFinder가 방문하는 NamedTableReference로는 못 찾는다.
            var result = AnalyzeUpdate("    UPDATE T SET AMT = 0 FROM @Buf T;");

            // Assert
            Assert.True(result.IsParsedSuccessfully);
            Assert.Empty(result.AstUpdateMappings);
            Assert.Contains("T", result.UpdateTables);
        }

        [Fact]
        public void Analyze_UpdateTargetIsCteAliasWithoutFromClause_ShouldNotCreateMapping()
        {
            // Arrange & Act - WITH C AS (...) UPDATE C SET ... 형태는 UPDATE 문 자체에
            // FROM 절이 없어(node.FromClause가 null) 별칭 C를 풀 방법이 없다.
            var result = AnalyzeUpdate(
                "    WITH C AS (SELECT SEQ, AMT FROM dbo.TSample) " +
                "UPDATE C SET AMT = 0;");

            // Assert
            Assert.True(result.IsParsedSuccessfully);
            Assert.Empty(result.AstUpdateMappings);
            Assert.Contains("C", result.UpdateTables);
        }

        [Fact]
        public void Analyze_UpdateTargetIsOpenQueryAlias_ShouldNotCreateMapping()
        {
            // Arrange & Act - OPENQUERY는 TableReferenceWithAlias의 자식이지만
            // NamedTableReference / QueryDerivedTable / VariableTableReference 어디에도
            // 속하지 않는다. AliasTargetFinder가 "별칭 선언 자체가 없다"고 오판하면
            // 별칭 O를 평범한 물리 테이블명으로 취급해 존재하지 않는 테이블 O를
            // L1 요구사항으로 승격시킨다. 이 단언이 그 분기를 고정한다.
            var result = AnalyzeUpdate(
                "    UPDATE O SET AMT = 0 " +
                "FROM OPENQUERY(LNK, 'SELECT SEQ, AMT FROM T') O;");

            // Assert
            Assert.True(result.IsParsedSuccessfully);
            Assert.Empty(result.AstUpdateMappings);
        }

        [Fact]
        public void Analyze_UpdateTargetIsTempTable_ShouldNotCreateMapping()
        {
            // Arrange & Act - 임시 테이블은 UpdateTables에도 들어가지 않고 명세서 CRUD
            // 분석 표에도 물리 테이블로 기술되지 않으므로 SET 매핑을 만들면 안 된다.
            var result = AnalyzeUpdate("    UPDATE #TMP SET AMT = 0;");

            // Assert
            Assert.True(result.IsParsedSuccessfully);
            Assert.Empty(result.AstUpdateMappings);
            Assert.Contains("#TMP", result.CreatedTempTables);
            Assert.DoesNotContain("#TMP", result.UpdateTables);
        }

        [Fact]
        public void Analyze_WithWriteMutatorSetClause_ShouldExtractColumnFromCallTarget()
        {
            // Arrange & Act - .WRITE()는 AssignmentSetClause가 아니라 FunctionCallSetClause로
            // 파싱된다. 컬럼은 뮤테이터 호출 대상에서, 표현식은 절 원문에서 가져온다.
            var result = AnalyzeUpdate(
                "    UPDATE dbo.TDocument SET Content.WRITE(@chunk, 1, 4) WHERE ID = 1;");

            // Assert
            var mapping = Assert.Single(result.AstUpdateMappings);
            var assignment = Assert.Single(mapping.Assignments);
            Assert.Equal("Content", assignment.Column);
            Assert.Contains(".WRITE(", assignment.SourceExpression);
        }
    }
}
