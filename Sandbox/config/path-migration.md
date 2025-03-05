# SQLFlow Path Migration Script

## Purpose
This script updates path references in SQLFlow data tables from one file system location to another (e.g., from `/b/SQLFlow` to `/c/SQLFlow`).

## Usage Instructions
1. Set the `@UpdatePaths` flag to `0` to first run in report-only mode
2. Review the results to confirm affected records
3. Set the `@UpdatePaths` flag to `1` to perform the actual updates
4. **IMPORTANT**: Run lineage calculation after path changes are complete
5. **IMPORTANT**: Custom integration scripts in the flw.Invoke must be updated manually
5. Use SSMS or tool of your choice to execute the script

## Parameters
| Parameter | Description |
|-----------|-------------|
| `@UpdatePaths` | Set to `0` for report mode, `1` to perform updates |
| `@SrcPathElement` | The source path element to be replaced (e.g., '/b/SQLFlow') |
| `@TrgPathElement` | The target path element to replace with (e.g., '/c/SQLFlow') |

## Affected Tables
- `flw.SysLogExport`
- `flw.SysSourceControl`
- `flw.Export`
- `flw.Invoke`
- `flw.LineageMap`
- `flw.PreIngestionCSV`
- `flw.PreIngestionPRC`
- `flw.PreIngestionPRQ`
- `flw.PreIngestionXLS`
- `flw.PreIngestionJSN`
- `flw.PreIngestionXML`

## Execution Flow
1. Counts occurrences of `@SrcPathElement` in all relevant tables
2. If `@UpdatePaths = 1`, performs updates with transaction safety
3. Counts occurrences of `@TrgPathElement` to verify success

