SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO




/*
  ##################################################################################################################################################
  -- Name				:   [flw].[GetGoogleApiKey]
  -- Date				:   2023.09.29
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   
  -- Summary			:	

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2023.09.29		Initial
  ##################################################################################################################################################
*/

CREATE PROCEDURE [flw].[GetApiKey]
  
AS
BEGIN

    SELECT [ApiKeyID], [ServiceType], [ApiKeyAlias], [AccessKey], [SecretKey], [ServicePrincipalAlias], [KeyVaultSecretName]
	FROM [flw].[SysAPIKey]
	--WHERE ServiceType = 'Google'
END;
GO
