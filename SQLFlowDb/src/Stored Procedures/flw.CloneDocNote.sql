SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
CREATE PROCEDURE [flw].[CloneDocNote] @DocNoteId INT = 0
AS
BEGIN;

    DECLARE @Type NVARCHAR(255) = N'',
            @Dupe INT = 0;

    SELECT @Dupe = ISNULL(COUNT(*), 0)
    FROM [flw].[SysDocNote]
    WHERE DocNoteID = @DocNoteId
          AND ObjectName IN
              (
                  SELECT ObjectName FROM [flw].[GetConfigTableColumns]()
              );

    IF (@Dupe >= 1)
    BEGIN
        SET @Type = N'Config';
    END;

    SELECT @Dupe = ISNULL(COUNT(*), 0)
    FROM [flw].[SysDocNote]
    WHERE DocNoteID = @DocNoteId
          AND ObjectName IN
              (
                  SELECT ObjectName FROM [flw].[GetInternalTableColumns]()
              );

    IF (@Dupe >= 1)
    BEGIN
        SET @Type = N'Internal';
    END;

    SELECT @Dupe = ISNULL(COUNT(*), 0)
    FROM [flw].[SysDocNote]
    WHERE DocNoteID = @DocNoteId
          AND ObjectName IN
              (
                  SELECT ObjectName FROM [flw].[GetStaticTableColumns]()
              );

    IF (@Dupe >= 1)
    BEGIN
        SET @Type = N'Static';
    END;
    PRINT @Type;

    IF (@Type = 'Config')
    BEGIN
        IF OBJECT_ID('tempdb..#TempNote1') IS NOT NULL
        BEGIN
            DROP TABLE #TempNote1;
        END;
        --GetConfigTableColumns
        ;
        WITH base
        AS (SELECT DocNoteID DocNoteID,
                   [ObjectName],
                   PARSENAME([ObjectName], 1) AS [ColName],
                   [Title],
                   [Description]
            FROM [flw].[SysDocNote]
            WHERE DocNoteID = @DocNoteId),
             GetDupeColumns
        AS (SELECT DocNoteID,
                   sd.ObjectName,
                   base.[Title],
                   base.[Description]
            FROM [flw].[SysDoc] sd
                INNER JOIN base
                    ON base.[ColName] = PARSENAME(sd.[ObjectName], 1)
            WHERE sd.[ObjectName] IN
                  (
                      SELECT ObjectName FROM [flw].[GetConfigTableColumns]()
                  ))
        SELECT src.[ObjectName],
               src.[Title],
               src.[Description]
        INTO #TempNote1
        FROM GetDupeColumns src;

        INSERT INTO [flw].[SysDocNote]
        (
            [ObjectName],
            [Title],
            [Description]
        )
        SELECT src.[ObjectName],
               src.[Title],
               src.[Description]
        FROM #TempNote1 src
            LEFT OUTER JOIN [flw].[SysDocNote] trg
                ON src.ObjectName = trg.ObjectName
                   AND src.[Title] = trg.[Title]
        WHERE trg.ObjectName IS NULL;

        UPDATE trg
        SET trg.Description = src.Description,
            trg.Title = src.Title
        FROM [flw].[SysDocNote] trg
            INNER JOIN #TempNote1 src
                ON trg.ObjectName = src.ObjectName
        WHERE trg.Description <> src.Description;

        --Reset Description
        UPDATE trg
        SET trg.[Description] = NULL
        FROM [flw].[SysDoc] trg
            INNER JOIN #TempNote1 base
                ON trg.[ObjectName] = base.ObjectName
        WHERE trg.ObjectType = 'Column';

    END;
    ELSE IF (@Type = 'Internal')
    BEGIN
        IF OBJECT_ID('tempdb..#TempNote2') IS NOT NULL
        BEGIN
            DROP TABLE #TempNote2;
        END;
        --GetInternalTableColumns
        ;
        WITH base
        AS (SELECT DocNoteID DocNoteID,
                   [ObjectName],
                   PARSENAME([ObjectName], 1) AS [ColName],
                   [Title],
                   [Description]
            FROM [flw].[SysDocNote]
            WHERE DocNoteID = @DocNoteId),
             GetDupeColumns
        AS (SELECT DocNoteID,
                   sd.ObjectName,
                   base.[Title],
                   base.[Description]
            FROM [flw].[SysDoc] sd
                INNER JOIN base
                    ON base.[ColName] = PARSENAME(sd.[ObjectName], 1)
            WHERE sd.[ObjectName] IN
                  (
                      SELECT ObjectName FROM [flw].[GetInternalTableColumns]()
                  ))
        SELECT src.[ObjectName],
               src.[Title],
               src.[Description]
        INTO #TempNote2
        FROM GetDupeColumns src;

        INSERT INTO [flw].[SysDocNote]
        (
            [ObjectName],
            [Title],
            [Description]
        )
        SELECT src.[ObjectName],
               src.[Title],
               src.[Description]
        FROM #TempNote2 src
            LEFT OUTER JOIN [flw].[SysDocNote] trg
                ON src.ObjectName = trg.ObjectName
                   AND src.[Title] = trg.[Title]
        WHERE trg.ObjectName IS NULL;

        UPDATE trg
        SET trg.Description = src.Description,
            trg.Title = src.Title
        FROM [flw].[SysDocNote] trg
            INNER JOIN #TempNote2 src
                ON trg.ObjectName = src.ObjectName
        WHERE trg.Description <> src.Description;

        --Reset Description
        UPDATE trg
        SET trg.[Description] = NULL
        FROM [flw].[SysDoc] trg
            INNER JOIN #TempNote2 base
                ON trg.[ObjectName] = base.ObjectName
        WHERE trg.ObjectType = 'Column';
    END;
    ELSE IF (@Type = 'Static')
    BEGIN
        IF OBJECT_ID('tempdb..#TempNote3') IS NOT NULL
        BEGIN
            DROP TABLE #TempNote3;
        END;

        --GetStaticTableColumns
        ;
        WITH base
        AS (SELECT DocNoteID DocNoteID,
                   [ObjectName],
                   PARSENAME([ObjectName], 1) AS [ColName],
                   [Title],
                   [Description]
            FROM [flw].[SysDocNote]
            WHERE DocNoteID = @DocNoteId),
             GetDupeColumns
        AS (SELECT DocNoteID,
                   sd.ObjectName,
                   base.[Title],
                   base.[Description]
            FROM [flw].[SysDoc] sd
                INNER JOIN base
                    ON base.[ColName] = PARSENAME(sd.[ObjectName], 1)
            WHERE sd.[ObjectName] IN
                  (
                      SELECT ObjectName FROM [flw].[GetStaticTableColumns]()
                  ))
        SELECT src.[ObjectName],
               src.[Title],
               src.[Description]
        INTO #TempNote3
        FROM GetDupeColumns src;

        INSERT INTO [flw].[SysDocNote]
        (
            [ObjectName],
            [Title],
            [Description]
        )
        SELECT src.[ObjectName],
               src.[Title],
               src.[Description]
        FROM #TempNote3 src
            LEFT OUTER JOIN [flw].[SysDocNote] trg
                ON src.ObjectName = trg.ObjectName
                   AND src.[Title] = trg.[Title]
        WHERE trg.ObjectName IS NULL;

        UPDATE trg
        SET trg.Description = src.Description,
            trg.Title = src.Title
        FROM [flw].[SysDocNote] trg
            INNER JOIN #TempNote3 src
                ON trg.ObjectName = src.ObjectName
        WHERE trg.Description <> src.Description;

        --Reset Description
        UPDATE trg
        SET trg.[Description] = NULL
        FROM [flw].[SysDoc] trg
            INNER JOIN #TempNote3 base
                ON trg.[ObjectName] = base.ObjectName
        WHERE trg.ObjectType = 'Column';
    END;


END;
GO