## T-SQL Script
```sql

DECLARE @UpdatePaths BIT = 1;
DECLARE @SrcPathElement NVARCHAR(255) = N'/b/SQLFlow';
DECLARE @TrgPathElement NVARCHAR(255) = N'/c/SQLFlow';


SELECT *
FROM
(
    SELECT 'flw.SysLogExport' AS TableName,
           COUNT(*) AS ReplacementsCount
    FROM flw.SysLogExport
    WHERE FilePath_DW LIKE '%' + @SrcPathElement + '%'
    UNION ALL
    SELECT 'flw.SysSourceControl' AS TableName,
           COUNT(*) AS ReplacementsCount
    FROM flw.SysSourceControl
    WHERE ScriptToPath LIKE '%' + @SrcPathElement + '%'
    UNION ALL
    SELECT 'flw.Export' AS TableName,
           COUNT(*) AS ReplacementsCount
    FROM flw.Export
    WHERE trgPath LIKE '%' + @SrcPathElement + '%'
    UNION ALL
    SELECT 'flw.Invoke' AS TableName,
           COUNT(*) AS ReplacementsCount
    FROM flw.Invoke
    WHERE InvokePath LIKE '%' + @SrcPathElement + '%'
    UNION ALL
    SELECT 'flw.LineageMap (PathStr)' AS TableName,
           COUNT(*) AS ReplacementsCount
    FROM flw.LineageMap
    WHERE PathStr LIKE '%' + @SrcPathElement + '%'
    UNION ALL
    SELECT 'flw.LineageMap (PathNum)' AS TableName,
           COUNT(*) AS ReplacementsCount
    FROM flw.LineageMap
    WHERE PathNum LIKE '%' + @SrcPathElement + '%'
    UNION ALL
    SELECT 'flw.PreIngestionCSV (srcPath)' AS TableName,
           COUNT(*) AS ReplacementsCount
    FROM flw.PreIngestionCSV
    WHERE srcPath LIKE '%' + @SrcPathElement + '%'
    UNION ALL
    SELECT 'flw.PreIngestionCSV (srcPathMask)' AS TableName,
           COUNT(*) AS ReplacementsCount
    FROM flw.PreIngestionCSV
    WHERE srcPathMask LIKE '%' + @SrcPathElement + '%'
    UNION ALL
    SELECT 'flw.PreIngestionCSV (copyToPath)' AS TableName,
           COUNT(*) AS ReplacementsCount
    FROM flw.PreIngestionCSV
    WHERE copyToPath LIKE '%' + @SrcPathElement + '%'
    UNION ALL
    SELECT 'flw.PreIngestionCSV (zipToPath)' AS TableName,
           COUNT(*) AS ReplacementsCount
    FROM flw.PreIngestionCSV
    WHERE zipToPath LIKE '%' + @SrcPathElement + '%'
    UNION ALL
    SELECT 'flw.PreIngestionPRC' AS TableName,
           COUNT(*) AS ReplacementsCount
    FROM flw.PreIngestionPRC
    WHERE srcPath LIKE '%' + @SrcPathElement + '%'
    UNION ALL
    SELECT 'flw.PreIngestionPRQ (srcPath)' AS TableName,
           COUNT(*) AS ReplacementsCount
    FROM flw.PreIngestionPRQ
    WHERE srcPath LIKE '%' + @SrcPathElement + '%'
    UNION ALL
    SELECT 'flw.PreIngestionPRQ (srcPathMask)' AS TableName,
           COUNT(*) AS ReplacementsCount
    FROM flw.PreIngestionPRQ
    WHERE srcPathMask LIKE '%' + @SrcPathElement + '%'
    UNION ALL
    SELECT 'flw.PreIngestionPRQ (copyToPath)' AS TableName,
           COUNT(*) AS ReplacementsCount
    FROM flw.PreIngestionPRQ
    WHERE copyToPath LIKE '%' + @SrcPathElement + '%'
    UNION ALL
    SELECT 'flw.PreIngestionPRQ (zipToPath)' AS TableName,
           COUNT(*) AS ReplacementsCount
    FROM flw.PreIngestionPRQ
    WHERE zipToPath LIKE '%' + @SrcPathElement + '%'
    UNION ALL
    SELECT 'flw.PreIngestionXLS (srcPath)' AS TableName,
           COUNT(*) AS ReplacementsCount
    FROM flw.PreIngestionXLS
    WHERE srcPath LIKE '%' + @SrcPathElement + '%'
    UNION ALL
    SELECT 'flw.PreIngestionXLS (srcPathMask)' AS TableName,
           COUNT(*) AS ReplacementsCount
    FROM flw.PreIngestionXLS
    WHERE srcPathMask LIKE '%' + @SrcPathElement + '%'
    UNION ALL
    SELECT 'flw.PreIngestionXLS (copyToPath)' AS TableName,
           COUNT(*) AS ReplacementsCount
    FROM flw.PreIngestionXLS
    WHERE copyToPath LIKE '%' + @SrcPathElement + '%'
    UNION ALL
    SELECT 'flw.PreIngestionXLS (zipToPath)' AS TableName,
           COUNT(*) AS ReplacementsCount
    FROM flw.PreIngestionXLS
    WHERE zipToPath LIKE '%' + @SrcPathElement + '%'
    UNION ALL
    SELECT 'flw.PreIngestionJSN (srcPath)' AS TableName,
           COUNT(*) AS ReplacementsCount
    FROM flw.PreIngestionJSN
    WHERE srcPath LIKE '%' + @SrcPathElement + '%'
    UNION ALL
    SELECT 'flw.PreIngestionJSN (srcPathMask)' AS TableName,
           COUNT(*) AS ReplacementsCount
    FROM flw.PreIngestionJSN
    WHERE srcPathMask LIKE '%' + @SrcPathElement + '%'
    UNION ALL
    SELECT 'flw.PreIngestionJSN (copyToPath)' AS TableName,
           COUNT(*) AS ReplacementsCount
    FROM flw.PreIngestionJSN
    WHERE copyToPath LIKE '%' + @SrcPathElement + '%'
    UNION ALL
    SELECT 'flw.PreIngestionJSN (zipToPath)' AS TableName,
           COUNT(*) AS ReplacementsCount
    FROM flw.PreIngestionJSN
    WHERE zipToPath LIKE '%' + @SrcPathElement + '%'
    UNION ALL
    SELECT 'flw.PreIngestionXML (srcPath)' AS TableName,
           COUNT(*) AS ReplacementsCount
    FROM flw.PreIngestionXML
    WHERE srcPath LIKE '%' + @SrcPathElement + '%'
    UNION ALL
    SELECT 'flw.PreIngestionXML (srcPathMask)' AS TableName,
           COUNT(*) AS ReplacementsCount
    FROM flw.PreIngestionXML
    WHERE srcPathMask LIKE '%' + @SrcPathElement + '%'
    UNION ALL
    SELECT 'flw.PreIngestionXML (copyToPath)' AS TableName,
           COUNT(*) AS ReplacementsCount
    FROM flw.PreIngestionXML
    WHERE copyToPath LIKE '%' + @SrcPathElement + '%'
    UNION ALL
    SELECT 'flw.PreIngestionXML (zipToPath)' AS TableName,
           COUNT(*) AS ReplacementsCount
    FROM flw.PreIngestionXML
    WHERE zipToPath LIKE '%' + @SrcPathElement + '%'
) AS Replacements;


IF (@UpdatePaths = 1)
BEGIN
    -- UPDATE flw.with error handling for SysLogExport.FilePath_DW
    BEGIN TRY
        BEGIN TRANSACTION;
        UPDATE flw.SysLogExport
        SET FilePath_DW = REPLACE(FilePath_DW, @SrcPathElement, @TrgPathElement)
        WHERE FilePath_DW LIKE '%' + @SrcPathElement + '%';
        PRINT '  ' + CAST(@@ROWCOUNT AS VARCHAR(10)) + ' rows updated in SysLogExport.FilePath_DW';
        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
            ROLLBACK TRANSACTION;
        PRINT 'Error updating SysLogExport.FilePath_DW: ' + ERROR_MESSAGE();
    END CATCH;

    -- UPDATE flw.with error handling for SysSourceControl.ScriptToPath
    BEGIN TRY
        BEGIN TRANSACTION;
        UPDATE flw.SysSourceControl
        SET ScriptToPath = REPLACE(ScriptToPath, @SrcPathElement, @TrgPathElement)
        WHERE ScriptToPath LIKE '%' + @SrcPathElement + '%';
        PRINT '  ' + CAST(@@ROWCOUNT AS VARCHAR(10)) + ' rows updated in SysSourceControl.ScriptToPath';
        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
            ROLLBACK TRANSACTION;
        PRINT 'Error updating SysSourceControl.ScriptToPath: ' + ERROR_MESSAGE();
    END CATCH;



    -- UPDATE flw.with error handling for Export.trgPath
    BEGIN TRY
        BEGIN TRANSACTION;
        UPDATE flw.Export
        SET trgPath = REPLACE(trgPath, @SrcPathElement, @TrgPathElement)
        WHERE trgPath LIKE '%' + @SrcPathElement + '%';
        PRINT '  ' + CAST(@@ROWCOUNT AS VARCHAR(10)) + ' rows updated in Export.trgPath';
        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
            ROLLBACK TRANSACTION;
        PRINT 'Error updating Export.trgPath: ' + ERROR_MESSAGE();
    END CATCH;


    -- UPDATE flw.with error handling for Invoke.InvokePath
    BEGIN TRY
        BEGIN TRANSACTION;
        UPDATE flw.Invoke
        SET InvokePath = REPLACE(InvokePath, @SrcPathElement, @TrgPathElement)
        WHERE InvokePath LIKE '%' + @SrcPathElement + '%';
        PRINT '  ' + CAST(@@ROWCOUNT AS VARCHAR(10)) + ' rows updated in Invoke.InvokePath';
        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
            ROLLBACK TRANSACTION;
        PRINT 'Error updating Invoke.InvokePath: ' + ERROR_MESSAGE();
    END CATCH;



    -- UPDATE flw.with error handling for LineageMap.PathStr
    BEGIN TRY
        BEGIN TRANSACTION;
        UPDATE flw.LineageMap
        SET PathStr = REPLACE(PathStr, @SrcPathElement, @TrgPathElement)
        WHERE PathStr LIKE '%' + @SrcPathElement + '%';
        PRINT '  ' + CAST(@@ROWCOUNT AS VARCHAR(10)) + ' rows updated in LineageMap.PathStr';
        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
            ROLLBACK TRANSACTION;
        PRINT 'Error updating LineageMap.PathStr: ' + ERROR_MESSAGE();
    END CATCH;



    -- UPDATE flw.with error handling for LineageMap.PathNum
    BEGIN TRY
        BEGIN TRANSACTION;
        UPDATE flw.LineageMap
        SET PathNum = REPLACE(PathNum, @SrcPathElement, @TrgPathElement)
        WHERE PathNum LIKE '%' + @SrcPathElement + '%';
        PRINT '  ' + CAST(@@ROWCOUNT AS VARCHAR(10)) + ' rows updated in LineageMap.PathNum';
        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
            ROLLBACK TRANSACTION;
        PRINT 'Error updating LineageMap.PathNum: ' + ERROR_MESSAGE();
    END CATCH;


    -- UPDATE flw.with error handling for PreIngestionCSV.srcPath
    BEGIN TRY
        BEGIN TRANSACTION;
        UPDATE flw.PreIngestionCSV
        SET srcPath = REPLACE(srcPath, @SrcPathElement, @TrgPathElement)
        WHERE srcPath LIKE '%' + @SrcPathElement + '%';
        PRINT '  ' + CAST(@@ROWCOUNT AS VARCHAR(10)) + ' rows updated in PreIngestionCSV.srcPath';
        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
            ROLLBACK TRANSACTION;
        PRINT 'Error updating PreIngestionCSV.srcPath: ' + ERROR_MESSAGE();
    END CATCH;



    -- UPDATE flw.with error handling for PreIngestionCSV.srcPathMask
    BEGIN TRY
        BEGIN TRANSACTION;
        UPDATE flw.PreIngestionCSV
        SET srcPathMask = REPLACE(srcPathMask, @SrcPathElement, @TrgPathElement)
        WHERE srcPathMask LIKE '%' + @SrcPathElement + '%';
        PRINT '  ' + CAST(@@ROWCOUNT AS VARCHAR(10)) + ' rows updated in PreIngestionCSV.srcPathMask';
        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
            ROLLBACK TRANSACTION;
        PRINT 'Error updating PreIngestionCSV.srcPathMask: ' + ERROR_MESSAGE();
    END CATCH;



    -- UPDATE flw.with error handling for PreIngestionCSV.copyToPath
    BEGIN TRY
        BEGIN TRANSACTION;
        UPDATE flw.PreIngestionCSV
        SET copyToPath = REPLACE(copyToPath, @SrcPathElement, @TrgPathElement)
        WHERE copyToPath LIKE '%' + @SrcPathElement + '%';
        PRINT '  ' + CAST(@@ROWCOUNT AS VARCHAR(10)) + ' rows updated in PreIngestionCSV.copyToPath';
        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
            ROLLBACK TRANSACTION;
        PRINT 'Error updating PreIngestionCSV.copyToPath: ' + ERROR_MESSAGE();
    END CATCH;



    -- UPDATE flw.with error handling for PreIngestionCSV.zipToPath
    BEGIN TRY
        BEGIN TRANSACTION;
        UPDATE flw.PreIngestionCSV
        SET zipToPath = REPLACE(zipToPath, @SrcPathElement, @TrgPathElement)
        WHERE zipToPath LIKE '%' + @SrcPathElement + '%';
        PRINT '  ' + CAST(@@ROWCOUNT AS VARCHAR(10)) + ' rows updated in PreIngestionCSV.zipToPath';
        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
            ROLLBACK TRANSACTION;
        PRINT 'Error updating PreIngestionCSV.zipToPath: ' + ERROR_MESSAGE();
    END CATCH;



    -- UPDATE flw.with error handling for PreIngestionPRC.srcPath
    BEGIN TRY
        BEGIN TRANSACTION;
        UPDATE flw.PreIngestionPRC
        SET srcPath = REPLACE(srcPath, @SrcPathElement, @TrgPathElement)
        WHERE srcPath LIKE '%' + @SrcPathElement + '%';
        PRINT '  ' + CAST(@@ROWCOUNT AS VARCHAR(10)) + ' rows updated in PreIngestionPRC.srcPath';
        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
            ROLLBACK TRANSACTION;
        PRINT 'Error updating PreIngestionPRC.srcPath: ' + ERROR_MESSAGE();
    END CATCH;



    -- UPDATE flw.with error handling for PreIngestionPRQ.srcPath
    BEGIN TRY
        BEGIN TRANSACTION;
        UPDATE flw.PreIngestionPRQ
        SET srcPath = REPLACE(srcPath, @SrcPathElement, @TrgPathElement)
        WHERE srcPath LIKE '%' + @SrcPathElement + '%';
        PRINT '  ' + CAST(@@ROWCOUNT AS VARCHAR(10)) + ' rows updated in PreIngestionPRQ.srcPath';
        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
            ROLLBACK TRANSACTION;
        PRINT 'Error updating PreIngestionPRQ.srcPath: ' + ERROR_MESSAGE();
    END CATCH;



    -- UPDATE flw.with error handling for PreIngestionPRQ.srcPathMask
    BEGIN TRY
        BEGIN TRANSACTION;
        UPDATE flw.PreIngestionPRQ
        SET srcPathMask = REPLACE(srcPathMask, @SrcPathElement, @TrgPathElement)
        WHERE srcPathMask LIKE '%' + @SrcPathElement + '%';
        PRINT '  ' + CAST(@@ROWCOUNT AS VARCHAR(10)) + ' rows updated in PreIngestionPRQ.srcPathMask';
        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
            ROLLBACK TRANSACTION;
        PRINT 'Error updating PreIngestionPRQ.srcPathMask: ' + ERROR_MESSAGE();
    END CATCH;



    -- UPDATE flw.with error handling for PreIngestionPRQ.copyToPath
    BEGIN TRY
        BEGIN TRANSACTION;
        UPDATE flw.PreIngestionPRQ
        SET copyToPath = REPLACE(copyToPath, @SrcPathElement, @TrgPathElement)
        WHERE copyToPath LIKE '%' + @SrcPathElement + '%';
        PRINT '  ' + CAST(@@ROWCOUNT AS VARCHAR(10)) + ' rows updated in PreIngestionPRQ.copyToPath';
        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
            ROLLBACK TRANSACTION;
        PRINT 'Error updating PreIngestionPRQ.copyToPath: ' + ERROR_MESSAGE();
    END CATCH;



    -- UPDATE flw.with error handling for PreIngestionPRQ.zipToPath
    BEGIN TRY
        BEGIN TRANSACTION;
        UPDATE flw.PreIngestionPRQ
        SET zipToPath = REPLACE(zipToPath, @SrcPathElement, @TrgPathElement)
        WHERE zipToPath LIKE '%' + @SrcPathElement + '%';
        PRINT '  ' + CAST(@@ROWCOUNT AS VARCHAR(10)) + ' rows updated in PreIngestionPRQ.zipToPath';
        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
            ROLLBACK TRANSACTION;
        PRINT 'Error updating PreIngestionPRQ.zipToPath: ' + ERROR_MESSAGE();
    END CATCH;



    -- UPDATE flw.with error handling for PreIngestionXLS.srcPath
    BEGIN TRY
        BEGIN TRANSACTION;
        UPDATE flw.PreIngestionXLS
        SET srcPath = REPLACE(srcPath, @SrcPathElement, @TrgPathElement)
        WHERE srcPath LIKE '%' + @SrcPathElement + '%';
        PRINT '  ' + CAST(@@ROWCOUNT AS VARCHAR(10)) + ' rows updated in PreIngestionXLS.srcPath';
        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
            ROLLBACK TRANSACTION;
        PRINT 'Error updating PreIngestionXLS.srcPath: ' + ERROR_MESSAGE();
    END CATCH;



    -- UPDATE flw.with error handling for PreIngestionXLS.srcPathMask
    BEGIN TRY
        BEGIN TRANSACTION;
        UPDATE flw.PreIngestionXLS
        SET srcPathMask = REPLACE(srcPathMask, @SrcPathElement, @TrgPathElement)
        WHERE srcPathMask LIKE '%' + @SrcPathElement + '%';
        PRINT '  ' + CAST(@@ROWCOUNT AS VARCHAR(10)) + ' rows updated in PreIngestionXLS.srcPathMask';
        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
            ROLLBACK TRANSACTION;
        PRINT 'Error updating PreIngestionXLS.srcPathMask: ' + ERROR_MESSAGE();
    END CATCH;



    -- UPDATE flw.with error handling for PreIngestionXLS.copyToPath
    BEGIN TRY
        BEGIN TRANSACTION;
        UPDATE flw.PreIngestionXLS
        SET copyToPath = REPLACE(copyToPath, @SrcPathElement, @TrgPathElement)
        WHERE copyToPath LIKE '%' + @SrcPathElement + '%';
        PRINT '  ' + CAST(@@ROWCOUNT AS VARCHAR(10)) + ' rows updated in PreIngestionXLS.copyToPath';
        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
            ROLLBACK TRANSACTION;
        PRINT 'Error updating PreIngestionXLS.copyToPath: ' + ERROR_MESSAGE();
    END CATCH;



    -- UPDATE flw.with error handling for PreIngestionXLS.zipToPath
    BEGIN TRY
        BEGIN TRANSACTION;
        UPDATE flw.PreIngestionXLS
        SET zipToPath = REPLACE(zipToPath, @SrcPathElement, @TrgPathElement)
        WHERE zipToPath LIKE '%' + @SrcPathElement + '%';
        PRINT '  ' + CAST(@@ROWCOUNT AS VARCHAR(10)) + ' rows updated in PreIngestionXLS.zipToPath';
        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
            ROLLBACK TRANSACTION;
        PRINT 'Error updating PreIngestionXLS.zipToPath: ' + ERROR_MESSAGE();
    END CATCH;



    -- UPDATE flw.with error handling for PreIngestionJSN.srcPath
    BEGIN TRY
        BEGIN TRANSACTION;
        UPDATE flw.PreIngestionJSN
        SET srcPath = REPLACE(srcPath, @SrcPathElement, @TrgPathElement)
        WHERE srcPath LIKE '%' + @SrcPathElement + '%';
        PRINT '  ' + CAST(@@ROWCOUNT AS VARCHAR(10)) + ' rows updated in PreIngestionJSN.srcPath';
        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
            ROLLBACK TRANSACTION;
        PRINT 'Error updating PreIngestionJSN.srcPath: ' + ERROR_MESSAGE();
    END CATCH;



    -- UPDATE flw.with error handling for PreIngestionJSN.srcPathMask
    BEGIN TRY
        BEGIN TRANSACTION;
        UPDATE flw.PreIngestionJSN
        SET srcPathMask = REPLACE(srcPathMask, @SrcPathElement, @TrgPathElement)
        WHERE srcPathMask LIKE '%' + @SrcPathElement + '%';
        PRINT '  ' + CAST(@@ROWCOUNT AS VARCHAR(10)) + ' rows updated in PreIngestionJSN.srcPathMask';
        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
            ROLLBACK TRANSACTION;
        PRINT 'Error updating PreIngestionJSN.srcPathMask: ' + ERROR_MESSAGE();
    END CATCH;



    -- UPDATE flw.with error handling for PreIngestionJSN.copyToPath
    BEGIN TRY
        BEGIN TRANSACTION;
        UPDATE flw.PreIngestionJSN
        SET copyToPath = REPLACE(copyToPath, @SrcPathElement, @TrgPathElement)
        WHERE copyToPath LIKE '%' + @SrcPathElement + '%';
        PRINT '  ' + CAST(@@ROWCOUNT AS VARCHAR(10)) + ' rows updated in PreIngestionJSN.copyToPath';
        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
            ROLLBACK TRANSACTION;
        PRINT 'Error updating PreIngestionJSN.copyToPath: ' + ERROR_MESSAGE();
    END CATCH;


    -- UPDATE flw.with error handling for PreIngestionJSN.zipToPath
    BEGIN TRY
        BEGIN TRANSACTION;
        UPDATE flw.PreIngestionJSN
        SET zipToPath = REPLACE(zipToPath, @SrcPathElement, @TrgPathElement)
        WHERE zipToPath LIKE '%' + @SrcPathElement + '%';
        PRINT '  ' + CAST(@@ROWCOUNT AS VARCHAR(10)) + ' rows updated in PreIngestionJSN.zipToPath';
        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
            ROLLBACK TRANSACTION;
        PRINT 'Error updating PreIngestionJSN.zipToPath: ' + ERROR_MESSAGE();
    END CATCH;



    -- UPDATE flw.with error handling for PreIngestionXML.srcPath
    BEGIN TRY
        BEGIN TRANSACTION;
        UPDATE flw.PreIngestionXML
        SET srcPath = REPLACE(srcPath, @SrcPathElement, @TrgPathElement)
        WHERE srcPath LIKE '%' + @SrcPathElement + '%';
        PRINT '  ' + CAST(@@ROWCOUNT AS VARCHAR(10)) + ' rows updated in PreIngestionXML.srcPath';
        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
            ROLLBACK TRANSACTION;
        PRINT 'Error updating PreIngestionXML.srcPath: ' + ERROR_MESSAGE();
    END CATCH;



    -- UPDATE flw.with error handling for PreIngestionXML.srcPathMask
    BEGIN TRY
        BEGIN TRANSACTION;
        UPDATE flw.PreIngestionXML
        SET srcPathMask = REPLACE(srcPathMask, @SrcPathElement, @TrgPathElement)
        WHERE srcPathMask LIKE '%' + @SrcPathElement + '%';
        PRINT '  ' + CAST(@@ROWCOUNT AS VARCHAR(10)) + ' rows updated in PreIngestionXML.srcPathMask';
        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
            ROLLBACK TRANSACTION;
        PRINT 'Error updating PreIngestionXML.srcPathMask: ' + ERROR_MESSAGE();
    END CATCH;



    -- UPDATE flw.with error handling for PreIngestionXML.copyToPath
    BEGIN TRY
        BEGIN TRANSACTION;
        UPDATE flw.PreIngestionXML
        SET copyToPath = REPLACE(copyToPath, @SrcPathElement, @TrgPathElement)
        WHERE copyToPath LIKE '%' + @SrcPathElement + '%';
        PRINT '  ' + CAST(@@ROWCOUNT AS VARCHAR(10)) + ' rows updated in PreIngestionXML.copyToPath';
        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
            ROLLBACK TRANSACTION;
        PRINT 'Error updating PreIngestionXML.copyToPath: ' + ERROR_MESSAGE();
    END CATCH;



    -- UPDATE flw.with error handling for PreIngestionXML.zipToPath
    BEGIN TRY
        BEGIN TRANSACTION;
        UPDATE flw.PreIngestionXML
        SET zipToPath = REPLACE(zipToPath, @SrcPathElement, @TrgPathElement)
        WHERE zipToPath LIKE '%' + @SrcPathElement + '%';
        PRINT '  ' + CAST(@@ROWCOUNT AS VARCHAR(10)) + ' rows updated in PreIngestionXML.zipToPath';
        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
            ROLLBACK TRANSACTION;
        PRINT 'Error updating PreIngestionXML.zipToPath: ' + ERROR_MESSAGE();
    END CATCH;


END;

-- Query to count total possible replacements
SELECT *
FROM
(
    SELECT 'flw.SysLogExport' AS TableName,
           COUNT(*) AS ReplacementsCount
    FROM flw.SysLogExport
    WHERE FilePath_DW LIKE '%' + @TrgPathElement + '%'
    UNION ALL
    SELECT 'flw.SysSourceControl' AS TableName,
           COUNT(*) AS ReplacementsCount
    FROM flw.SysSourceControl
    WHERE ScriptToPath LIKE '%' + @TrgPathElement + '%'
    UNION ALL
    SELECT 'flw.Export' AS TableName,
           COUNT(*) AS ReplacementsCount
    FROM flw.Export
    WHERE trgPath LIKE '%' + @TrgPathElement + '%'
    UNION ALL
    SELECT 'flw.Invoke' AS TableName,
           COUNT(*) AS ReplacementsCount
    FROM flw.Invoke
    WHERE InvokePath LIKE '%' + @TrgPathElement + '%'
    UNION ALL
    SELECT 'flw.LineageMap (PathStr)' AS TableName,
           COUNT(*) AS ReplacementsCount
    FROM flw.LineageMap
    WHERE PathStr LIKE '%' + @TrgPathElement + '%'
    UNION ALL
    SELECT 'flw.LineageMap (PathNum)' AS TableName,
           COUNT(*) AS ReplacementsCount
    FROM flw.LineageMap
    WHERE PathNum LIKE '%' + @TrgPathElement + '%'
    UNION ALL
    SELECT 'flw.PreIngestionCSV (srcPath)' AS TableName,
           COUNT(*) AS ReplacementsCount
    FROM flw.PreIngestionCSV
    WHERE srcPath LIKE '%' + @TrgPathElement + '%'
    UNION ALL
    SELECT 'flw.PreIngestionCSV (srcPathMask)' AS TableName,
           COUNT(*) AS ReplacementsCount
    FROM flw.PreIngestionCSV
    WHERE srcPathMask LIKE '%' + @TrgPathElement + '%'
    UNION ALL
    SELECT 'flw.PreIngestionCSV (copyToPath)' AS TableName,
           COUNT(*) AS ReplacementsCount
    FROM flw.PreIngestionCSV
    WHERE copyToPath LIKE '%' + @TrgPathElement + '%'
    UNION ALL
    SELECT 'flw.PreIngestionCSV (zipToPath)' AS TableName,
           COUNT(*) AS ReplacementsCount
    FROM flw.PreIngestionCSV
    WHERE zipToPath LIKE '%' + @TrgPathElement + '%'
    UNION ALL
    SELECT 'flw.PreIngestionPRC' AS TableName,
           COUNT(*) AS ReplacementsCount
    FROM flw.PreIngestionPRC
    WHERE srcPath LIKE '%' + @TrgPathElement + '%'
    UNION ALL
    SELECT 'flw.PreIngestionPRQ (srcPath)' AS TableName,
           COUNT(*) AS ReplacementsCount
    FROM flw.PreIngestionPRQ
    WHERE srcPath LIKE '%' + @TrgPathElement + '%'
    UNION ALL
    SELECT 'flw.PreIngestionPRQ (srcPathMask)' AS TableName,
           COUNT(*) AS ReplacementsCount
    FROM flw.PreIngestionPRQ
    WHERE srcPathMask LIKE '%' + @TrgPathElement + '%'
    UNION ALL
    SELECT 'flw.PreIngestionPRQ (copyToPath)' AS TableName,
           COUNT(*) AS ReplacementsCount
    FROM flw.PreIngestionPRQ
    WHERE copyToPath LIKE '%' + @TrgPathElement + '%'
    UNION ALL
    SELECT 'flw.PreIngestionPRQ (zipToPath)' AS TableName,
           COUNT(*) AS ReplacementsCount
    FROM flw.PreIngestionPRQ
    WHERE zipToPath LIKE '%' + @TrgPathElement + '%'
    UNION ALL
    SELECT 'flw.PreIngestionXLS (srcPath)' AS TableName,
           COUNT(*) AS ReplacementsCount
    FROM flw.PreIngestionXLS
    WHERE srcPath LIKE '%' + @TrgPathElement + '%'
    UNION ALL
    SELECT 'flw.PreIngestionXLS (srcPathMask)' AS TableName,
           COUNT(*) AS ReplacementsCount
    FROM flw.PreIngestionXLS
    WHERE srcPathMask LIKE '%' + @TrgPathElement + '%'
    UNION ALL
    SELECT 'flw.PreIngestionXLS (copyToPath)' AS TableName,
           COUNT(*) AS ReplacementsCount
    FROM flw.PreIngestionXLS
    WHERE copyToPath LIKE '%' + @TrgPathElement + '%'
    UNION ALL
    SELECT 'flw.PreIngestionXLS (zipToPath)' AS TableName,
           COUNT(*) AS ReplacementsCount
    FROM flw.PreIngestionXLS
    WHERE zipToPath LIKE '%' + @TrgPathElement + '%'
    UNION ALL
    SELECT 'flw.PreIngestionJSN (srcPath)' AS TableName,
           COUNT(*) AS ReplacementsCount
    FROM flw.PreIngestionJSN
    WHERE srcPath LIKE '%' + @TrgPathElement + '%'
    UNION ALL
    SELECT 'flw.PreIngestionJSN (srcPathMask)' AS TableName,
           COUNT(*) AS ReplacementsCount
    FROM flw.PreIngestionJSN
    WHERE srcPathMask LIKE '%' + @TrgPathElement + '%'
    UNION ALL
    SELECT 'flw.PreIngestionJSN (copyToPath)' AS TableName,
           COUNT(*) AS ReplacementsCount
    FROM flw.PreIngestionJSN
    WHERE copyToPath LIKE '%' + @TrgPathElement + '%'
    UNION ALL
    SELECT 'flw.PreIngestionJSN (zipToPath)' AS TableName,
           COUNT(*) AS ReplacementsCount
    FROM flw.PreIngestionJSN
    WHERE zipToPath LIKE '%' + @TrgPathElement + '%'
    UNION ALL
    SELECT 'flw.PreIngestionXML (srcPath)' AS TableName,
           COUNT(*) AS ReplacementsCount
    FROM flw.PreIngestionXML
    WHERE srcPath LIKE '%' + @TrgPathElement + '%'
    UNION ALL
    SELECT 'flw.PreIngestionXML (srcPathMask)' AS TableName,
           COUNT(*) AS ReplacementsCount
    FROM flw.PreIngestionXML
    WHERE srcPathMask LIKE '%' + @TrgPathElement + '%'
    UNION ALL
    SELECT 'flw.PreIngestionXML (copyToPath)' AS TableName,
           COUNT(*) AS ReplacementsCount
    FROM flw.PreIngestionXML
    WHERE copyToPath LIKE '%' + @TrgPathElement + '%'
    UNION ALL
    SELECT 'flw.PreIngestionXML (zipToPath)' AS TableName,
           COUNT(*) AS ReplacementsCount
    FROM flw.PreIngestionXML
    WHERE zipToPath LIKE '%' + @TrgPathElement + '%'
) AS Replacements;
```

## Important Notes
- Always back up your database before running this script in update mode
- The script uses transaction safety to prevent partial updates
- Path migration may impact dependent processes - ensure all systems are updated accordingly