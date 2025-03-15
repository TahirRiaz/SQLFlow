SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO


/*
  ##################################################################################################################################################
  -- Name				:   [flw].[GetFlowSchemaScript]
  -- Date				:   2020.11.06
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   The user-defined function "GetFlowSchemaScript" returns the SQL script that creates the necessary schemas in a given target database for a specified Flow ID.
  -- Summary			:	The function returns a single nvarchar(max) value, which is a SQL script that creates the required schemas in the target database for the specified flow. 
							
							The function checks if the specified schemas already exist in the target database before creating them. 
							If the schema does not exist, the function creates it using the CREATE SCHEMA statement. 
							If the SupportsCrossDBRef function returns 0, the function assumes that cross-database references are not supported and sets the VaultDB value to the trgDatabase value.
  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2020.11.06		Initial
  ##################################################################################################################################################
*/
CREATE FUNCTION [flw].[GetFlowSchemaScript]
(
    -- Add the parameters for the function here
    @flowID INT,
    @trgDatabase NVARCHAR(255),
    @trgSchema NVARCHAR(255),
    @Init BIT,
    @FirstExec BIT
)
RETURNS NVARCHAR(MAX)
AS
BEGIN
    -- Declare the return variable here

    DECLARE @Schema00Flow VARCHAR(255),
            @Schema01Raw VARCHAR(255),
            @Schema02Temp VARCHAR(255),
            @Schema03Vault VARCHAR(255),
            @Schema04Trusted VARCHAR(255),
            @Schema05Detokenize VARCHAR(255),
            @Schema06Version VARCHAR(255),
            @Schema07Pre VARCHAR(255),
			@Schema08Static  VARCHAR(255),
			@Schema09Export  VARCHAR(255),
			@Schema10EDW  VARCHAR(255),
			@Schema11DataMart  VARCHAR(255),
			@Schema12Fact  VARCHAR(255),
			@Schema13Skey VARCHAR(255),
			@Schema14Mkey VARCHAR(255),
            @VaultDB VARCHAR(255),
            @GeneralTimeoutInSek INT,
            @TokenCount INT;

    SELECT @Schema01Raw = Schema01Raw,
           @Schema02Temp = Schema02Temp,
           @Schema03Vault = Schema03Vault,
           @Schema04Trusted = Schema04Trusted,
           @Schema05Detokenize = Schema05Detokenize,
           @Schema06Version = Schema06Version,
           @Schema07Pre = Schema07Pre,
		   @Schema08Static = Schema08Static,
		   @Schema09Export = Schema09Export,
		   @Schema10EDW = Schema10EDW,
		   @Schema11DataMart  = Schema11DataMart,
		   @Schema12Fact  = Schema12Fact,
		   @Schema13Skey = Schema13Skey,
		   @Schema14Mkey = Schema14Mkey,
           @VaultDB = VaultDB,
           @GeneralTimeoutInSek = GeneralTimeoutInSek
    FROM flw.GetCFGParam('');

    DECLARE @cmdSchema NVARCHAR(MAX) = N'',
            @SchemaRes NVARCHAR(MAX);


    SELECT @TokenCount = COUNT(*)
    FROM flw.Ingestion AS I
        INNER JOIN flw.IngestionTokenize AS t
            ON I.FlowID = t.FlowID;


    --Azure does not support Cross Database Scripting. Vault must be the same as target database
    IF ([flw].[SupportsCrossDBRef](@FlowID) = 0)
    BEGIN
        SET @VaultDB = @trgDatabase;
    END;


    IF (@Init = 1)
    BEGIN
        --Lets Ensure that Target Schema Exsists including staging.
        --SET @cmdSchema
        --    = @cmdSchema + N'IF NOT EXISTS (SELECT * FROM [' + [flw].[RemBrackets](@trgDatabase)
        --      + N'].sys.schemas WHERE name = N''' + @Schema00Flow + N''') exec ['
        --      + [flw].[RemBrackets](@trgDatabase) + N'].sys.sp_executesql @stmt = N''CREATE SCHEMA ['
        --      + REPLACE(REPLACE(@Schema00Flow, '[', ''), ']', '') + N'] AUTHORIZATION [DBO]''' + CHAR(10) + CHAR(13);

        SET @cmdSchema
            = @cmdSchema + N'IF NOT EXISTS (SELECT * FROM [' + [flw].[RemBrackets](@trgDatabase)
              + N'].sys.schemas WHERE name = N''' + @Schema01Raw + N''') exec [' + [flw].[RemBrackets](@trgDatabase)
              + N'].sys.sp_executesql @stmt = N''CREATE SCHEMA [' + REPLACE(REPLACE(@Schema01Raw, '[', ''), ']', '')
              + N'] AUTHORIZATION [DBO]''' + CHAR(10) + CHAR(13);

        SET @cmdSchema
            = @cmdSchema + N'IF NOT EXISTS (SELECT * FROM [' + [flw].[RemBrackets](@trgDatabase)
              + N'].sys.schemas WHERE name = N''' + @Schema02Temp + N''') exec [' + [flw].[RemBrackets](@trgDatabase)
              + N'].sys.sp_executesql @stmt = N''CREATE SCHEMA [' + [flw].[RemBrackets](@Schema02Temp)
              + N'] AUTHORIZATION [DBO]''' + CHAR(10) + CHAR(13);

        IF (LEN(@VaultDB) > 0 AND @TokenCount > 0)
        BEGIN
            SET @cmdSchema
                = @cmdSchema + N'IF NOT EXISTS (SELECT * FROM [' + [flw].[RemBrackets](@VaultDB)
                  + N'].sys.schemas WHERE name = N''' + @Schema02Temp + N''') exec [' + [flw].[RemBrackets](@VaultDB)
                  + N'].sys.sp_executesql @stmt = N''CREATE SCHEMA [' + [flw].[RemBrackets](@Schema02Temp)
                  + N'] AUTHORIZATION [DBO]''' + CHAR(10) + CHAR(13);

            SET @cmdSchema
                = @cmdSchema + N'IF NOT EXISTS (SELECT * FROM [' + [flw].[RemBrackets](@VaultDB)
                  + N'].sys.schemas WHERE name = N''' + @Schema03Vault + N''') exec [' + [flw].[RemBrackets](@VaultDB)
                  + N'].sys.sp_executesql @stmt = N''CREATE SCHEMA [' + [flw].[RemBrackets](@Schema03Vault)
                  + N'] AUTHORIZATION [DBO]''' + CHAR(10) + CHAR(13);

            SET @cmdSchema
                = @cmdSchema + N'IF NOT EXISTS (SELECT * FROM [' + [flw].[RemBrackets](@VaultDB)
                  + N'].sys.schemas WHERE name = N''' + @Schema05Detokenize + N''') exec ['
                  + [flw].[RemBrackets](@VaultDB) + N'].sys.sp_executesql @stmt = N''CREATE SCHEMA ['
                  + [flw].[RemBrackets](@Schema05Detokenize) + N'] AUTHORIZATION [DBO]''' + CHAR(10) + CHAR(13);

            SET @cmdSchema
                = @cmdSchema + N'IF NOT EXISTS (SELECT * FROM [' + [flw].[RemBrackets](@VaultDB)
                  + N'].sys.schemas WHERE name = N''' + @Schema06Version + N''') exec ['
                  + [flw].[RemBrackets](@VaultDB) + N'].sys.sp_executesql @stmt = N''CREATE SCHEMA ['
                  + [flw].[RemBrackets](@Schema06Version) + N'] AUTHORIZATION [DBO]''' + CHAR(10) + CHAR(13);

        END;
        ELSE
        BEGIN
            SET @cmdSchema
                = @cmdSchema + N'IF NOT EXISTS (SELECT * FROM [' + [flw].[RemBrackets](@trgDatabase)
                  + N'].sys.schemas WHERE name = N''' + @Schema02Temp + N''') exec ['
                  + [flw].[RemBrackets](@trgDatabase) + N'].sys.sp_executesql @stmt = N''CREATE SCHEMA ['
                  + [flw].[RemBrackets](@Schema02Temp) + N'] AUTHORIZATION [DBO]''' + CHAR(10) + CHAR(13);

            SET @cmdSchema
                = @cmdSchema + N'IF NOT EXISTS (SELECT * FROM [' + [flw].[RemBrackets](@trgDatabase)
                  + N'].sys.schemas WHERE name = N''' + @Schema03Vault + N''') exec ['
                  + [flw].[RemBrackets](@trgDatabase) + N'].sys.sp_executesql @stmt = N''CREATE SCHEMA ['
                  + [flw].[RemBrackets](@Schema03Vault) + N'] AUTHORIZATION [DBO]''' + CHAR(10) + CHAR(13);

            SET @cmdSchema
                = @cmdSchema + N'IF NOT EXISTS (SELECT * FROM [' + [flw].[RemBrackets](@trgDatabase)
                  + N'].sys.schemas WHERE name = N''' + @Schema05Detokenize + N''') exec ['
                  + [flw].[RemBrackets](@trgDatabase) + N'].sys.sp_executesql @stmt = N''CREATE SCHEMA ['
                  + [flw].[RemBrackets](@Schema05Detokenize) + N'] AUTHORIZATION [DBO]''' + CHAR(10) + CHAR(13);

        END;

        SET @cmdSchema
            = @cmdSchema + N'IF NOT EXISTS (SELECT * FROM [' + [flw].[RemBrackets](@trgDatabase)
              + N'].sys.schemas WHERE name = N''' + @Schema04Trusted + N''') exec ['
              + [flw].[RemBrackets](@trgDatabase) + N'].sys.sp_executesql @stmt = N''CREATE SCHEMA ['
              + [flw].[RemBrackets](@Schema04Trusted) + N'] AUTHORIZATION [DBO]''' + CHAR(10) + CHAR(13);

        SET @cmdSchema
            = @cmdSchema + N'IF NOT EXISTS (SELECT * FROM [' + [flw].[RemBrackets](@trgDatabase)
              + N'].sys.schemas WHERE name = N''' + @Schema06Version + N''') exec ['
              + [flw].[RemBrackets](@trgDatabase) + N'].sys.sp_executesql @stmt = N''CREATE SCHEMA ['
              + [flw].[RemBrackets](@Schema06Version) + N'] AUTHORIZATION [DBO]''' + CHAR(10) + CHAR(13);

        SET @cmdSchema
            = @cmdSchema + N'IF NOT EXISTS (SELECT * FROM [' + [flw].[RemBrackets](@trgDatabase)
              + N'].sys.schemas WHERE name = N''' + @Schema07Pre + N''') exec [' + [flw].[RemBrackets](@trgDatabase)
              + N'].sys.sp_executesql @stmt = N''CREATE SCHEMA [' + [flw].[RemBrackets](@Schema07Pre)
              + N'] AUTHORIZATION [DBO]''' + CHAR(10) + CHAR(13);

        SET @cmdSchema
            = @cmdSchema + N'IF NOT EXISTS (SELECT * FROM [' + [flw].[RemBrackets](@trgDatabase)
              + N'].sys.schemas WHERE name = N''' + @Schema08Static + N''') exec [' + [flw].[RemBrackets](@trgDatabase)
              + N'].sys.sp_executesql @stmt = N''CREATE SCHEMA [' + [flw].[RemBrackets](@Schema08Static)
              + N'] AUTHORIZATION [DBO]''' + CHAR(10) + CHAR(13);

        SET @cmdSchema
            = @cmdSchema + N'IF NOT EXISTS (SELECT * FROM [' + [flw].[RemBrackets](@trgDatabase)
              + N'].sys.schemas WHERE name = N''' + @Schema09Export + N''') exec [' + [flw].[RemBrackets](@trgDatabase)
              + N'].sys.sp_executesql @stmt = N''CREATE SCHEMA [' + [flw].[RemBrackets](@Schema09Export)
              + N'] AUTHORIZATION [DBO]''' + CHAR(10) + CHAR(13);

         SET @cmdSchema
            = @cmdSchema + N'IF NOT EXISTS (SELECT * FROM [' + [flw].[RemBrackets](@trgDatabase)
              + N'].sys.schemas WHERE name = N''' + @Schema10EDW + N''') exec [' + [flw].[RemBrackets](@trgDatabase)
              + N'].sys.sp_executesql @stmt = N''CREATE SCHEMA [' + [flw].[RemBrackets](@Schema10EDW)
              + N'] AUTHORIZATION [DBO]''' + CHAR(10) + CHAR(13);

        SET @cmdSchema
            = @cmdSchema + N'IF NOT EXISTS (SELECT * FROM [' + [flw].[RemBrackets](@trgDatabase)
              + N'].sys.schemas WHERE name = N''' + @Schema11DataMart + N''') exec [' + [flw].[RemBrackets](@trgDatabase)
              + N'].sys.sp_executesql @stmt = N''CREATE SCHEMA [' + [flw].[RemBrackets](@Schema11DataMart)
              + N'] AUTHORIZATION [DBO]''' + CHAR(10) + CHAR(13);

        SET @cmdSchema
            = @cmdSchema + N'IF NOT EXISTS (SELECT * FROM [' + [flw].[RemBrackets](@trgDatabase)
              + N'].sys.schemas WHERE name = N''' + @Schema12Fact + N''') exec [' + [flw].[RemBrackets](@trgDatabase)
              + N'].sys.sp_executesql @stmt = N''CREATE SCHEMA [' + [flw].[RemBrackets](@Schema12Fact)
              + N'] AUTHORIZATION [DBO]''' + CHAR(10) + CHAR(13);

        SET @cmdSchema
            = @cmdSchema + N'IF NOT EXISTS (SELECT * FROM [' + [flw].[RemBrackets](@trgDatabase)
              + N'].sys.schemas WHERE name = N''' + @Schema13Skey + N''') exec [' + [flw].[RemBrackets](@trgDatabase)
              + N'].sys.sp_executesql @stmt = N''CREATE SCHEMA [' + [flw].[RemBrackets](@Schema13Skey)
              + N'] AUTHORIZATION [DBO]''' + CHAR(10) + CHAR(13);


	   SET @cmdSchema
            = @cmdSchema + N'IF NOT EXISTS (SELECT * FROM [' + [flw].[RemBrackets](@trgDatabase)
              + N'].sys.schemas WHERE name = N''' + @Schema14Mkey + N''') exec [' + [flw].[RemBrackets](@trgDatabase)
              + N'].sys.sp_executesql @stmt = N''CREATE SCHEMA [' + [flw].[RemBrackets](@Schema14Mkey)
              + N'] AUTHORIZATION [DBO]''' + CHAR(10) + CHAR(13);

    END;

    --Dupe target schema against CFG
    --IF (@FirstExec = 1)
    --BEGIN
    IF NOT EXISTS
    (
        SELECT *
        FROM flw.SysCFG
        WHERE ParamName LIKE 'Schema%'
              AND ParamValue = @trgSchema
    )
    BEGIN
        SET @cmdSchema
            = @cmdSchema + N'IF NOT EXISTS (SELECT * FROM [' + [flw].[RemBrackets](@trgDatabase)
              + N'].sys.schemas WHERE name = N''' + [flw].[RemBrackets](@trgSchema) + N''') exec ['
              + [flw].[RemBrackets](@trgDatabase) + N'].sys.sp_executesql @stmt = N''CREATE SCHEMA ['
              + REPLACE(REPLACE(@trgSchema, '[', ''), ']', '') + N'] AUTHORIZATION [DBO]''' + CHAR(10) + CHAR(13);


        SET @cmdSchema
            = @cmdSchema + N'IF NOT EXISTS (SELECT * FROM [' + [flw].[RemBrackets](@trgDatabase)
              + N'].sys.schemas WHERE name = N''' + @Schema07Pre + N''') exec [' + [flw].[RemBrackets](@trgDatabase)
              + N'].sys.sp_executesql @stmt = N''CREATE SCHEMA [' + [flw].[RemBrackets](@Schema07Pre)
              + N'] AUTHORIZATION [DBO]''' + CHAR(10) + CHAR(13);
    END;
    --END;

    RETURN @cmdSchema;

END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   The user-defined function "GetFlowSchemaScript" returns the SQL script that creates the necessary schemas in a given target database for a specified Flow ID.
  -- Summary			:	The function returns a single nvarchar(max) value, which is a SQL script that creates the required schemas in the target database for the specified flow. 
							
							The function checks if the specified schemas already exist in the target database before creating them. 
							If the schema does not exist, the function creates it using the CREATE SCHEMA statement. 
							If the SupportsCrossDBRef function returns 0, the function assumes that cross-database references are not supported and sets the 
							VaultDB value to the trgDatabase value.', 'SCHEMA', N'flw', 'FUNCTION', N'GetFlowSchemaScript', NULL, NULL
GO
