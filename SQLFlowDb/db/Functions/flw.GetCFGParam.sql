SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO

/*
  ##################################################################################################################################################
  -- Name				:   [flw].[GetCFGParam]
  -- Date				:   2021.04.26
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   The purpose of this user-defined function named "flw.GetCFGParam" is to fetch configuration parameters related 
						    to a specific Flow ID from the "flw.SysCFG" table and use them to generate a set of object names  
  -- Summary			:	This function returns a table that contains the following schema details for the provided Flow ID: Target database name, 
							Vault database name, source table name, different schema names, version details, and timeouts. 
							
							The function also creates object names by concatenating different schema details with the provided object name.

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2021.04.26		Initial
  ##################################################################################################################################################
*/



CREATE FUNCTION [flw].[GetCFGParam]
(
    @FlowID INT
)
RETURNS @ObjNames TABLE
(
    recId INT IDENTITY(1, 1),
    TargetDB NVARCHAR(255) NULL,
    VaultDB NVARCHAR(255) NULL,
    SrcTable NVARCHAR(255) NULL,
    [Flow] NVARCHAR(255) NULL,
    [Raw] NVARCHAR(255) NULL,
    [Trusted] NVARCHAR(255) NULL,
    [TrustedVersion] NVARCHAR(255) NULL,
    [Temp] NVARCHAR(255) NULL,
    [Vault] NVARCHAR(255) NULL,
    [VaultVersion] NVARCHAR(255) NULL,
    [Detokenize] NVARCHAR(255) NULL,
    [Pre] NVARCHAR(255) NULL,
    GeneralTimeoutInSek NVARCHAR(255) NULL,
    [Schema01Raw] NVARCHAR(255) NULL,
    [Schema02Temp] NVARCHAR(255) NULL,
    [Schema03Vault] NVARCHAR(255) NULL,
    [Schema04Trusted] NVARCHAR(255) NULL,
    [Schema05Detokenize] NVARCHAR(255) NULL,
    [Schema06Version] NVARCHAR(255) NULL,
    Schema07Pre NVARCHAR(255) NULL,
    Schema08Static NVARCHAR(255) NULL,
    Schema09Export NVARCHAR(255) NULL,
    Schema10EDW NVARCHAR(255) NULL,
    Schema11DataMart NVARCHAR(255) NULL,
    Schema12Fact NVARCHAR(255) NULL,
    Schema13Skey NVARCHAR(255) NULL,
	Schema14Mkey NVARCHAR(255) NULL
)
AS
BEGIN

    --FETCH Target DATABASE Name
    --FETCH Target DATABASE Name
    DECLARE @trgDbName NVARCHAR(255),
            @vltDbName NVARCHAR(255),
            @Source NVARCHAR(255),
            @flow NVARCHAR(255),
            @raw NVARCHAR(255),
            @tru NVARCHAR(255),
            @ver NVARCHAR(255),
            @tmp NVARCHAR(255),
            @vlt NVARCHAR(255),
            @sta NVARCHAR(255),
            @exp NVARCHAR(255),
            @edw NVARCHAR(255),
            @dm NVARCHAR(255),
            @fact NVARCHAR(255),
            @skey NVARCHAR(255),
			@mkey NVARCHAR(255),
            @pre NVARCHAR(255),
            @Detokenize NVARCHAR(255),
            @ObjectName NVARCHAR(255),
            @GeneralTimeoutInSek NVARCHAR(255);


    SELECT @flow = [Schema00Flow],
           @raw = [Schema01Raw],
           @tmp = [Schema02Temp],
           @vlt = [Schema03Vault],
           @tru = [Schema04Trusted],
           @Detokenize = [Schema05Detokenize],
           @ver = [Schema06Version],
           @pre = Schema07Pre,
           @sta = Schema08Static,
           @exp = Schema09Export,
           @edw = Schema10EDW,
           @dm = Schema11DataMart,
           @fact = Schema12Fact,
           @skey = Schema13Skey,
		   @mkey = Schema14Mkey,
           @vltDbName = [VaultDB],
           @trgDbName = [flw].[GetDefaultTrgDB] (),
           @GeneralTimeoutInSek = [GeneralTimeoutInSek]
    FROM
    (SELECT [ParamName], [ParamValue] FROM flw.SysCFG) p
    PIVOT
    (
        MAX([ParamValue])
        FOR [ParamName] IN ([Schema00Flow], [Schema01Raw], [Schema02Temp], [Schema03Vault], [Schema04Trusted],
                            [Schema05Detokenize], [Schema06Version], [Schema07Pre], [Schema08Static], [Schema09Export],
                            [Schema10EDW], [Schema11DataMart], [Schema12Fact], [Schema13Skey], Schema14Mkey, [VaultDB],
                             [GeneralTimeoutInSek]
                           )
    ) AS pvt;

    SET @Source =
    (
        SELECT srcDBSchTbl FROM [flw].[Ingestion] WHERE flowID = @FlowID
    );

    SET @ObjectName =
    (
        SELECT [trgDBSchObj] FROM [flw].[FlowDS] WHERE FlowID = @FlowID
    );



    INSERT INTO @ObjNames
    (
        TargetDB,
        VaultDB,
        SrcTable,
        [Flow],
        [Raw],
        [Trusted],
        [TrustedVersion],
        [Temp],
        [Vault],
        [VaultVersion],
        [Detokenize],
        [Pre],
        GeneralTimeoutInSek,
        [Schema01Raw],
        [Schema02Temp],
        [Schema03Vault],
        [Schema04Trusted],
        [Schema05Detokenize],
        [Schema06Version],
        [Schema07Pre],
        Schema08Static,
        Schema09Export,
        Schema10EDW,
        Schema11DataMart,
        Schema12Fact,
        Schema13Skey,
		Schema14Mkey
    )
    SELECT '[' + PARSENAME(@ObjectName, 3) + N']' AS TargetDB,
           '[' + @vltDbName + N']' AS VaultDB,
           @Source AS SrcTable,
           @flow,
           '[' + PARSENAME(@ObjectName, 3) + N'].[' + @raw + N'].[' + PARSENAME(@ObjectName, 1) + N']' AS [RAW],
           '[' + PARSENAME(@ObjectName, 3) + N'].[' + @tru + N'].[' + PARSENAME(@ObjectName, 1) + N']' AS [Trusted],
           '[' + PARSENAME(@ObjectName, 3) + N'].[' + @ver + N'].[' + PARSENAME(@ObjectName, 1) + N']' AS [TrustedVersion],
           '[' + @vltDbName + N'].[' + @tmp + N'].[' + PARSENAME(@ObjectName, 1) + N']' AS [Temp],
           '[' + @vltDbName + N'].[' + @vlt + N'].[' + PARSENAME(@ObjectName, 1) + N']' AS [Vault],
           '[' + @vltDbName + N'].[' + @ver + N'].[' + PARSENAME(@ObjectName, 1) + N']' AS [VaultVersion],
           '[' + @vltDbName + N'].[' + @Detokenize + N'].[' + PARSENAME(@ObjectName, 1) + N']' AS [Vault],
           '[' + PARSENAME(@ObjectName, 3) + N'].[' + @pre + N'].[' + PARSENAME(@ObjectName, 1) + N']' AS [RAW],
           @GeneralTimeoutInSek,
           @raw,
           @tmp,
           @vlt,
           @tru,
           @Detokenize,
           @ver,
           @pre,
           @sta,
           @exp,
           @edw,
           @dm,
           @fact,
           @skey,
		   @mkey

    --SELECT @ObjNames;

    RETURN;
END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   The purpose of this user-defined function named "flw.GetCFGParam" is to fetch configuration parameters related 
						    to a specific Flow ID from the "flw.SysCFG" table and use them to generate a set of object names  
  -- Summary			:	This function returns a table that contains the following schema details for the provided Flow ID: Target database name, 
							Vault database name, source table name, different schema names, version details, and timeouts. 
							
							The function also creates object names by concatenating different schema details with the provided object name.', 'SCHEMA', N'flw', 'FUNCTION', N'GetCFGParam', NULL, NULL
GO
