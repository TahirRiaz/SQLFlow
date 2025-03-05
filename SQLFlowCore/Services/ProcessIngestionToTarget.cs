using System;
using Microsoft.Data.SqlClient;
using System.Diagnostics;
using System.Linq;
using System.Text;
using SQLFlowCore.Engine.Utils.DataExt;
using SQLFlowCore.Common;
using SQLFlowCore.Services.Schema;
using SQLFlowCore.Logger;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using Octokit;

namespace SQLFlowCore.Services;

internal static class ProcessIngestionToTarget
{
    internal static string GetRowCountUpdateCommand(bool trgIsSynapse, string updateLable)
    {
        if (trgIsSynapse)
        {
            return $@"
        OPTION (LABEL = '{updateLable}') 
        INSERT INTO #InsertUpdates (Inserts,Updates) SELECT 0, (SELECT top 1 row_count
            FROM sys.dm_pdw_request_steps s, sys.dm_pdw_exec_requests r  
            Where r.request_id = s.request_id
            and row_count > -1
            and r.[label] = '{updateLable}'
            order by r.[end_time] desc) ";
        }
        else
        {
            return "INSERT INTO #InsertUpdates (Inserts,Updates) SELECT 0, @@ROWCOUNT ";
        }
    }

    internal static string GetRowCountInsertCommand(bool trgIsSynapse, string insertLable)
    {
        if (trgIsSynapse)
        {
            return $@"
        OPTION (LABEL = '{insertLable}')
        INSERT INTO #InsertUpdates (Inserts,Updates) SELECT (SELECT top 1 row_count  
            FROM sys.dm_pdw_request_steps s, sys.dm_pdw_exec_requests r
            Where r.request_id = s.request_id 
            and row_count > -1
            and r.[label] = '{insertLable}'
            order by r.[end_time] desc), 0  ";
        }
        else
        {
            return "INSERT INTO #InsertUpdates (Inserts,Updates) SELECT @@ROWCOUNT, 0 ";
        }
    }

    internal static string GetUpdateStgTrgCommand(SyncOutput trgShm, string srcDS4Update, string trgDatabase,
        string trgSchema, string trgObject, string joinExp, string dataSetColumnQuoted, string cmdRowCountUpdate,
        bool skipUpdateExsisting)
    {

        var updateStgTrgCmd = "";
        if (!skipUpdateExsisting && trgShm.CheckSumColumnsSrc.Length > 2)
        {
            int noOfSrcChkSumCols = trgShm.CheckSumColumnsSrc.Split(',').Length;
            string srcChkSum = $"CAST({trgShm.CheckSumColumnsSrc} as varchar(4000))";

            int noOfTrgChkSumCols = trgShm.CheckSumColumnsTrg.Split(',').Length;
            string trgChkSum = $"CAST({trgShm.CheckSumColumnsSrc} as varchar(4000))";

            if (noOfSrcChkSumCols > 1)
            {
                srcChkSum = $"CONCAT({trgShm.CheckSumColumnsSrc})";
            }

            if (noOfTrgChkSumCols > 1)
            {
                trgChkSum = $"CONCAT({trgShm.CheckSumColumnsTrg})";
            }

            updateStgTrgCmd = $@"
        UPDATE TRG
        SET {trgShm.UpdateColumnsSrcTrg}  
        FROM {srcDS4Update} src
        INNER JOIN  [{trgDatabase}].[{trgSchema}].[{trgObject}] trg
        ON {joinExp}
        WHERE HASHBYTES('SHA2_512', {srcChkSum}) <> HASHBYTES('SHA2_512', {trgChkSum}) 
        AND src.{dataSetColumnQuoted} = @LoopVal
        {cmdRowCountUpdate}
";
        }
        else if (!skipUpdateExsisting)
        {
            //Update all rows. No valid columns for checksum calculation
            updateStgTrgCmd = $@"
        UPDATE TRG
        SET {trgShm.UpdateColumnsSrcTrg}
        FROM  {srcDS4Update} src  
        INNER JOIN  [{trgDatabase}].[{trgSchema}].[{trgObject}] trg
        ON {joinExp}
        WHERE src.{dataSetColumnQuoted} = @LoopVal 
        {cmdRowCountUpdate}
         ";
        }

        return updateStgTrgCmd;
    }

    internal static string GetInsertStgTrgCommand(SyncOutput trgShm, string trgDatabase, string stgSchema,
        string stagingTableName, string trgSchema, string trgObject, string joinExp, string dataSetColumnQuoted,
        string outerJoinExp, string cmdRowCountInsert, bool skipInsertNew)
    {
        var insertStgTrgCmd = "";
        if (!skipInsertNew)
        {
            insertStgTrgCmd = $@"
        INSERT INTO [{trgDatabase}].[{trgSchema}].[{trgObject}] ({trgShm.TrgColumns}) 
        SELECT {(trgShm.ImageDataTypeFound ? "" : "DISTINCT")} {trgShm.TrgColumnsWithSrc} FROM  [{trgDatabase}].[{stgSchema}].[{stagingTableName}] src
        LEFT OUTER JOIN  [{trgDatabase}].[{trgSchema}].[{trgObject}] trg  
        ON {joinExp}
        WHERE src.{dataSetColumnQuoted} = @LoopVal 
        AND {outerJoinExp} IS NULL 
        {cmdRowCountInsert}
";
        }

        return insertStgTrgCmd;
    }

    internal static string GetDsCommand(SyncOutput trgShm, string trgDatabase, string stgSchema, string stagingTableName, string dataSetColumnQuoted,
        string datasetDupeColList, string datasetDupeColListWithSrc, string updateStgTrgCmd, string insertStgTrgCmd)
    {
        return $@"
        SET NOCOUNT ON;
        
        --Create An Index on the DS Column
        IF NOT EXISTS (   SELECT      b.name, 
                                      a.name
                            FROM      [{trgDatabase}].sys.indexes AS a  
                           INNER JOIN [{trgDatabase}].sys.objects AS b
                              ON a.object_id = b.object_id
                           WHERE      a.name LIKE 'NCI_DataSets'
                             AND      OBJECT_SCHEMA_NAME(b.object_id, DB_ID('{trgDatabase}')) = '{stgSchema}'
                             AND      b.name                                        = '{stagingTableName}')
        BEGIN
            CREATE NONCLUSTERED INDEX [NCI_DataSets] ON  [{trgDatabase}].[{stgSchema}].[{stagingTableName}]({dataSetColumnQuoted} ASC)
        END;

        

        --Fix duplicate key in same DataSet
        IF OBJECT_ID(N'tempdb..#DupeDataSet') IS NOT NULL
        BEGIN
	        DROP TABLE #DupeDataSet;
        END;

        IF OBJECT_ID(N'tempdb..#DataSets') IS NOT NULL  
        BEGIN
	        DROP TABLE #DataSets;
        END;

        SELECT DISTINCT {dataSetColumnQuoted} INTO #DupeDataSet
        FROM [{trgDatabase}].[{stgSchema}].[{stagingTableName}]
        GROUP BY {datasetDupeColList}
        HAVING( COUNT(1) > 1)
        
        IF EXISTS (SELECT 1 FROM #DupeDataSet)
        BEGIN
            IF OBJECT_ID(N'tempdb..#DuplicateRows') IS NOT NULL
            BEGIN
                DROP TABLE #DuplicateRows;
            END;

            SELECT DISTINCT {trgShm.TrgColumnsWithSrc.Replace($"src.{dataSetColumnQuoted}", $"src.{dataSetColumnQuoted} + ROW_NUMBER() OVER (PARTITION BY {datasetDupeColListWithSrc} ORDER BY (SELECT NULL)) AS {dataSetColumnQuoted}")}  
                INTO #DuplicateRows
            FROM [{trgDatabase}].[{stgSchema}].[{stagingTableName}] src
            INNER JOIN #DupeDataSet tmp ON src.{dataSetColumnQuoted} = tmp.{dataSetColumnQuoted}

            DELETE FROM Trg
            FROM [{trgDatabase}].[{stgSchema}].[{stagingTableName}] trg
            INNER JOIN #DupeDataSet src ON src.{dataSetColumnQuoted} = trg.{dataSetColumnQuoted}

            Insert into [{trgDatabase}].[{stgSchema}].[{stagingTableName}]
            SELECT * FROM #DuplicateRows;

        END

        --Fetch DataSet  
        ;WITH base
        AS (
            SELECT {(trgShm.ImageDataTypeFound ? "" : "DISTINCT")} {dataSetColumnQuoted}  
	        FROM  [{trgDatabase}].[{stgSchema}].[{stagingTableName}]
        )
        SELECT  {dataSetColumnQuoted}, ROW_NUMBER() OVER (ORDER BY {dataSetColumnQuoted}) AS RN
            INTO #DataSets 
        FROM base


        IF OBJECT_ID(N'tempdb..#InsertUpdates') IS NOT NULL  
        BEGIN
            DROP TABLE #InsertUpdates;
        END;

        CREATE TABLE #InsertUpdates
        (
            [Inserts] [INT] NULL,
            [Updates] [INT] NULL  
        )

        ----------Start Merge Process ----------;
        DECLARE @LoopVal VARCHAR(255);
        DECLARE @Counter INT = 1;
        DECLARE @dsCount INT = (SELECT COUNT_BIG(1) FROM #DataSets);

        -- Loop through records in Temp table  
        WHILE @Counter <= @dsCount
        BEGIN
            SELECT @LoopVal = {dataSetColumnQuoted}
              FROM #DataSets AS T
            WHERE RN = @Counter;
                
            {updateStgTrgCmd}
            
            {insertStgTrgCmd}

             SET @counter = @counter + 1;
        END;
        --Return Total Inserts and Updates
        SELECT IsNull(Sum(Inserts),0) as Inserts, Isnull(Sum(Updates),0) as Updates FROM #InsertUpdates  ";
    }

    internal static string GetDsCommandBatch(SyncOutput trgShm, string trgDatabase, string stgSchema, string stagingTableName, string trgSchema, string trgObject, string dataSetColumnQuoted,
    string datasetDupeColList, string datasetDupeColListWithSrc, string updateStgTrgCmd, string insertStgTrgCmd, string outerJoinExp, string joinExp)
    {
        string keyColumnsQuotedWithSrc = string.Join(", ", trgShm.keyColumnsQuoted.Split(',').Select(k => $"src.{k.Trim()}"));
        return $@"
        SET NOCOUNT ON;
        
        -- Create An Index on the DS Column
        IF NOT EXISTS (SELECT 1 FROM [{trgDatabase}].sys.indexes WHERE name = 'NCI_DataSets'
                        AND object_id = OBJECT_ID('[{trgDatabase}].[{stgSchema}].[{stagingTableName}]'))
        BEGIN
            CREATE NONCLUSTERED INDEX [NCI_DataSets] ON [{trgDatabase}].[{stgSchema}].[{stagingTableName}] ({dataSetColumnQuoted} ASC)
        END;

        -- Create temp table for UpdateKeys
        IF OBJECT_ID('tempdb..#UpdateKeys') IS NOT NULL DROP TABLE #UpdateKeys;
        
        SELECT {keyColumnsQuotedWithSrc}, src.{dataSetColumnQuoted}, ROW_NUMBER() OVER (PARTITION BY src.{dataSetColumnQuoted} ORDER BY (SELECT NULL)) AS RowNum
        INTO #UpdateKeys
        FROM [{trgDatabase}].[{stgSchema}].[{stagingTableName}] src
        INNER JOIN [{trgDatabase}].[{trgSchema}].[{trgObject}] trg ON {joinExp};
        
        CREATE CLUSTERED INDEX IX_UpdateKeys_RowNum ON #UpdateKeys (RowNum);
        CREATE STATISTICS UpdateKeys_Stats ON #UpdateKeys ({trgShm.keyColumnsQuoted});

        -- Create temp table for InsertKeys
        IF OBJECT_ID('tempdb..#InsertKeys') IS NOT NULL DROP TABLE #InsertKeys;
        
        SELECT {keyColumnsQuotedWithSrc}, src.{dataSetColumnQuoted}, ROW_NUMBER() OVER (PARTITION BY src.{dataSetColumnQuoted} ORDER BY (SELECT NULL)) AS RowNum  
        INTO #InsertKeys
        FROM [{trgDatabase}].[{stgSchema}].[{stagingTableName}] src
        LEFT JOIN [{trgDatabase}].[{trgSchema}].[{trgObject}] trg ON {joinExp}
        WHERE {outerJoinExp} IS NULL;
        
        CREATE CLUSTERED INDEX IX_InsertKeys_RowNum ON #InsertKeys (RowNum);
        CREATE STATISTICS InsertKeys_Stats ON #InsertKeys ({trgShm.keyColumnsQuoted});

        -- Create temp table for DataSets
        IF OBJECT_ID(N'tempdb..#DataSets') IS NOT NULL DROP TABLE #DataSets;
        
        ;WITH base AS (
            SELECT {(trgShm.ImageDataTypeFound ? "" : "DISTINCT")} {dataSetColumnQuoted}
            FROM [{trgDatabase}].[{stgSchema}].[{stagingTableName}]
        )  
        SELECT {dataSetColumnQuoted}, ROW_NUMBER() OVER (ORDER BY {dataSetColumnQuoted}) AS RN
        INTO #DataSets
        FROM base;
        
        -- Create temp table for InsertUpdates
        IF OBJECT_ID(N'tempdb..#InsertUpdates') IS NOT NULL DROP TABLE #InsertUpdates;
        
        CREATE TABLE #InsertUpdates (
            [Inserts] INT NULL,
            [Updates] INT NULL  
        );

        DECLARE @LoopVal VARCHAR(255);
        DECLARE @Counter INT = 1;
        DECLARE @dsCount INT = (SELECT COUNT_BIG(1) FROM #DataSets);
        
        WHILE @Counter <= @dsCount
        BEGIN
            SELECT @LoopVal = {dataSetColumnQuoted}
            FROM #DataSets AS T
            WHERE RN = @Counter;

            {updateStgTrgCmd}

            {insertStgTrgCmd}

            SET @counter = @counter + 1;
        END;

        SELECT ISNULL(SUM(Inserts), 0) AS Inserts, ISNULL(SUM(Updates), 0) AS Updates
        FROM #InsertUpdates;
    ";
    }

    internal static string GetUpdateStgTrgCommandBatch(SyncOutput trgShm, string srcDS4Update, string trgDatabase,
        string trgSchema, string trgObject, string dataSetColumnQuoted, string joinExp, int batchSize)
    {
        string joinExp4UpdateKey = string.Join(" AND ", trgShm.keyColumnsQuoted.Split(',').Select(k => $"src.{k.Trim()} = UK.{k.Trim()}"));

        return $@"
        SET NOCOUNT ON;
        DECLARE @BatchSize2 INT = {batchSize};

        DECLARE @StartRowNum2 INT = 1;
        DECLARE @EndRowNum2 INT = @BatchSize2;
        DECLARE @TotalRows2 INT = (SELECT COUNT(1) FROM #UpdateKeys WHERE {dataSetColumnQuoted} = @LoopVal);  
        DECLARE @UpdateCount2 INT = 0;

        WHILE @StartRowNum2 <= @TotalRows2
        BEGIN
            UPDATE TRG
            SET {trgShm.UpdateColumnsSrcTrg}
            FROM {srcDS4Update} src
            INNER JOIN #UpdateKeys UK ON {joinExp4UpdateKey}  
            INNER JOIN [{trgDatabase}].[{trgSchema}].[{trgObject}] trg ON {joinExp}
            WHERE UK.{dataSetColumnQuoted} = @LoopVal
                AND {(trgShm.CheckSumColumnsSrc.Length > 2
                    ? $"HASHBYTES('SHA2_512', CONCAT({trgShm.CheckSumColumnsSrc})) <> HASHBYTES('SHA2_512', CONCAT({trgShm.CheckSumColumnsTrg})) AND"
                    : "")}
                UK.RowNum BETWEEN @StartRowNum2 AND @EndRowNum2;
            
            SELECT @UpdateCount2 = @UpdateCount2 + @@ROWCOUNT;

            SET @StartRowNum2 = @EndRowNum2 + 1;
            SET @EndRowNum2 = @StartRowNum2 + @BatchSize2 - 1;
            IF @EndRowNum2 > @TotalRows2 SET @EndRowNum2 = @TotalRows2;
        END
        
        INSERT INTO #InsertUpdates (Updates) VALUES (@UpdateCount2);
    ";
    }

    internal static string GetInsertStgTrgCommandBatch(SyncOutput trgShm, string trgDatabase, string stgSchema,
        string stagingTableName, string trgSchema, string trgObject, string dataSetColumnQuoted, int batchSize)
    {
        string joinExp4InsertKey = string.Join(" AND ", trgShm.keyColumnsQuoted.Split(',').Select(k => $"src.{k.Trim()} = IK.{k.Trim()}"));

        return $@"
        DECLARE @BatchSize INT = {batchSize};
        
        DECLARE @StartRowNum INT = 1;
        DECLARE @EndRowNum INT = @BatchSize;
        DECLARE @TotalRows INT = (SELECT COUNT(1) FROM #InsertKeys WHERE {dataSetColumnQuoted} = @LoopVal);
        DECLARE @InsertCount INT = 0;
        
        WHILE @StartRowNum <= @TotalRows
        BEGIN  
            INSERT INTO [{trgDatabase}].[{trgSchema}].[{trgObject}] ({trgShm.TrgColumns})
            SELECT {(trgShm.ImageDataTypeFound ? "" : "DISTINCT")} {trgShm.TrgColumnsWithSrc}  
            FROM [{trgDatabase}].[{stgSchema}].[{stagingTableName}] src
            INNER JOIN #InsertKeys IK ON {joinExp4InsertKey}
            WHERE IK.{dataSetColumnQuoted} = @LoopVal  
                AND IK.RowNum BETWEEN @StartRowNum AND @EndRowNum;
            
            SELECT @InsertCount = @InsertCount + @@ROWCOUNT;
            
            SET @StartRowNum = @EndRowNum + 1;
            SET @EndRowNum = @StartRowNum + @BatchSize - 1;
            IF @EndRowNum > @TotalRows SET @EndRowNum = @TotalRows;
        END
        
        INSERT INTO #InsertUpdates (Inserts) VALUES (@InsertCount);
    ";
    }


    internal static void ProcessWithoutDataSetColumn(bool skipUpdateExsisting, SyncOutput trgShm, string trgDatabase,
        string trgSchema, string trgObject, string joinExp, bool skipInsertNew, string stagingTableName,
        string outerJoinExp, string stgSchema, SqlFlowParam sqlFlowParam, RealTimeLogger logger,
        SqlConnection trgSqlCon, int bulkLoadTimeoutInSek, bool truncateTrg, ref string logUpdateCmd, ref long logUpdated,
        ref string logErrorUpdate, ref string logInsertCmd, ref long logInserted, ref string logErrorInsert)
    {

        using (logger.TrackOperation("UpdateDeltaDs"))
        {
            if (skipUpdateExsisting)
            {
                logger.LogInformation("Update on existing staging rows skipped");
            }
            else
            {
                string srcDS4Update = $@"[{trgDatabase}].[{stgSchema}].[{stagingTableName}]";

                int noOfSrcChkSumCols = trgShm.CheckSumColumnsSrc.Split(',').Length;
                string srcChkSum = $"CAST({trgShm.CheckSumColumnsSrc}  as varchar(4000))";

                int noOfTrgChkSumCols = trgShm.CheckSumColumnsTrg.Split(',').Length;
                string trgChkSum = $"CAST({trgShm.CheckSumColumnsSrc}  as varchar(4000))";

                if (noOfSrcChkSumCols > 1)
                {
                    srcChkSum = $"CONCAT({trgShm.CheckSumColumnsSrc})";
                }

                if (noOfTrgChkSumCols > 1)
                {
                    trgChkSum = $"CONCAT({trgShm.CheckSumColumnsTrg})";
                }

                var updateStgTrgCmd = "";
                if (trgShm.CheckSumColumnsSrc.Length > 2)
                {
                    updateStgTrgCmd = $@"
        SET NOCOUNT ON;
        UPDATE TRG
        SET {trgShm.UpdateColumnsSrcTrg}
        FROM {srcDS4Update} src
        INNER JOIN  [{trgDatabase}].[{trgSchema}].[{trgObject}] trg
        ON {joinExp}
        WHERE HASHBYTES('SHA2_512', {srcChkSum}) <> HASHBYTES('SHA2_512', {trgChkSum});
SELECT @@Rowcount AS UpdatedRows;

";
                }
                else
                {
                    //Update all rows. No valid columns for checksum calculation
                    updateStgTrgCmd = $@"
        SET NOCOUNT ON;
        UPDATE TRG
        SET {trgShm.UpdateColumnsSrcTrg}
        FROM  {srcDS4Update} src
        INNER JOIN  [{trgDatabase}].[{trgSchema}].[{trgObject}] trg
        ON {joinExp};
        SELECT @@Rowcount AS UpdatedRows;
";
                }

                logger.LogCodeBlock("Update Changed Rows:", updateStgTrgCmd);
                logUpdateCmd = updateStgTrgCmd;

                //Merge the delta set
                try
                {
                    if (truncateTrg == false) //There are no rows to update if target is truncated trgVerssioning == false &&
                    {
                        using (var cmd = new SqlCommand(updateStgTrgCmd, trgSqlCon) { CommandTimeout = bulkLoadTimeoutInSek })
                        {
                            //cmd.CommandType = CommandType.Text;
                            logUpdated = Convert.ToInt64(cmd.ExecuteScalar());
                        }
                        
                        logUpdated = logUpdated < 0 ? 0 : logUpdated;
                        logger.LogInformation($"Staging rows updated with target table {logUpdated}");
                    }
                }
                catch (SqlException ex)
                {
                    logErrorUpdate = ex.Message;
                    logger.LogError(ex.Message);
                }
                
            }
        }

        using (logger.TrackOperation("InsertDeltaDs"))
        {
            if (skipInsertNew)
            {
                logger.LogInformation($"Insert of new staging rows skipped");
            }
            else
            {
                var insertStgTrgCmd = $@"
        SET NOCOUNT ON;
        INSERT INTO [{trgDatabase}].[{trgSchema}].[{trgObject}] ({trgShm.TrgColumns})
        SELECT {(trgShm.ImageDataTypeFound ? "" : "DISTINCT")} {trgShm.TrgColumnsWithSrc}
        FROM  [{trgDatabase}].[{stgSchema}].[{stagingTableName}] src
        LEFT OUTER JOIN  [{trgDatabase}].[{trgSchema}].[{trgObject}] trg
        ON {joinExp}
        WHERE {outerJoinExp} IS NULL;
 SELECT @@Rowcount AS InsertedRows;
";

                logger.LogCodeBlock("Insert New Rows:", insertStgTrgCmd);
                logInsertCmd = insertStgTrgCmd;
                
                //Merge the delta set
                var insCmd = new ExecNonQuery(trgSqlCon, insertStgTrgCmd, bulkLoadTimeoutInSek);

                //if there are no new files
                if (insertStgTrgCmd.Length > 0)
                {
                    try
                    {
                        using (var cmd = new SqlCommand(insertStgTrgCmd, trgSqlCon) { CommandTimeout = bulkLoadTimeoutInSek })
                        {
                            //cmd.CommandType = CommandType.Text;
                            logInserted = Convert.ToInt64(cmd.ExecuteScalar());
                        }

                        logInserted = logInserted < 0 ? 0 : logInserted;
                        logger.LogInformation($"Staging rows inserted into target table {logInserted}", logInserted);
                    }
                    catch (SqlException ex)
                    {
                        logErrorInsert = ex.Message;
                    }
                }
                else
                {
                    logInserted = 0;
                }
            }
        }
    }

    internal static void ProcessBatchedWithStagingJoin(bool skipUpdateExisting, SyncOutput trgShm, string trgDatabase,
string trgSchema, string trgObject, string joinExp, bool skipInsertNew, string stagingTableName,
string outerJoinExp, string stgSchema, SqlFlowParam sqlFlowParam, RealTimeLogger logger,
SqlConnection trgSqlCon, int bulkLoadTimeoutInSek, bool truncateTrg, ref string logUpdateCmd, ref long logUpdated,
ref string logErrorUpdate, ref string logInsertCmd, ref long logInserted, ref string logErrorInsert, int batchSize)
    {
        string srcDS4Update = $@"[{trgDatabase}].[{stgSchema}].[{stagingTableName}]";

        string joinExp4UpdateKey = string.Join(" AND ", trgShm.keyColumnsQuoted.Split(',').Select(k => $"src.{k.Trim()} = UK.{k.Trim()}"));
        string joinExp4InsertKey = string.Join(" AND ", trgShm.keyColumnsQuoted.Split(',').Select(k => $"src.{k.Trim()} = IK.{k.Trim()}"));
        string keyColumnsQuotedWithSrc = string.Join(", ", trgShm.keyColumnsQuoted.Split(',').Select(k => $"src.{k.Trim()}"));

        using (logger.TrackOperation("BatchedUpdateWithStagingJoin"))
        {
            if (!skipUpdateExisting)
            {
                string srcChkSum = trgShm.CheckSumColumnsSrc.Split(',').Length > 1 ? $"CONCAT({trgShm.CheckSumColumnsSrc})" : $"CAST({trgShm.CheckSumColumnsSrc} AS VARCHAR(4000))";
                string trgChkSum = trgShm.CheckSumColumnsTrg.Split(',').Length > 1 ? $"CONCAT({trgShm.CheckSumColumnsTrg})" : $"CAST({trgShm.CheckSumColumnsTrg} AS VARCHAR(4000))";

                string updateStgTrgCmd = $@"
            SET NOCOUNT ON;
            DECLARE @BatchSize int = {batchSize.ToString()};
            IF OBJECT_ID('tempdb..#UpdateKeys') IS NOT NULL DROP TABLE #UpdateKeys;
            SELECT {keyColumnsQuotedWithSrc}, 
                ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS RowNum
            INTO #UpdateKeys
            FROM {srcDS4Update} src
            INNER JOIN [{trgDatabase}].[{trgSchema}].[{trgObject}] trg ON {joinExp};
            
            CREATE CLUSTERED INDEX IX_UpdateKeys_RowNum ON #UpdateKeys(RowNum);
            CREATE STATISTICS UpdateKeys_Stats ON #UpdateKeys({trgShm.keyColumnsQuoted});

            DECLARE @UpdatedRows int = 0;
            DECLARE @StartRowNum INT = 1;
            DECLARE @EndRowNum INT = @BatchSize;
            DECLARE @TotalRows INT;

            SELECT @TotalRows = COUNT(*) FROM #UpdateKeys;

            WHILE @StartRowNum <= @TotalRows
            BEGIN
                UPDATE TRG 
                SET {trgShm.UpdateColumnsSrcTrg}
                FROM {srcDS4Update} src
                INNER JOIN #UpdateKeys uk ON {joinExp4UpdateKey}  
                INNER JOIN [{trgDatabase}].[{trgSchema}].[{trgObject}] trg ON {joinExp}
                WHERE {(trgShm.CheckSumColumnsSrc.Length > 2 ? $"HASHBYTES('SHA2_512', {srcChkSum}) <> HASHBYTES('SHA2_512', {trgChkSum}) AND" : "")}
                    uk.RowNum BETWEEN @StartRowNum AND @EndRowNum;
                SELECT @UpdatedRows = @UpdatedRows + @@Rowcount    


                SET @StartRowNum = @EndRowNum + 1;
                SET @EndRowNum = @StartRowNum + @BatchSize - 1;
                IF @EndRowNum > @TotalRows SET @EndRowNum = @TotalRows;
            END
            
             DROP TABLE #UpdateKeys;
            SELECT @UpdatedRows AS UpdatedRows;";

                logUpdateCmd = updateStgTrgCmd;
                
                logger.LogCodeBlock("updateStgTrgCmd", updateStgTrgCmd);

                var updCmd = new ExecNonQuery(trgSqlCon, updateStgTrgCmd, bulkLoadTimeoutInSek);
                try
                {
                    if (truncateTrg == false) //There are no rows to update if target is truncated trgVerssioning == false &&
                    {
                        using (var cmd = new SqlCommand(updateStgTrgCmd, trgSqlCon) { CommandTimeout = bulkLoadTimeoutInSek })
                        {
                            //cmd.CommandType = CommandType.Text;
                            logUpdated = Convert.ToInt64(cmd.ExecuteScalar());
                        }

                        logUpdated = logUpdated < 0 ? 0 : logUpdated;
                    }
                    logger.LogInformation($"Staging rows updated with target table {logUpdated}");
                }
                catch (SqlException ex)
                {
                    logErrorUpdate = ex.Message;
                }
                
            }
            else
            {
                logger.LogInformation($"Update on existing staging rows skipped");
            }
        }

        using (logger.TrackOperation("BatchedInsertWithStagingJoin"))
        {
            if (!skipInsertNew)
            {
                string insertStgTrgCmd = $@"
        DECLARE @BatchSize int = {batchSize.ToString()};
        IF OBJECT_ID('tempdb..#InsertKeys') IS NOT NULL DROP TABLE #InsertKeys;
        SET NOCOUNT ON;
        SELECT {keyColumnsQuotedWithSrc},
            ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS RowNum  
        INTO #InsertKeys
        FROM {srcDS4Update} src
        LEFT OUTER JOIN [{trgDatabase}].[{trgSchema}].[{trgObject}] trg ON {joinExp}
        WHERE {outerJoinExp} IS NULL;
        
        CREATE CLUSTERED INDEX IX_InsertKeys_RowNum ON #InsertKeys(RowNum);
        CREATE STATISTICS InsertKeys_Stats ON #InsertKeys({trgShm.keyColumnsQuoted});        
        
        DECLARE @InsertedRows int = 0;
        DECLARE @StartRowNum INT = 1;
        DECLARE @EndRowNum INT = @BatchSize;
        DECLARE @TotalRows INT;

        SELECT @TotalRows = COUNT(*) FROM #InsertKeys;

        WHILE @StartRowNum <= @TotalRows
        BEGIN
            INSERT INTO [{trgDatabase}].[{trgSchema}].[{trgObject}] ({trgShm.TrgColumns})
            SELECT {(trgShm.ImageDataTypeFound ? "" : "DISTINCT")} {trgShm.TrgColumnsWithSrc}
            FROM {srcDS4Update} src  
            INNER JOIN #InsertKeys ik ON {joinExp4InsertKey}
            LEFT OUTER JOIN [{trgDatabase}].[{trgSchema}].[{trgObject}] trg ON {joinExp}
            WHERE {outerJoinExp} IS NULL
                AND ik.RowNum BETWEEN @StartRowNum AND @EndRowNum;
             select @InsertedRows = @InsertedRows + @@Rowcount    
            SET @StartRowNum = @EndRowNum + 1;
            SET @EndRowNum = @StartRowNum + @BatchSize - 1;
            IF @EndRowNum > @TotalRows SET @EndRowNum = @TotalRows;
        END
        
         DROP TABLE #InsertKeys;
        SELECT @InsertedRows AS InsertedRows;";

                logInsertCmd = insertStgTrgCmd;
                logger.LogCodeBlock("insertStgTrgCmd:", insertStgTrgCmd);

                //if there are no new files
                if (insertStgTrgCmd.Length > 0)
                {
                    try
                    {
                        using (var cmd = new SqlCommand(insertStgTrgCmd, trgSqlCon) { CommandTimeout = bulkLoadTimeoutInSek })
                        {
                            //cmd.CommandType = CommandType.Text;
                            logInserted = Convert.ToInt64(cmd.ExecuteScalar());
                        }

                        logInserted = logInserted < 0 ? 0 : logInserted;
                    }
                    catch (SqlException ex)
                    {
                        logErrorInsert = ex.Message;
                    }
                }
                else
                {
                    logInserted = 0;
                }

                logger.LogInformation($"Staging rows inserted into target table {logInserted}");
            }
            else
            {
                logger.LogInformation($"Insert of new staging rows skipped");
            }
        }
    }
}