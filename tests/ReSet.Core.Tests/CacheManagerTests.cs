using System;
using System.Collections.Generic;
using System.IO;
using Xunit;
using ReSet.Core.Models;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class CacheManagerTests : IDisposable
    {
        private readonly string _tempOutputDir;
        private readonly CacheManager _cacheManager;

        public CacheManagerTests()
        {
            // 각 테스트 실행 시 임시 디렉토리 생성
            _tempOutputDir = Path.Combine(Path.GetTempPath(), "ReSetTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempOutputDir);
            _cacheManager = new CacheManager();
        }

        public void Dispose()
        {
            // 테스트 종료 후 임시 디렉토리 및 파일 정리
            if (Directory.Exists(_tempOutputDir))
            {
                try
                {
                    Directory.Delete(_tempOutputDir, true);
                }
                catch
                {
                    // 무시
                }
            }
        }

        [Fact]
        public void ComputeCompositeHash_IdenticalDefinitions_ReturnsSameHash()
        {
            // Arrange
            var sp1 = new SpDefinition
            {
                Schema = "dbo",
                Name = "TestSp",
                DdlText = "CREATE PROCEDURE dbo.TestSp AS SELECT 1;",
                Dependencies = new List<DependencyInfo>
                {
                    new DependencyInfo { Schema = "dbo", Name = "TableA", ReferencedDdlText = "CREATE TABLE TableA (Id INT);" },
                    new DependencyInfo { Schema = "dbo", Name = "TableB", ReferencedDdlText = "CREATE TABLE TableB (Id INT);" }
                }
            };

            var sp2 = new SpDefinition
            {
                Schema = "dbo",
                Name = "TestSp",
                DdlText = "CREATE PROCEDURE dbo.TestSp AS SELECT 1;",
                // 의존성 등록 순서가 다름
                Dependencies = new List<DependencyInfo>
                {
                    new DependencyInfo { Schema = "dbo", Name = "TableB", ReferencedDdlText = "CREATE TABLE TableB (Id INT);" },
                    new DependencyInfo { Schema = "dbo", Name = "TableA", ReferencedDdlText = "CREATE TABLE TableA (Id INT);" }
                }
            };

            // Act
            var hash1 = _cacheManager.ComputeCompositeHash(sp1);
            var hash2 = _cacheManager.ComputeCompositeHash(sp2);

            // Assert
            Assert.False(string.IsNullOrEmpty(hash1));
            Assert.Equal(hash1, hash2);
        }

        [Fact]
        public void ComputeCompositeHash_DifferentDefinitions_ReturnsDifferentHash()
        {
            // Arrange
            var sp1 = new SpDefinition
            {
                Schema = "dbo",
                Name = "TestSp",
                DdlText = "CREATE PROCEDURE dbo.TestSp AS SELECT 1;"
            };

            var sp2 = new SpDefinition
            {
                Schema = "dbo",
                Name = "TestSp",
                DdlText = "CREATE PROCEDURE dbo.TestSp AS SELECT 2;" // DDL이 다름
            };

            // Act
            var hash1 = _cacheManager.ComputeCompositeHash(sp1);
            var hash2 = _cacheManager.ComputeCompositeHash(sp2);

            // Assert
            Assert.NotEqual(hash1, hash2);
        }

        [Fact]
        public void IsCacheValid_ReturnsFalse_WhenCacheIndexOrSpecMissing()
        {
            // Act & Assert
            // 1. 인덱스도 파일도 없는 상태
            var isValid = _cacheManager.IsCacheValid("dbo.TestSp", "somehash", _tempOutputDir);
            Assert.False(isValid);

            // 2. 인덱스는 존재하지만, Spec.md 파일이 존재하지 않는 상태
            _cacheManager.UpdateCache("dbo.TestSp", new SpDefinition { DdlText = "CREATE PROC" }, "somehash", _tempOutputDir);
            isValid = _cacheManager.IsCacheValid("dbo.TestSp", "somehash", _tempOutputDir);
            Assert.False(isValid); // Spec.md가 없어 false
        }

        [Fact]
        public void UpdateCache_And_IsCacheValid_ReturnsTrue_WhenBothExistAndMatch()
        {
            // Arrange
            var spName = "dbo.TestSp";
            var hash = "expectedcompositehash12345";
            var specContent = "# Spec Report for TestSp";

            // Spec 파일 생성
            var specFilePath = Path.Combine(_tempOutputDir, "Procedures", spName, "docs", "Spec.md");
            Directory.CreateDirectory(Path.GetDirectoryName(specFilePath)!);
            File.WriteAllText(specFilePath, specContent);

            // Act
            _cacheManager.UpdateCache(spName, new SpDefinition { DdlText = "CREATE PROC dbo.TestSp AS SELECT 1;" }, hash, _tempOutputDir);
            var isValid = _cacheManager.IsCacheValid(spName, hash, _tempOutputDir);

            // Assert
            Assert.True(isValid);
        }

        [Fact]
        public void IsCacheValid_ReturnsFalse_WhenHashMismatches()
        {
            // Arrange
            var spName = "dbo.TestSp";
            var specContent = "# Spec Report";
            var specFilePath = Path.Combine(_tempOutputDir, "Procedures", spName, "docs", "Spec.md");
            Directory.CreateDirectory(Path.GetDirectoryName(specFilePath)!);
            File.WriteAllText(specFilePath, specContent);

            // Act
            _cacheManager.UpdateCache(spName, new SpDefinition { DdlText = "CREATE PROC" }, "hash_a", _tempOutputDir);
            var isValid = _cacheManager.IsCacheValid(spName, "hash_b", _tempOutputDir); // 다른 해시로 조회

            // Assert
            Assert.False(isValid);
        }
    }
}
