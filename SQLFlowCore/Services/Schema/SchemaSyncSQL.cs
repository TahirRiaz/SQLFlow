using System;
using System.Collections.Generic;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Linq;
using SQLFlowCore.Engine.Utils.DataExt;
using SQLFlowCore.Args;
using SQLFlowCore.Common;

//using SQLFlowCore.DataExt;

//using System.Data;

namespace SQLFlowCore.Services.Schema
{
    internal class SchemaSyncSql
    {
        internal static event EventHandler<EventArgsSchema> GetSchemaScript;

        private readonly int _addConstraints;
        private readonly string _colCleanupSqlRegExp;
        private readonly int _commandTimeOutInSeconds;
        private readonly int _convUnicodeDt;

        private readonly string _keyColumns = "";
        private readonly string _dateColumn = "";
        private readonly string _incrementalColumns = "";
        private readonly string _identityColumn = "";
        private readonly string[] _ignoreColumns = new string[0];
        private readonly string _keyColumnsParsed = "";

        private readonly string _metaJoinColName = "";

        private readonly SqlConnection _srcSqlCon;
        private readonly SqlConnection _trgSqlCon;

        private bool _convertUnicodeDt;
        private string _srcFile = @"";
        private bool _syncSchema;
        private string _trgConString = @"";
        internal Dictionary<string, string> ColumnMappings = new();
        internal string CreateCmd = "";
        internal string DateColumnVerified = "";
        internal string IncrementalColumnsVerified = "";
        internal string IncrementalColumnsSrcVerified = "";

        internal string GenerateCreateCmd = "";
        internal string GenerateRenameCmd = "";
        private readonly List<string> _ingoreChecksumColumns = new();
        private List<string> _hashKeyCols = new();

        private readonly List<string> _invalidChecksumDataTypes = new();
        private string _srcWithHint = "readpast";
        private string _trgWithHint = "readpast";
        private bool _srcIsSynapse = false;
        private bool _trgIsSynapse = false;
        private bool _columnStoreIndexOnTrg = false;
        internal string KeyColumnsSrcVerified = "";

        internal string KeyColumnsVerified = "";
        internal string NewColumnsCmd = "";
        internal string RenameCmd = "";
        internal string RenamedTrgObject = "";
        internal bool SchemaChanged;
        internal string SrcCmd = "";
        internal bool SrcExists;
        private readonly int _srcIsStaging;
        internal bool StgExists = false;
        internal string TargetColumns = "";
        internal string SourceColumns = "";
        internal string TrgCmd = "";
        internal bool TrgExists;


        private readonly bool _trgIsStaging;
        internal string ValidChkSumColumns = "";
        internal string ValidColumns = "";
        internal string ValidSrcColumnsWithAlias = "";
        internal string ValidSrcColumnsWithCollateAndAlias = "";
        internal string ValidUpdateColumns = "";
        internal bool HashKeyColumnFound = false;

        internal SchemaSyncSql(SqlConnection trgSqlCon, SqlConnection srcSqlCon, string srcFile, string trgConString,
            string srcDatabase, string srcSchema, string srcObject, string trgDatabase, string trgSchema,
            string trgObject, bool syncSchema, bool cleanColumnName, bool convertUnicodeDt, bool createIndexes,
            string keyColumns, string dateColumn, int commandTimeOutInSeconds, string colCleanupSqlRegExp,
            string ignoreColumns,
            string tokenSchemaXml,
            string sysTblColSelectXml,
            string sysColSelectXml,
            string sysDataTypeSelectXml,
            string virtualSchemaXml,
            bool trgIsStaging,
            bool srcIsStaging,
            bool srcIsSynapse,
            bool trgIsSynapse,
            string incrementalColumns,
            string identityColumn,
            string tokenSchemaForCte,
            string sysTblColSelectForCte,
            string sysColSelectForCte,
            string sysDataTypeSelectForCte, string virtualSchemaForCte, string defTokenSchemaForCte, bool columnStoreIndexOnTrg,
             List<string> hashKeyCols
        )
        {
            _hashKeyCols = hashKeyCols;


            _invalidChecksumDataTypes.Add("xml");
            _invalidChecksumDataTypes.Add("geography");
            _invalidChecksumDataTypes.Add("varbinary");
            _invalidChecksumDataTypes.Add("hierarchyid");
            _ingoreChecksumColumns.Add("UpdatedDate_DW");
            _ingoreChecksumColumns.Add("DeletedDate_DW");
            _ingoreChecksumColumns.Add("InsertedDate_DW");
            _ingoreChecksumColumns.Add("RowStatus_DW");
            _ingoreChecksumColumns.Add("HashKey_DW");
            _ingoreChecksumColumns.Add("FileDate_DW");
            _ingoreChecksumColumns.Add("FileName_DW");
            _ingoreChecksumColumns.Add("FileRowDate_DW");
            _ingoreChecksumColumns.Add("FileSize_DW");
            _ingoreChecksumColumns.Add("FileLineNumber_DW");

            _columnStoreIndexOnTrg = columnStoreIndexOnTrg;
            //UpdatedDate_DW,DeletedDate_DW,InsertedDate_DW,RowStatus_DW,HashKey_DW,FileDate_DW,FileName_DW,FileRowDate_DW,FileSize_DW

            var defTokenSchemaXml =
                @"<TokenSchema><row TABLE_NAME="""" COLUMN_NAME="""" DataType="""" /></TokenSchema>"; //

            _ignoreColumns = ignoreColumns.Replace("'", "").Split(',');


            _srcIsSynapse = srcIsSynapse;
            _trgIsSynapse = trgIsSynapse;

            _srcWithHint = srcIsSynapse ? "nolock" : "readpast";
            _trgWithHint = trgIsSynapse || srcIsSynapse ? "nolock" : "readpast";

            _trgIsStaging = trgIsStaging;
            _srcIsStaging = srcIsStaging ? 1 : 0;

            _colCleanupSqlRegExp = colCleanupSqlRegExp;
            RenameCmd = "";
            var keyColMaxArray = keyColumns.Split(',');

            _addConstraints = createIndexes ? 1 : 0;
            _convUnicodeDt = convertUnicodeDt ? 1 : 0;


            _trgSqlCon = trgSqlCon;
            _srcSqlCon = srcSqlCon;

            if (keyColumns.Length > 0)
            {
                foreach (var t in keyColMaxArray)
                    _keyColumnsParsed += $",'{t.Trim()}'";

                _keyColumnsParsed = _keyColumnsParsed.Substring(1);
            }

            if (_keyColumnsParsed.Length == 0) _keyColumnsParsed = "''";

            var dateColumnsParsed = dateColumn.Length == 0 ? "''" : $"'{dateColumn.Trim()}'";

            _keyColumns = keyColumns;
            _dateColumn = dateColumn;
            _incrementalColumns = incrementalColumns;
            _identityColumn = identityColumn;
            _convertUnicodeDt = convertUnicodeDt;

            _metaJoinColName = cleanColumnName ? "COLUMN_NAME_CLEANED" : "COLUMN_NAME";
            _trgConString = trgConString;
            _srcFile = srcFile;
            SrcExists = false;
            TrgExists = false;
            _syncSchema = syncSchema;
            _commandTimeOutInSeconds = commandTimeOutInSeconds;

            if (trgIsSynapse || srcIsSynapse)
            {
                CreateCmd = BuildCreateCmdSyn(srcDatabase, srcSchema, srcObject, trgDatabase, trgSchema,
                    trgObject, createIndexes, _keyColumnsParsed, dateColumn, ignoreColumns, tokenSchemaForCte,
                    sysTblColSelectForCte, sysColSelectForCte, sysDataTypeSelectForCte, virtualSchemaForCte, _trgWithHint, trgIsSynapse, srcIsSynapse, incrementalColumns);
            }
            else
            {
                CreateCmd = BuildCreateCmd(srcDatabase, srcSchema, srcObject, trgDatabase, trgSchema,
                    trgObject, createIndexes, _keyColumnsParsed, dateColumn, ignoreColumns, tokenSchemaXml,
                    sysTblColSelectXml, sysColSelectXml, sysDataTypeSelectXml, virtualSchemaXml, _trgWithHint, incrementalColumns);
            }



            RenameCmd = ""; //BuildRenameCmd(trgDatabase, trgSchema, trgObject); Synapse Bug

            if (srcIsSynapse)
            {
                SrcCmd = BuildColCleanSyn(srcDatabase, srcSchema, srcObject, ignoreColumns, tokenSchemaForCte,
                    sysTblColSelectForCte, sysColSelectForCte, sysDataTypeSelectForCte, virtualSchemaForCte, _srcWithHint);
            }
            else
            {
                SrcCmd = BuildColClean(srcDatabase, srcSchema, srcObject, ignoreColumns, tokenSchemaXml,
                    sysTblColSelectXml, sysColSelectXml, sysDataTypeSelectXml, virtualSchemaXml, _srcWithHint);
            }

            SrcCmd = SrcCmd + $@" SELECT col.TABLE_CATALOG,col.TABLE_SCHEMA,col.TABLE_NAME,col.COLUMN_NAME,col.ORDINAL_POSITION,col.DATA_TYPE,IsNull(col.COLLATION_NAME,'') as COLLATION_NAME, IsNull(cl.COLUMN_NAME_CLEANED,cl.COLUMN_NAME) as COLUMN_NAME_CLEANED, 
    CASE WHEN cl.COLUMN_NAME_CLEANED in ({_keyColumnsParsed}) OR cl.COLUMN_NAME IN ({_keyColumnsParsed}) THEN 1 ELSE 0 END IsKeyColumn,
    CASE WHEN cl.COLUMN_NAME_CLEANED in ({dateColumnsParsed}) OR cl.COLUMN_NAME in ({dateColumnsParsed}) THEN 1 ELSE 0 END IsDateColumn,
    CASE WHEN cl.COLUMN_NAME_CLEANED in ({ignoreColumns}) OR cl.COLUMN_NAME in ({ignoreColumns}) THEN 1 ELSE 0 END IsIgnored,
    COALESCE(SysTblCol.SelectExp,SysCol.SelectExp,replace(replace(SysDataType.SelectExp,'@ColName', '[' + col.COLUMN_NAME +']' ),'@ColName', '[' + cl.COLUMN_NAME_CLEANED +']' ),'') as SelectExp,
    CASE WHEN cl.COLUMN_NAME_CLEANED in ({ignoreColumns}) OR cl.COLUMN_NAME in ({ignoreColumns}) THEN 
     '' ELSE 
    'ALTER TABLE [{trgDatabase}].[{trgSchema}].[{trgObject}] ADD ' + Quotename({"cl." + _metaJoinColName})  + ' '
    + CASE WHEN data_type in ('nvarchar','nchar','ntext') AND 1={_convUnicodeDt} THEN substring(data_type,2,len(data_type)) ELSE data_type END
    + CASE WHEN data_type in ('sql_variant','text','ntext','int','float','smallint','bigint','real','datetime','smalldatetime','tinyint', 'bit', 'datetime2','date','xml','hierarchyid','geography') THEN '' WHEN data_type in ('time') THEN '(' + Cast(Datetime_Precision AS VARCHAR) + ')' WHEN data_type in ('decimal', 'NUMERIC') THEN '(' + Cast(numeric_precision AS VARCHAR) + ', ' + Cast(numeric_scale AS VARCHAR) + ')' ELSE ISNULL( CASE WHEN DATA_TYPE IN ('XML')  THEN '' WHEN character_maximum_length = -1 THEN '(MAX)' ELSE '(' + CAST(character_maximum_length AS VARCHAR) + ')' END , '')  END + ' ' 
    -- can lead to issues null issues NOT NULL  + ' ' + (CASE WHEN IS_NULLABLE = 'No' THEN 'NOT ' ELSE '' END) + 'NULL ' 
    -- can lead to issues null issues + CASE WHEN cl.COLUMN_NAME_CLEANED in ({_keyColumnsParsed}) OR cl.COLUMN_NAME IN ({dateColumnsParsed}) OR kcu.COLUMN_NAME is not null THEN 'NOT ' ELSE '' END 
    +  'NULL ' --NotNullOnlyFor PK Columns
    -- + CASE WHEN col.COLUMN_DEFAULT IS NOT NULL AND col.COLUMN_DEFAULT NOT LIKE '%NEXT%' THEN 'DEFAULT '  + col.COLUMN_DEFAULT ELSE '' END  -- The source data will provide default values
    + ';' END as AddColumnCMD
    FROM [{srcDatabase}].INFORMATION_SCHEMA.COLUMNS col with({_srcWithHint}) 
    
    LEFT OUTER JOIN SysTblCol SysTblCol
    on SysTblCol.TABLE_NAME = col.TABLE_NAME
    AND SysTblCol.COLUMN_NAME = col.COLUMN_NAME 

    LEFT OUTER JOIN SysCol SysCol
    on SysCol.COLUMN_NAME = col.COLUMN_NAME 

    LEFT OUTER JOIN SysDataType SysDataType
    on SysDataType.DataType = col.Data_Type

    LEFT OUTER JOIN UniqueCleanedColName cl
    on cl.TABLE_SCHEMA = col.TABLE_SCHEMA 
    AND cl.TABLE_NAME = col.TABLE_NAME 
    AND cl.COLUMN_NAME = col.COLUMN_NAME 
    LEFT OUTER JOIN
	(
	SELECT ColU.TABLE_SCHEMA, ColU.Table_Name, ColU.Column_Name from 
    	[{srcDatabase}].INFORMATION_SCHEMA.TABLE_CONSTRAINTS Tab with({_srcWithHint}), 
    	[{srcDatabase}].INFORMATION_SCHEMA.KEY_COLUMN_USAGE  ColU with({_srcWithHint})
	WHERE 
		ColU.TABLE_SCHEMA = Tab.TABLE_SCHEMA 
        AND ColU.Table_Name = Tab.Table_Name
        and ColU.Constraint_Name = Tab.Constraint_Name
		AND tab.Constraint_Type = 'PRIMARY KEY'
	) kcu
	on kcu.TABLE_SCHEMA = col.TABLE_SCHEMA 
    AND kcu.Table_Name = col.Table_Name 
    AND kcu.Column_Name = col.COLUMN_NAME 
    WHERE 
        col.COLUMN_NAME not in ({ignoreColumns})
        AND col.COLUMN_NAME not in (SELECT ColumnName FROM Virtual) --Exclude SysCols / Virtual Cols as they are added seperatly
        AND col.TABLE_SCHEMA = '{srcSchema}' AND col.TABLE_NAME = '{srcObject}' 
    UNION ALL
    SELECT SrcDB,SrcSch,SrcTbl, ColumnName,OrdPos,DataType,Coll,ColClean,IsKey,[IsDate],IsIgnored,SelectExp,SrcAddColumnCMD
    FROM Virtual
    WHERE 0 = {_srcIsStaging.ToString()}
   -- WHERE ColumnName not in (SELECT Column_Name from UniqueCleanedColName)
;

";
            if (trgIsSynapse || srcIsSynapse)
            {
                TrgCmd = BuildColCleanSyn(trgDatabase, trgSchema, trgObject, ignoreColumns, defTokenSchemaForCte,
                    sysTblColSelectForCte, sysColSelectForCte, sysDataTypeSelectForCte, virtualSchemaForCte, _trgWithHint);
            }
            else
            {
                TrgCmd = BuildColClean(trgDatabase, trgSchema, trgObject, ignoreColumns, defTokenSchemaXml,
                    sysTblColSelectXml, sysColSelectXml, sysDataTypeSelectXml, virtualSchemaXml, _trgWithHint);
            }

            TrgCmd = TrgCmd +
                     $@" SELECT col.TABLE_CATALOG,col.TABLE_SCHEMA,col.TABLE_NAME,col.COLUMN_NAME,col.ORDINAL_POSITION,col.DATA_TYPE, IsNull(col.COLLATION_NAME,'') as COLLATION_NAME,  IsNull(cl.COLUMN_NAME_CLEANED,cl.COLUMN_NAME) as COLUMN_NAME_CLEANED,
    CASE WHEN cl.COLUMN_NAME_CLEANED in ({_keyColumnsParsed}) OR cl.COLUMN_NAME IN ({_keyColumnsParsed}) THEN 1 ELSE 0 END IsKeyColumn,
    CASE WHEN cl.COLUMN_NAME_CLEANED in ({dateColumnsParsed}) OR cl.COLUMN_NAME in ({dateColumnsParsed}) THEN 1 ELSE 0 END IsDateColumn,
    CASE WHEN cl.COLUMN_NAME_CLEANED in ({ignoreColumns}) OR cl.COLUMN_NAME in ({ignoreColumns}) THEN 1 ELSE 0 END IsIgnored,
    '' as SelectExp,
        CASE WHEN cl.COLUMN_NAME_CLEANED in ({ignoreColumns}) OR cl.COLUMN_NAME in ({ignoreColumns}) THEN 
     '' ELSE 
    'ALTER TABLE [{trgDatabase}].[{trgSchema}].[{trgObject}] ADD ' + Quotename({"cl." + _metaJoinColName})  + ' '
    + CASE WHEN data_type in ('nvarchar','nchar','ntext') AND 1={_convUnicodeDt} THEN substring(data_type,2,len(data_type)) ELSE data_type END
    + CASE WHEN data_type in ('sql_variant','text','ntext','int','float','smallint','bigint','real','datetime','smalldatetime','tinyint', 'bit', 'datetime2','date','xml','hierarchyid','geography') THEN '' WHEN data_type in ('time') THEN '(' + Cast(Datetime_Precision AS VARCHAR) + ')' WHEN data_type in ('decimal', 'NUMERIC') THEN '(' + Cast(numeric_precision AS VARCHAR) + ', ' + Cast(numeric_scale AS VARCHAR) + ')' ELSE ISNULL( CASE WHEN DATA_TYPE IN ('XML')  THEN '' WHEN character_maximum_length = -1 THEN '(MAX)' ELSE '(' + CAST(character_maximum_length AS VARCHAR) + ')' END , '')  END + ' ' 
    -- can lead to issues null issues NOT NULL  + ' ' + (CASE WHEN IS_NULLABLE = 'No' THEN 'NOT ' ELSE '' END) + 'NULL ' 
    -- can lead to issues null issues + CASE WHEN cl.COLUMN_NAME_CLEANED in ({_keyColumnsParsed}) OR cl.COLUMN_NAME IN ({dateColumnsParsed}) OR kcu.COLUMN_NAME is not null THEN 'NOT ' ELSE '' END 
    +  'NULL ' --NotNullOnlyFor PK Columns
    -- + CASE WHEN col.COLUMN_DEFAULT IS NOT NULL AND col.COLUMN_DEFAULT NOT LIKE '%NEXT%' THEN 'DEFAULT '  + col.COLUMN_DEFAULT ELSE '' END  -- The source data will provide default values
    + ';' END as AddColumnCMD
    FROM  [{trgDatabase}].INFORMATION_SCHEMA.COLUMNS col with({_trgWithHint}) 
    LEFT OUTER JOIN UniqueCleanedColName cl
    on cl.TABLE_SCHEMA = col.TABLE_SCHEMA
    AND cl.TABLE_NAME = col.TABLE_NAME 
    AND cl.COLUMN_NAME = col.COLUMN_NAME 
    LEFT OUTER JOIN
	(
	SELECT ColU.TABLE_SCHEMA, ColU.Table_Name, ColU.Column_Name From 
    	[{trgDatabase}].INFORMATION_SCHEMA.TABLE_CONSTRAINTS Tab with({_trgWithHint}), 
    	[{trgDatabase}].INFORMATION_SCHEMA.KEY_COLUMN_USAGE  ColU with({_trgWithHint})
	WHERE 
		ColU.TABLE_SCHEMA = Tab.TABLE_SCHEMA 
        AND ColU.Table_Name = Tab.Table_Name
        and ColU.Constraint_Name = Tab.Constraint_Name
		AND tab.Constraint_Type = 'PRIMARY KEY'
	) kcu
	on  kcu.TABLE_SCHEMA = col.TABLE_SCHEMA 
    AND kcu.Table_Name = col.Table_Name 
    AND kcu.Column_Name = col.COLUMN_NAME 
    WHERE  col.COLUMN_NAME not in ({ignoreColumns}) AND col.TABLE_SCHEMA = '{trgSchema}' AND col.TABLE_NAME = '{trgObject}' ";

            EventArgsSchema eas = new EventArgsSchema
            {
                MetaSrcObject = $"[{srcDatabase}].[{srcSchema}].[{srcObject}]",
                MetaSrcSchemaCmd = SrcCmd,
                MetaTrgObject = $"[{trgDatabase}].[{trgSchema}].[{trgObject}]",
                MetaTrgSchemaCmd = TrgCmd
            };
            GetSchemaScript?.Invoke(this, eas);

        }

        internal void Compare()
        {
            ColumnMappings.Clear();
            ValidColumns = "";
            ValidUpdateColumns = "";
            ValidChkSumColumns = "";
            SchemaChanged = false;
            SrcExists = false;
            TrgExists = false;

            IncrementalColumnsVerified = "";
            KeyColumnsVerified = "";
            DateColumnVerified = "";
            ValidSrcColumnsWithAlias = "";
            ValidSrcColumnsWithCollateAndAlias = "";
            TargetColumns = "";
            KeyColumnsSrcVerified = "";
            NewColumnsCmd = "";

            var srcData = new GetData(_srcSqlCon, SrcCmd, _commandTimeOutInSeconds);
            var trgData = new GetData(_trgSqlCon, TrgCmd, _commandTimeOutInSeconds);

            var srcTbl = srcData.Fetch();
            var trgTbl = trgData.Fetch();

            //string trgColList = "";
            //string srcColList = "";
            //foreach (DataRow dr in trgTbl.Rows)
            //{
            //    trgColList  += $",{dr[_metaJoinColName]}";
            //}

            //foreach (DataRow dr in srcTbl.Rows)
            //{
            //    srcColList += $",{dr[_metaJoinColName]}";
            //}

            foreach (DataRow dr in trgTbl.Rows)
                TargetColumns += $",[{dr[_metaJoinColName]}]";
            if (TargetColumns.Length > 0) TargetColumns = TargetColumns.Substring(1);

            foreach (DataRow dr in srcTbl.Rows)
                SourceColumns += $",[{dr[_metaJoinColName]}]";
            if (SourceColumns.Length > 0) SourceColumns = SourceColumns.Substring(1);

            var sTable = srcTbl.AsEnumerable();
            var tTable = trgTbl.AsEnumerable();

            var newColumns = from sTableRow in sTable
                             join tTableRow in tTable
                             on sTableRow.Field<string>(_metaJoinColName).ToLower() equals tTableRow.Field<string>(_metaJoinColName).ToLower()
                             into outer
                             from dtouter in outer.DefaultIfEmpty()
                             where dtouter == null
                             select new
                             {
                                 TABLE_CATALOG = (string)sTableRow["TABLE_CATALOG"],
                                 TABLE_SCHEMA = (string)sTableRow["TABLE_SCHEMA"],
                                 TABLE_NAME = (string)sTableRow["TABLE_NAME"],
                                 COLUMN_NAME = (string)sTableRow[_metaJoinColName],
                                 ORDINAL_POSITION = (int)sTableRow["ORDINAL_POSITION"],
                                 AddColumnCMD = (string)sTableRow["AddColumnCMD"],
                                 IsIgnored = (int)sTableRow["IsIgnored"],
                                 SelectExp = (string)sTableRow["SelectExp"]
                             };

            /*
            var newColumns = from srcTable in srcTbl.AsEnumerable()
                join trgTable in trgTbl.AsEnumerable() on (string)srcTable[_metaJoinColName] equals (string)trgTable[_metaJoinColName] into gj
     
                             from trgTable in gj.DefaultIfEmpty()
                where trgTable == null
                select new 
                {
                    TABLE_CATALOG = (string) srcTable["TABLE_CATALOG"],
                    TABLE_SCHEMA = (string) srcTable["TABLE_SCHEMA"],
                    TABLE_NAME = (string) srcTable["TABLE_NAME"],
                    COLUMN_NAME = (string) srcTable[_metaJoinColName],
                    ORDINAL_POSITION = (int) srcTable["ORDINAL_POSITION"],
                    AddColumnCMD = (string) srcTable["AddColumnCMD"],
                    SelectExp = (string) srcTable["SelectExp"]
                };

            */

            foreach (var item in newColumns)
            {

                if (item.IsIgnored == 0 && _ignoreColumns.Contains(item.COLUMN_NAME, StringComparer.OrdinalIgnoreCase) == false)
                {
                    NewColumnsCmd = NewColumnsCmd + item.AddColumnCMD;
                }
            }

            //NewColumnsCmd = newColumns.Count().ToString();
            var comonColumns = from srcTable in srcTbl.AsEnumerable()
                               join trgTable in trgTbl.AsEnumerable() on
                                   srcTable[_metaJoinColName].ToString().ToLower() equals trgTable[_metaJoinColName].ToString().ToLower()
                               orderby srcTable["ORDINAL_POSITION"]
                               select new
                               {
                                   TABLE_CATALOG = (string)srcTable["TABLE_CATALOG"],
                                   TABLE_SCHEMA = (string)srcTable["TABLE_SCHEMA"],
                                   TABLE_NAME = (string)srcTable["TABLE_NAME"],
                                   COLUMN_NAME = (string)srcTable[_metaJoinColName],
                                   COLUMN_NAME_SRC = (string)srcTable["COLUMN_NAME"],
                                   COLUMN_NAME_TRG = (string)trgTable["COLUMN_NAME"],
                                   ORDINAL_POSITION = (int)srcTable["ORDINAL_POSITION"],
                                   COLLATION_NAME_SRC = (string)srcTable["COLLATION_NAME"],
                                   COLLATION_NAME_TRG = (string)trgTable["COLLATION_NAME"],
                                   IsKeyColumn = (int)srcTable["IsKeyColumn"],
                                   IsDateColumn = (int)srcTable["IsDateColumn"],
                                   DATA_TYPE_SRC = (string)srcTable["DATA_TYPE"],
                                   DATA_TYPE_TRG = (string)trgTable["DATA_TYPE"],
                                   SelectExp = (string)srcTable["SelectExp"]
                               };

            var colNameMisMatch = false;
            var colDataTypeMisMatch = false;
            foreach (var item in comonColumns)
            {
                if (item.COLUMN_NAME_TRG.ToLower() != item.COLUMN_NAME.ToLower()) colNameMisMatch = true;
                if (item.DATA_TYPE_SRC.ToLower() != item.DATA_TYPE_TRG.ToLower()) colDataTypeMisMatch = true;

                if (item.COLUMN_NAME == "HashKey_DW")
                {
                    //ValidUpdateColumns += $",[{item.COLUMN_NAME}]"; Changed Hash Value = New Row :)
                    HashKeyColumnFound = true;
                }

                if (ColumnMappings.ContainsKeyIgnoreCase(item.COLUMN_NAME) == false)
                    ColumnMappings.Add(item.COLUMN_NAME, item.COLUMN_NAME);

                ValidColumns += $",[{item.COLUMN_NAME}]";

                //if (_hashKeyCols.Contains(

                switch (item.IsKeyColumn.ToString())
                {
                    case "0":
                        {
                            if (_hashKeyCols.Contains(item.COLUMN_NAME, StringComparer.OrdinalIgnoreCase) == false)
                            {
                                ValidUpdateColumns += $",[{item.COLUMN_NAME}]";
                            }

                            if (!_hashKeyCols.Contains(item.COLUMN_NAME, StringComparer.OrdinalIgnoreCase) && !_invalidChecksumDataTypes.Contains(item.DATA_TYPE_SRC, StringComparer.OrdinalIgnoreCase) && !_ingoreChecksumColumns.Contains(item.COLUMN_NAME, StringComparer.OrdinalIgnoreCase))
                                ValidChkSumColumns += $",[{item.COLUMN_NAME}]";
                            break;
                        }
                    case "1":
                        KeyColumnsVerified += $",[{item.COLUMN_NAME}]";
                        KeyColumnsSrcVerified += $",[{item.COLUMN_NAME_SRC}]";

                        break;
                }

                if (item.IsDateColumn.ToString() == "1") DateColumnVerified = $"[{item.COLUMN_NAME}]";

                ValidSrcColumnsWithAlias = ValidSrcColumnsWithAlias +
                                           (item.SelectExp.Length > 0
                                               ? "," + item.SelectExp
                                               : ",[" + item.COLUMN_NAME_SRC + "] ") + " AS [" +
                                           item.COLUMN_NAME + "]";

                ValidSrcColumnsWithCollateAndAlias = ValidSrcColumnsWithCollateAndAlias +
                                                     (item.SelectExp.Length > 0
                                                         ? "," + item.SelectExp
                                                         : ",[" + item.COLUMN_NAME_SRC + "] ") +
                                                     (item.COLLATION_NAME_SRC != item.COLLATION_NAME_TRG &&
                                                      item.COLLATION_NAME_SRC.Length > 0 && item.COLLATION_NAME_TRG.Length > 0
                                                         ? " COLLATE " + item.COLLATION_NAME_TRG
                                                         : "") + " AS [" + item.COLUMN_NAME + "]";
            }


            if (ValidSrcColumnsWithAlias.IndexOf(',') == 0)
                ValidSrcColumnsWithAlias = ValidSrcColumnsWithAlias.Substring(1);

            if (ValidSrcColumnsWithCollateAndAlias.IndexOf(',') == 0)
                ValidSrcColumnsWithCollateAndAlias = ValidSrcColumnsWithCollateAndAlias.Substring(1);

            if (KeyColumnsVerified.IndexOf(',') == 0)
            {
                KeyColumnsVerified = KeyColumnsVerified.Substring(1);
                KeyColumnsSrcVerified = KeyColumnsSrcVerified.Substring(1);
            }
            else
            {
                //CheckForError for invalid key columns
                var srcKeyColumns = _keyColumns.Split(','); //KeyColumns.Split(',');
                var srcKeyColumnsQualifed = "";
                foreach (var val in srcKeyColumns)
                    foreach (var item in comonColumns)
                        if (val == item.COLUMN_NAME)
                            srcKeyColumnsQualifed = srcKeyColumnsQualifed + "," + item.COLUMN_NAME;
                if (srcKeyColumnsQualifed.IndexOf(',') == 0) srcKeyColumnsQualifed = srcKeyColumnsQualifed.Substring(1);

                KeyColumnsVerified = srcKeyColumnsQualifed;
            }


            var incColumns = _incrementalColumns.Split(','); //KeyColumns.Split(',');
            var incColumnsQualifed = "";
            var incColumnsSrcQualifed = "";
            foreach (var val in incColumns)
            {
                foreach (var item in comonColumns)
                    if (val == item.COLUMN_NAME)
                    {
                        incColumnsQualifed += $",[{item.COLUMN_NAME.Trim()}]";
                        incColumnsSrcQualifed += $",[{item.COLUMN_NAME_SRC.Trim()}]";
                    }
            }

            if (incColumnsQualifed.IndexOf(',') == 0) incColumnsQualifed = incColumnsQualifed.Substring(1);
            if (incColumnsSrcQualifed.IndexOf(',') == 0) incColumnsSrcQualifed = incColumnsSrcQualifed.Substring(1);

            IncrementalColumnsVerified = incColumnsQualifed;
            IncrementalColumnsSrcVerified = incColumnsSrcQualifed;

            if (DateColumnVerified.Length > 0)
            {

            }
            else
            {
                var srcDateColumnsQualifed = "";
                foreach (var item in comonColumns)
                    if (_dateColumn == item.COLUMN_NAME)
                        srcDateColumnsQualifed = item.COLUMN_NAME;

                DateColumnVerified = srcDateColumnsQualifed;
            }

            if (ValidColumns.IndexOf(',') == 0)
            {
                ValidColumns = ValidColumns.Substring(1);
                ValidUpdateColumns = ValidUpdateColumns.Substring(1);
            }

            if (srcTbl.Rows.Count > 0) SrcExists = true;


            if (trgTbl.Rows.Count > 0) TrgExists = true;

            if (srcTbl.Rows.Count != comonColumns.Count() ||
                colNameMisMatch) // || srcTbl.Columns.Count != trgTbl.Columns.Count
                SchemaChanged = true;

            if (_trgIsStaging &&
                srcTbl.Rows.Count < trgTbl.Rows.Count) // || srcTbl.Columns.Count != trgTbl.Columns.Count
                SchemaChanged = true;

            //If staging table has a miss match on datatypes
            if (_trgIsStaging && colDataTypeMisMatch)
                SchemaChanged = true;


            /*
             * Detect Other differences 
            for (int i = 0; i < srcTbl.Rows.Count; i++)
            {
                for (int c = 0; c < srcTbl.Columns.Count; c++)
                {
                    if (!Equals(srcTbl.Rows[i][c], trgTbl.Rows[i][c]))
                        schemaChanged = true;
                }
            }
            */
        }

        private string BuildRenameCmd(string trgDatabase, string trgSchema, string trgObject)
        {
            var tmpTableName = "_TmpDS";
            RenamedTrgObject = trgObject + tmpTableName;
            var rValue = "";
            var renameCmd = $@"SELECT 
    'IF exists(SELECT TABLE_NAME FROM  [{trgDatabase}].INFORMATION_SCHEMA.TABLES tbl with({_trgWithHint}) WHERE tbl.TABLE_NAME = ''{RenamedTrgObject}'' and tbl.TABLE_SCHEMA = ''{trgSchema}'') BEGIN DROP TABLE [{trgDatabase}].[{trgSchema}].[{RenamedTrgObject}] END;' +
    rename.cmd as cmd
    FROM   [{trgDatabase}].sys.objects o with({_trgWithHint})
            CROSS apply(SELECT '' + cmd
                        FROM   (SELECT ' ;EXEC [{trgDatabase}].sys.sp_rename ''' + s1.name + '.' + o1.name
                                        + ''', ''' + + o1.name + '{tmpTableName}' + '''' + ';' AS cmd
                                FROM   [{trgDatabase}].sys.objects o1 with({_trgWithHint})
                                        INNER JOIN [{trgDatabase}].sys.schemas s1 with({_trgWithHint})
                                                ON o1.schema_id = s1.schema_id
                                WHERE  o1.object_id = o.object_id
                                UNION -- index renames
                                SELECT ' EXEC [{trgDatabase}].sys.sp_rename ''' + ( s1.name + '.' + o2.name + '{tmpTableName}' + '.' + i.name ) + ''', ''' + ( i.name + '{tmpTableName}' ) + ''', ''INDEX''' + ';' AS cmd
                                FROM   [{trgDatabase}].sys.objects o2 with({_trgWithHint})
                                        INNER JOIN [{trgDatabase}].sys.schemas s1 with({_trgWithHint})
                                                ON o2.schema_id = s1.schema_id
                                        LEFT JOIN [{trgDatabase}].sys.indexes i
                                                ON o2.object_id = i.object_id
                                WHERE  o2.object_id = o.object_id
                                --sys.indexes.name like '%' + @old + '%'
                                ) AS a
                        FOR XML PATH('')) rename(cmd)
    WHERE  type IN( 'V', 'U' )
            AND object_id = Object_id('[{trgDatabase}].[{trgSchema}].[{trgObject}]'); ";

            EventArgsSchema eas = new EventArgsSchema
            {
                RenameObject = $"[{trgDatabase}].[{trgSchema}].[{trgObject}]",
                GenerateRenameCmd = renameCmd
            };
            GetSchemaScript?.Invoke(this, eas);


            GenerateRenameCmd = renameCmd;
            var gd = new GetData(_trgSqlCon, renameCmd, _commandTimeOutInSeconds);
            var dt = gd.Fetch();

            if (dt.Rows.Count > 0) rValue = dt.Rows[0]["cmd"]?.ToString() ?? string.Empty;

            EventArgsSchema eas2 = new EventArgsSchema
            {
                RenameObject = $"[{trgDatabase}].[{trgSchema}].[{trgObject}]",
                RenameCmd = rValue
            };
            GetSchemaScript?.Invoke(this, eas2);

            return rValue;
        }



        private string BuildColClean(string database, string schema, string tablename, string ignoreColumn,
        string tokenSchemaXml, string sysTblColSelectXml,
        string sysColSelectXml,
        string sysDataTypeSelectXml, string virtualSchemaXml, string withHint)
        {

            var colCleanSql = $@"
DECLARE @data xml = N'{tokenSchemaXml}',
     @SysTblCol xml = N'{sysTblColSelectXml}',
     @SysCol xml = N'{sysColSelectXml}',
     @SysDataType xml = N'{sysDataTypeSelectXml}',
     @Virtual xml = N'{virtualSchemaXml}';

; WITH
    E1(N) AS ( 
    -- 10 rows
    SELECT 1 UNION ALL SELECT 1 UNION ALL SELECT 1 UNION ALL SELECT 1 UNION ALL 
    SELECT 1 UNION ALL SELECT 1 UNION ALL SELECT 1 UNION ALL SELECT 1 UNION ALL 
    SELECT 1 UNION ALL SELECT 1
    ), 
    E2(N) AS (SELECT 1 FROM E1 a, E1 b), -- 100 rows
    E4(N) AS (SELECT 1 FROM E2 a, E2 b), -- 10,000 rows
    Nums(Number) AS (
        SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) FROM E4
    ),
    spt_values
    AS 
    (
	    SELECT Number 
        FROM Nums
        WHERE Number <= 201
    ),
    SrcColumnDS as 
    (
	    SELECT  TABLE_NAME,  TABLE_SCHEMA,  COLUMN_NAME, ORDINAL_POSITION
	    FROM   [{database}].INFORMATION_SCHEMA.COLUMNS with({withHint})
	    WHERE TABLE_SCHEMA = '{schema}' AND TABLE_NAME = '{tablename}'
        AND COLUMN_NAME not in ({ignoreColumn})
    ),
    CleanColumn as 
    (
        select 
            P.Number as ValueOrder,
            isnull ( substring ( t.COLUMN_NAME , number , 1 ) , '' ) as ScrubbedValue,
            t.TABLE_SCHEMA, 
		    t.TABLE_NAME, 
		    t.COLUMN_NAME,
            t.ORDINAL_POSITION
        from
            SrcColumnDS t
            left join spt_values P
                on P.number between 1 and len(t.COLUMN_NAME)
        where
            PatIndex('%{_colCleanupSqlRegExp}%', substring(t.COLUMN_NAME,P.number,1) ) = 0
    ),
    ColumnNameCleaned as
    (
    SELECT
        TABLE_NAME, TABLE_SCHEMA, COLUMN_NAME, 
        IsNull([1],'')+IsNull([2],'')+IsNull([3],'')+IsNull([4],'')+IsNull([5],'')+IsNull([6],'')+IsNull([7],'')+IsNull([8],'')+IsNull([9],'')+IsNull([10],'')+IsNull([11],'')+IsNull([12],'')+IsNull([13],'')+IsNull([14],'')+IsNull([15],'')+IsNull([16],'')+IsNull([17],'')+IsNull([18],'')+IsNull([19],'')+IsNull([20],'')+IsNull([21],'')+IsNull([22],'')+IsNull([23],'')+IsNull([24],'')+IsNull([25],'')+IsNull([26],'')+IsNull([27],'')+IsNull([28],'')+IsNull([29],'')+IsNull([30],'')+IsNull([31],'')+IsNull([32],'')+IsNull([33],'')+IsNull([34],'')+IsNull([35],'')+IsNull([36],'')+IsNull([37],'')+IsNull([38],'')+IsNull([39],'')+IsNull([40],'')+IsNull([41],'')+IsNull([42],'')+IsNull([43],'')+IsNull([44],'')+IsNull([45],'')+IsNull([46],'')+IsNull([47],'')+IsNull([48],'')+IsNull([49],'')+IsNull([50],'')+IsNull([51],'')+IsNull([52],'')+IsNull([53],'')+IsNull([54],'')+IsNull([55],'')+IsNull([56],'')+IsNull([57],'')+IsNull([58],'')+IsNull([59],'')+IsNull([60],'')+IsNull([61],'')+IsNull([62],'')+IsNull([63],'')+IsNull([64],'')+IsNull([65],'')+IsNull([66],'')+IsNull([67],'')+IsNull([68],'')+IsNull([69],'')+IsNull([70],'')+IsNull([71],'')+IsNull([72],'')+IsNull([73],'')+IsNull([74],'')+IsNull([75],'')+IsNull([76],'')+IsNull([77],'')+IsNull([78],'')+IsNull([79],'')+IsNull([80],'')+IsNull([81],'')+IsNull([82],'')+IsNull([83],'')+IsNull([84],'')+IsNull([85],'')+IsNull([86],'')+IsNull([87],'')+IsNull([88],'')+IsNull([89],'')+IsNull([90],'')+IsNull([91],'')+IsNull([92],'')+IsNull([93],'')+IsNull([94],'')+IsNull([95],'')+IsNull([96],'')+IsNull([97],'')+IsNull([98],'')+IsNull([99],'')+IsNull([100],'')+IsNull([101],'')+
                        IsNull([102],'')+IsNull([103],'')+IsNull([104],'')+IsNull([105],'')+IsNull([106],'')+IsNull([107],'')+IsNull([108],'')+IsNull([109],'')+IsNull([110],'')+IsNull([111],'')+IsNull([112],'')+IsNull([113],'')+IsNull([114],'')+IsNull([115],'')+IsNull([116],'')+IsNull([117],'')+IsNull([118],'')+IsNull([119],'')+IsNull([120],'')+IsNull([121],'')+IsNull([122],'')+IsNull([123],'')+IsNull([124],'')+IsNull([125],'')+IsNull([126],'')+IsNull([127],'')+IsNull([128],'')+IsNull([129],'')+IsNull([130],'')+IsNull([131],'')+IsNull([132],'')+IsNull([133],'')+IsNull([134],'')+IsNull([135],'')+IsNull([136],'')+IsNull([137],'')+IsNull([138],'')+IsNull([139],'')+IsNull([140],'')+IsNull([141],'')+IsNull([142],'')+IsNull([143],'')+IsNull([144],'')+IsNull([145],'')+IsNull([146],'')+IsNull([147],'')+IsNull([148],'')+IsNull([149],'')+IsNull([150],'')+IsNull([151],'')+IsNull([152],'')+IsNull([153],'')+IsNull([154],'')+IsNull([155],'')+IsNull([156],'')+IsNull([157],'')+IsNull([158],'')+IsNull([159],'')+IsNull([160],'')+IsNull([161],'')+IsNull([162],'')+IsNull([163],'')+IsNull([164],'')+IsNull([165],'')+IsNull([166],'')+IsNull([167],'')+IsNull([168],'')+IsNull([169],'')+IsNull([170],'')+IsNull([171],'')+IsNull([172],'')+IsNull([173],'')+IsNull([174],'')+IsNull([175],'')+IsNull([176],'')+IsNull([177],'')+IsNull([178],'')+IsNull([179],'')+IsNull([180],'')+IsNull([181],'')+IsNull([182],'')+IsNull([183],'')+IsNull([184],'')+IsNull([185],'')+IsNull([186],'')+IsNull([187],'')+IsNull([188],'')+IsNull([189],'')+IsNull([190],'')+IsNull([191],'')+IsNull([192],'')+IsNull([193],'')+IsNull([194],'')+IsNull([195],'')+IsNull([196],'')+IsNull([197],'')+IsNull([198],'')+IsNull([199],'')+IsNull([200],'')+IsNull([201],'')
                        --IsNull([202],'')+IsNull([203],'')+IsNull([204],'')+IsNull([205],'')+IsNull([206],'')+IsNull([207],'')+IsNull([208],'')+IsNull([209],'')+IsNull([210],'')+IsNull([211],'')+IsNull([212],'')+IsNull([213],'')+IsNull([214],'')+IsNull([215],'')+IsNull([216],'')+IsNull([217],'')+IsNull([218],'')+IsNull([219],'')+IsNull([220],'')+IsNull([221],'')+IsNull([222],'')+IsNull([223],'')+IsNull([224],'')+IsNull([225],'')+IsNull([226],'')+IsNull([227],'')+IsNull([228],'')+IsNull([229],'')+IsNull([230],'')+IsNull([231],'')+IsNull([232],'')+IsNull([233],'')+IsNull([234],'')+IsNull([235],'')+IsNull([236],'')+IsNull([237],'')+IsNull([238],'')+IsNull([239],'')+IsNull([240],'')+IsNull([241],'')+IsNull([242],'')+IsNull([243],'')+IsNull([244],'')+IsNull([245],'')+IsNull([246],'')+IsNull([247],'')+IsNull([248],'')+IsNull([249],'')+IsNull([250],'')+IsNull([251],'')+IsNull([252],'')+IsNull([253],'')+IsNull([254],'')+IsNull([255],'')+IsNull([256],'')+IsNull([257],'')+IsNull([258],'')+IsNull([259],'')+IsNull([260],'')+IsNull([261],'')+IsNull([262],'')+IsNull([263],'')+IsNull([264],'')+IsNull([265],'')+IsNull([266],'')+IsNull([267],'')+IsNull([268],'')+IsNull([269],'')+IsNull([270],'')+IsNull([271],'')+IsNull([272],'')+IsNull([273],'')+IsNull([274],'')+IsNull([275],'')+IsNull([276],'')+IsNull([277],'')+IsNull([278],'')+IsNull([279],'')+IsNull([280],'')+IsNull([281],'')+IsNull([282],'')+IsNull([283],'')+IsNull([284],'')+IsNull([285],'')+IsNull([286],'')+IsNull([287],'')+IsNull([288],'')+IsNull([289],'')+IsNull([290],'')+IsNull([291],'')+IsNull([292],'')+IsNull([293],'')+IsNull([294],'')+IsNull([295],'')+IsNull([296],'')+IsNull([297],'')+IsNull([298],'')+IsNull([299],'')+IsNull([300],'')+IsNull([301],'')+
                        --IsNull([302],'')+IsNull([303],'')+IsNull([304],'')+IsNull([305],'')+IsNull([306],'')+IsNull([307],'')+IsNull([308],'')+IsNull([309],'')+IsNull([310],'')+IsNull([311],'')+IsNull([312],'')+IsNull([313],'')+IsNull([314],'')+IsNull([315],'')+IsNull([316],'')+IsNull([317],'')+IsNull([318],'')+IsNull([319],'')+IsNull([320],'')+IsNull([321],'')+IsNull([322],'')+IsNull([323],'')+IsNull([324],'')+IsNull([325],'')+IsNull([326],'')+IsNull([327],'')+IsNull([328],'')+IsNull([329],'')+IsNull([330],'')+IsNull([331],'')+IsNull([332],'')+IsNull([333],'')+IsNull([334],'')+IsNull([335],'')+IsNull([336],'')+IsNull([337],'')+IsNull([338],'')+IsNull([339],'')+IsNull([340],'')+IsNull([341],'')+IsNull([342],'')+IsNull([343],'')+IsNull([344],'')+IsNull([345],'')+IsNull([346],'')+IsNull([347],'')+IsNull([348],'')+IsNull([349],'')+IsNull([350],'')+IsNull([351],'')+IsNull([352],'')+IsNull([353],'')+IsNull([354],'')+IsNull([355],'')+IsNull([356],'')+IsNull([357],'')+IsNull([358],'')+IsNull([359],'')+IsNull([360],'')+IsNull([361],'')+IsNull([362],'')+IsNull([363],'')+IsNull([364],'')+IsNull([365],'')+IsNull([366],'')+IsNull([367],'')+IsNull([368],'')+IsNull([369],'')+IsNull([370],'')+IsNull([371],'')+IsNull([372],'')+IsNull([373],'')+IsNull([374],'')+IsNull([375],'')+IsNull([376],'')+IsNull([377],'')+IsNull([378],'')+IsNull([379],'')+IsNull([380],'')+IsNull([381],'')+IsNull([382],'')+IsNull([383],'')+IsNull([384],'')+IsNull([385],'')+IsNull([386],'')+IsNull([387],'')+IsNull([388],'')+IsNull([389],'')+IsNull([390],'')+IsNull([391],'')+IsNull([392],'')+IsNull([393],'')+IsNull([394],'')+IsNull([395],'')+IsNull([396],'')+IsNull([397],'')+IsNull([398],'')+IsNull([399],'')+IsNull([400],'')+IsNull([401],'')+
                        --IsNull([402],'')+IsNull([403],'')+IsNull([404],'')+IsNull([405],'')+IsNull([406],'')+IsNull([407],'')+IsNull([408],'')+IsNull([409],'')+IsNull([410],'')+IsNull([411],'')+IsNull([412],'')+IsNull([413],'')+IsNull([414],'')+IsNull([415],'')+IsNull([416],'')+IsNull([417],'')+IsNull([418],'')+IsNull([419],'')+IsNull([420],'')+IsNull([421],'')+IsNull([422],'')+IsNull([423],'')+IsNull([424],'')+IsNull([425],'')+IsNull([426],'')+IsNull([427],'')+IsNull([428],'')+IsNull([429],'')+IsNull([430],'')+IsNull([431],'')+IsNull([432],'')+IsNull([433],'')+IsNull([434],'')+IsNull([435],'')+IsNull([436],'')+IsNull([437],'')+IsNull([438],'')+IsNull([439],'')+IsNull([440],'')+IsNull([441],'')+IsNull([442],'')+IsNull([443],'')+IsNull([444],'')+IsNull([445],'')+IsNull([446],'')+IsNull([447],'')+IsNull([448],'')+IsNull([449],'')+IsNull([450],'')+IsNull([451],'')+IsNull([452],'')+IsNull([453],'')+IsNull([454],'')+IsNull([455],'')+IsNull([456],'')+IsNull([457],'')+IsNull([458],'')+IsNull([459],'')+IsNull([460],'')+IsNull([461],'')+IsNull([462],'')+IsNull([463],'')+IsNull([464],'')+IsNull([465],'')+IsNull([466],'')+IsNull([467],'')+IsNull([468],'')+IsNull([469],'')+IsNull([470],'')+IsNull([471],'')+IsNull([472],'')+IsNull([473],'')+IsNull([474],'')+IsNull([475],'')+IsNull([476],'')+IsNull([477],'')+IsNull([478],'')+IsNull([479],'')+IsNull([480],'')+IsNull([481],'')+IsNull([482],'')+IsNull([483],'')+IsNull([484],'')+IsNull([485],'')+IsNull([486],'')+IsNull([487],'')+IsNull([488],'')+IsNull([489],'')+IsNull([490],'')+IsNull([491],'')+IsNull([492],'')+IsNull([493],'')+IsNull([494],'')+IsNull([495],'')+IsNull([496],'')+IsNull([497],'')+IsNull([498],'')+IsNull([499],'')+IsNull([500],'')+IsNull([501],'')+
                        --IsNull([502],'')+IsNull([503],'')+IsNull([504],'')+IsNull([505],'')+IsNull([506],'')+IsNull([507],'')+IsNull([508],'')+IsNull([509],'')+IsNull([510],'')+IsNull([511],'')+IsNull([512],'')+IsNull([513],'')+IsNull([514],'')+IsNull([515],'')+IsNull([516],'')+IsNull([517],'')+IsNull([518],'')+IsNull([519],'')+IsNull([520],'')+IsNull([521],'')+IsNull([522],'')+IsNull([523],'')+IsNull([524],'')+IsNull([525],'')+IsNull([526],'')+IsNull([527],'')+IsNull([528],'')+IsNull([529],'')+IsNull([530],'')+IsNull([531],'')+IsNull([532],'')+IsNull([533],'')+IsNull([534],'')+IsNull([535],'')+IsNull([536],'')+IsNull([537],'')+IsNull([538],'')+IsNull([539],'')+IsNull([540],'')+IsNull([541],'')+IsNull([542],'')+IsNull([543],'')+IsNull([544],'')+IsNull([545],'')+IsNull([546],'')+IsNull([547],'')+IsNull([548],'')+IsNull([549],'')+IsNull([550],'')+IsNull([551],'')+IsNull([552],'')+IsNull([553],'')+IsNull([554],'')+IsNull([555],'')+IsNull([556],'')+IsNull([557],'')+IsNull([558],'')+IsNull([559],'')+IsNull([560],'')+IsNull([561],'')+IsNull([562],'')+IsNull([563],'')+IsNull([564],'')+IsNull([565],'')+IsNull([566],'')+IsNull([567],'')+IsNull([568],'')+IsNull([569],'')+IsNull([570],'')+IsNull([571],'')+IsNull([572],'')+IsNull([573],'')+IsNull([574],'')+IsNull([575],'')+IsNull([576],'')+IsNull([577],'')+IsNull([578],'')+IsNull([579],'')+IsNull([580],'')+IsNull([581],'')+IsNull([582],'')+IsNull([583],'')+IsNull([584],'')+IsNull([585],'')+IsNull([586],'')+IsNull([587],'')+IsNull([588],'')+IsNull([589],'')+IsNull([590],'')+IsNull([591],'')+IsNull([592],'')+IsNull([593],'')+IsNull([594],'')+IsNull([595],'')+IsNull([596],'')+IsNull([597],'')+IsNull([598],'')+IsNull([599],'')+IsNull([600],'')+IsNull([601],'')+
                        --IsNull([602],'')+IsNull([603],'')+IsNull([604],'')+IsNull([605],'')+IsNull([606],'')+IsNull([607],'')+IsNull([608],'')+IsNull([609],'')+IsNull([610],'')+IsNull([611],'')+IsNull([612],'')+IsNull([613],'')+IsNull([614],'')+IsNull([615],'')+IsNull([616],'')+IsNull([617],'')+IsNull([618],'')+IsNull([619],'')+IsNull([620],'')+IsNull([621],'')+IsNull([622],'')+IsNull([623],'')+IsNull([624],'')+IsNull([625],'')+IsNull([626],'')+IsNull([627],'')+IsNull([628],'')+IsNull([629],'')+IsNull([630],'')+IsNull([631],'')+IsNull([632],'')+IsNull([633],'')+IsNull([634],'')+IsNull([635],'')+IsNull([636],'')+IsNull([637],'')+IsNull([638],'')+IsNull([639],'')+IsNull([640],'')+IsNull([641],'')+IsNull([642],'')+IsNull([643],'')+IsNull([644],'')+IsNull([645],'')+IsNull([646],'')+IsNull([647],'')+IsNull([648],'')+IsNull([649],'')+IsNull([650],'')+IsNull([651],'')+IsNull([652],'')+IsNull([653],'')+IsNull([654],'')+IsNull([655],'')+IsNull([656],'')+IsNull([657],'')+IsNull([658],'')+IsNull([659],'')+IsNull([660],'')+IsNull([661],'')+IsNull([662],'')+IsNull([663],'')+IsNull([664],'')+IsNull([665],'')+IsNull([666],'')+IsNull([667],'')+IsNull([668],'')+IsNull([669],'')+IsNull([670],'')+IsNull([671],'')+IsNull([672],'')+IsNull([673],'')+IsNull([674],'')+IsNull([675],'')+IsNull([676],'')+IsNull([677],'')+IsNull([678],'')+IsNull([679],'')+IsNull([680],'')+IsNull([681],'')+IsNull([682],'')+IsNull([683],'')+IsNull([684],'')+IsNull([685],'')+IsNull([686],'')+IsNull([687],'')+IsNull([688],'')+IsNull([689],'')+IsNull([690],'')+IsNull([691],'')+IsNull([692],'')+IsNull([693],'')+IsNull([694],'')+IsNull([695],'')+IsNull([696],'')+IsNull([697],'')+IsNull([698],'')+IsNull([699],'')+IsNull([700],'')+IsNull([701],'')+
                        --IsNull([702],'')+IsNull([703],'')+IsNull([704],'')+IsNull([705],'')+IsNull([706],'')+IsNull([707],'')+IsNull([708],'')+IsNull([709],'')+IsNull([710],'')+IsNull([711],'')+IsNull([712],'')+IsNull([713],'')+IsNull([714],'')+IsNull([715],'')+IsNull([716],'')+IsNull([717],'')+IsNull([718],'')+IsNull([719],'')+IsNull([720],'')+IsNull([721],'')+IsNull([722],'')+IsNull([723],'')+IsNull([724],'')+IsNull([725],'')+IsNull([726],'')+IsNull([727],'')+IsNull([728],'')+IsNull([729],'')+IsNull([730],'')+IsNull([731],'')+IsNull([732],'')+IsNull([733],'')+IsNull([734],'')+IsNull([735],'')+IsNull([736],'')+IsNull([737],'')+IsNull([738],'')+IsNull([739],'')+IsNull([740],'')+IsNull([741],'')+IsNull([742],'')+IsNull([743],'')+IsNull([744],'')+IsNull([745],'')+IsNull([746],'')+IsNull([747],'')+IsNull([748],'')+IsNull([749],'')+IsNull([750],'')+IsNull([751],'')+IsNull([752],'')+IsNull([753],'')+IsNull([754],'')+IsNull([755],'')+IsNull([756],'')+IsNull([757],'')+IsNull([758],'')+IsNull([759],'')+IsNull([760],'')+IsNull([761],'')+IsNull([762],'')+IsNull([763],'')+IsNull([764],'')+IsNull([765],'')+IsNull([766],'')+IsNull([767],'')+IsNull([768],'')+IsNull([769],'')+IsNull([770],'')+IsNull([771],'')+IsNull([772],'')+IsNull([773],'')+IsNull([774],'')+IsNull([775],'')+IsNull([776],'')+IsNull([777],'')+IsNull([778],'')+IsNull([779],'')+IsNull([780],'')+IsNull([781],'')+IsNull([782],'')+IsNull([783],'')+IsNull([784],'')+IsNull([785],'')+IsNull([786],'')+IsNull([787],'')+IsNull([788],'')+IsNull([789],'')+IsNull([790],'')+IsNull([791],'')+IsNull([792],'')+IsNull([793],'')+IsNull([794],'')+IsNull([795],'')+IsNull([796],'')+IsNull([797],'')+IsNull([798],'')+IsNull([799],'')+IsNull([800],'')+IsNull([801],'')+
                        --IsNull([802],'')+IsNull([803],'')+IsNull([804],'')+IsNull([805],'')+IsNull([806],'')+IsNull([807],'')+IsNull([808],'')+IsNull([809],'')+IsNull([810],'')+IsNull([811],'')+IsNull([812],'')+IsNull([813],'')+IsNull([814],'')+IsNull([815],'')+IsNull([816],'')+IsNull([817],'')+IsNull([818],'')+IsNull([819],'')+IsNull([820],'')+IsNull([821],'')+IsNull([822],'')+IsNull([823],'')+IsNull([824],'')+IsNull([825],'')+IsNull([826],'')+IsNull([827],'')+IsNull([828],'')+IsNull([829],'')+IsNull([830],'')+IsNull([831],'')+IsNull([832],'')+IsNull([833],'')+IsNull([834],'')+IsNull([835],'')+IsNull([836],'')+IsNull([837],'')+IsNull([838],'')+IsNull([839],'')+IsNull([840],'')+IsNull([841],'')+IsNull([842],'')+IsNull([843],'')+IsNull([844],'')+IsNull([845],'')+IsNull([846],'')+IsNull([847],'')+IsNull([848],'')+IsNull([849],'')+IsNull([850],'')+IsNull([851],'')+IsNull([852],'')+IsNull([853],'')+IsNull([854],'')+IsNull([855],'')+IsNull([856],'')+IsNull([857],'')+IsNull([858],'')+IsNull([859],'')+IsNull([860],'')+IsNull([861],'')+IsNull([862],'')+IsNull([863],'')+IsNull([864],'')+IsNull([865],'')+IsNull([866],'')+IsNull([867],'')+IsNull([868],'')+IsNull([869],'')+IsNull([870],'')+IsNull([871],'')+IsNull([872],'')+IsNull([873],'')+IsNull([874],'')+IsNull([875],'')+IsNull([876],'')+IsNull([877],'')+IsNull([878],'')+IsNull([879],'')+IsNull([880],'')+IsNull([881],'')+IsNull([882],'')+IsNull([883],'')+IsNull([884],'')+IsNull([885],'')+IsNull([886],'')+IsNull([887],'')+IsNull([888],'')+IsNull([889],'')+IsNull([890],'')+IsNull([891],'')+IsNull([892],'')+IsNull([893],'')+IsNull([894],'')+IsNull([895],'')+IsNull([896],'')+IsNull([897],'')+IsNull([898],'')+IsNull([899],'')+IsNull([900],'')+IsNull([901],'')+
                        --IsNull([902],'')+IsNull([903],'')+IsNull([904],'')+IsNull([905],'')+IsNull([906],'')+IsNull([907],'')+IsNull([908],'')+IsNull([909],'')+IsNull([910],'')+IsNull([911],'')+IsNull([912],'')+IsNull([913],'')+IsNull([914],'')+IsNull([915],'')+IsNull([916],'')+IsNull([917],'')+IsNull([918],'')+IsNull([919],'')+IsNull([920],'')+IsNull([921],'')+IsNull([922],'')+IsNull([923],'')+IsNull([924],'')+IsNull([925],'')+IsNull([926],'')+IsNull([927],'')+IsNull([928],'')+IsNull([929],'')+IsNull([930],'')+IsNull([931],'')+IsNull([932],'')+IsNull([933],'')+IsNull([934],'')+IsNull([935],'')+IsNull([936],'')+IsNull([937],'')+IsNull([938],'')+IsNull([939],'')+IsNull([940],'')+IsNull([941],'')+IsNull([942],'')+IsNull([943],'')+IsNull([944],'')+IsNull([945],'')+IsNull([946],'')+IsNull([947],'')+IsNull([948],'')+IsNull([949],'')+IsNull([950],'')+IsNull([951],'')+IsNull([952],'')+IsNull([953],'')+IsNull([954],'')+IsNull([955],'')+IsNull([956],'')+IsNull([957],'')+IsNull([958],'')+IsNull([959],'')+IsNull([960],'')+IsNull([961],'')+IsNull([962],'')+IsNull([963],'')+IsNull([964],'')+IsNull([965],'')+IsNull([966],'')+IsNull([967],'')+IsNull([968],'')+IsNull([969],'')+IsNull([970],'')+IsNull([971],'')+IsNull([972],'')+IsNull([973],'')+IsNull([974],'')+IsNull([975],'')+IsNull([976],'')+IsNull([977],'')+IsNull([978],'')+IsNull([979],'')+IsNull([980],'')+IsNull([981],'')+IsNull([982],'')+IsNull([983],'')+IsNull([984],'')+IsNull([985],'')+IsNull([986],'')+IsNull([987],'')+IsNull([988],'')+IsNull([989],'')+IsNull([990],'')+IsNull([991],'')+IsNull([992],'')+IsNull([993],'')+IsNull([994],'')+IsNull([995],'')+IsNull([996],'')+IsNull([997],'')+IsNull([998],'')+IsNull([999],'')+IsNull([1000],'')
        as COLUMN_NAME_CLEANED
    FROM (
        select *
        from CleanColumn
        ) 
        src
        PIVOT (
            MAX(ScrubbedValue) FOR ValueOrder IN (
            [1],[2],[3],[4],[5],[6],[7],[8],[9],[10],[11],[12],[13],[14],[15],[16],[17],[18],[19],[20],[21],[22],[23],[24],[25],[26],[27],[28],[29],[30],[31],[32],[33],[34],[35],[36],[37],[38],[39],[40],[41],[42],[43],[44],[45],[46],[47],[48],[49],[50],[51],[52],[53],[54],[55],[56],[57],[58],[59],[60],[61],[62],[63],[64],[65],[66],[67],[68],[69],[70],[71],[72],[73],[74],[75],[76],[77],[78],[79],[80],[81],[82],[83],[84],[85],[86],[87],[88],[89],[90],[91],[92],[93],[94],[95],[96],[97],[98],[99],[100],[101],
            [102],[103],[104],[105],[106],[107],[108],[109],[110],[111],[112],[113],[114],[115],[116],[117],[118],[119],[120],[121],[122],[123],[124],[125],[126],[127],[128],[129],[130],[131],[132],[133],[134],[135],[136],[137],[138],[139],[140],[141],[142],[143],[144],[145],[146],[147],[148],[149],[150],[151],[152],[153],[154],[155],[156],[157],[158],[159],[160],[161],[162],[163],[164],[165],[166],[167],[168],[169],[170],[171],[172],[173],[174],[175],[176],[177],[178],[179],[180],[181],[182],[183],[184],[185],[186],[187],[188],[189],[190],[191],[192],[193],[194],[195],[196],[197],[198],[199],[200],[201]
            --[202],[203],[204],[205],[206],[207],[208],[209],[210],[211],[212],[213],[214],[215],[216],[217],[218],[219],[220],[221],[222],[223],[224],[225],[226],[227],[228],[229],[230],[231],[232],[233],[234],[235],[236],[237],[238],[239],[240],[241],[242],[243],[244],[245],[246],[247],[248],[249],[250],[251],[252],[253],[254],[255],[256],[257],[258],[259],[260],[261],[262],[263],[264],[265],[266],[267],[268],[269],[270],[271],[272],[273],[274],[275],[276],[277],[278],[279],[280],[281],[282],[283],[284],[285],[286],[287],[288],[289],[290],[291],[292],[293],[294],[295],[296],[297],[298],[299],[300],[301],
            --[302],[303],[304],[305],[306],[307],[308],[309],[310],[311],[312],[313],[314],[315],[316],[317],[318],[319],[320],[321],[322],[323],[324],[325],[326],[327],[328],[329],[330],[331],[332],[333],[334],[335],[336],[337],[338],[339],[340],[341],[342],[343],[344],[345],[346],[347],[348],[349],[350],[351],[352],[353],[354],[355],[356],[357],[358],[359],[360],[361],[362],[363],[364],[365],[366],[367],[368],[369],[370],[371],[372],[373],[374],[375],[376],[377],[378],[379],[380],[381],[382],[383],[384],[385],[386],[387],[388],[389],[390],[391],[392],[393],[394],[395],[396],[397],[398],[399],[400],[401],
            --[402],[403],[404],[405],[406],[407],[408],[409],[410],[411],[412],[413],[414],[415],[416],[417],[418],[419],[420],[421],[422],[423],[424],[425],[426],[427],[428],[429],[430],[431],[432],[433],[434],[435],[436],[437],[438],[439],[440],[441],[442],[443],[444],[445],[446],[447],[448],[449],[450],[451],[452],[453],[454],[455],[456],[457],[458],[459],[460],[461],[462],[463],[464],[465],[466],[467],[468],[469],[470],[471],[472],[473],[474],[475],[476],[477],[478],[479],[480],[481],[482],[483],[484],[485],[486],[487],[488],[489],[490],[491],[492],[493],[494],[495],[496],[497],[498],[499],[500],[501],
            --[502],[503],[504],[505],[506],[507],[508],[509],[510],[511],[512],[513],[514],[515],[516],[517],[518],[519],[520],[521],[522],[523],[524],[525],[526],[527],[528],[529],[530],[531],[532],[533],[534],[535],[536],[537],[538],[539],[540],[541],[542],[543],[544],[545],[546],[547],[548],[549],[550],[551],[552],[553],[554],[555],[556],[557],[558],[559],[560],[561],[562],[563],[564],[565],[566],[567],[568],[569],[570],[571],[572],[573],[574],[575],[576],[577],[578],[579],[580],[581],[582],[583],[584],[585],[586],[587],[588],[589],[590],[591],[592],[593],[594],[595],[596],[597],[598],[599],[600],[601],
            --[602],[603],[604],[605],[606],[607],[608],[609],[610],[611],[612],[613],[614],[615],[616],[617],[618],[619],[620],[621],[622],[623],[624],[625],[626],[627],[628],[629],[630],[631],[632],[633],[634],[635],[636],[637],[638],[639],[640],[641],[642],[643],[644],[645],[646],[647],[648],[649],[650],[651],[652],[653],[654],[655],[656],[657],[658],[659],[660],[661],[662],[663],[664],[665],[666],[667],[668],[669],[670],[671],[672],[673],[674],[675],[676],[677],[678],[679],[680],[681],[682],[683],[684],[685],[686],[687],[688],[689],[690],[691],[692],[693],[694],[695],[696],[697],[698],[699],[700],[701],
            --[702],[703],[704],[705],[706],[707],[708],[709],[710],[711],[712],[713],[714],[715],[716],[717],[718],[719],[720],[721],[722],[723],[724],[725],[726],[727],[728],[729],[730],[731],[732],[733],[734],[735],[736],[737],[738],[739],[740],[741],[742],[743],[744],[745],[746],[747],[748],[749],[750],[751],[752],[753],[754],[755],[756],[757],[758],[759],[760],[761],[762],[763],[764],[765],[766],[767],[768],[769],[770],[771],[772],[773],[774],[775],[776],[777],[778],[779],[780],[781],[782],[783],[784],[785],[786],[787],[788],[789],[790],[791],[792],[793],[794],[795],[796],[797],[798],[799],[800],[801],
            --[802],[803],[804],[805],[806],[807],[808],[809],[810],[811],[812],[813],[814],[815],[816],[817],[818],[819],[820],[821],[822],[823],[824],[825],[826],[827],[828],[829],[830],[831],[832],[833],[834],[835],[836],[837],[838],[839],[840],[841],[842],[843],[844],[845],[846],[847],[848],[849],[850],[851],[852],[853],[854],[855],[856],[857],[858],[859],[860],[861],[862],[863],[864],[865],[866],[867],[868],[869],[870],[871],[872],[873],[874],[875],[876],[877],[878],[879],[880],[881],[882],[883],[884],[885],[886],[887],[888],[889],[890],[891],[892],[893],[894],[895],[896],[897],[898],[899],[900],[901],
            --[902],[903],[904],[905],[906],[907],[908],[909],[910],[911],[912],[913],[914],[915],[916],[917],[918],[919],[920],[921],[922],[923],[924],[925],[926],[927],[928],[929],[930],[931],[932],[933],[934],[935],[936],[937],[938],[939],[940],[941],[942],[943],[944],[945],[946],[947],[948],[949],[950],[951],[952],[953],[954],[955],[956],[957],[958],[959],[960],[961],[962],[963],[964],[965],[966],[967],[968],[969],[970],[971],[972],[973],[974],[975],[976],[977],[978],[979],[980],[981],[982],[983],[984],[985],[986],[987],[988],[989],[990],[991],[992],[993],[994],[995],[996],[997],[998],[999],[1000]
            )
        ) pvt
    ),
    ColNameDeduped as
    ( 
        Select TABLE_NAME, TABLE_SCHEMA, COLUMN_NAME,  COLUMN_NAME_CLEANED, CAST(ROW_NUMBER() over(partition by COLUMN_NAME_CLEANED Order by COLUMN_NAME_CLEANED) as varchar(255)) ColCounter
        From ColumnNameCleaned
    ),
    UniqueCleanedColName as
    (
	    SELECT TABLE_NAME COLLATE DATABASE_DEFAULT as TABLE_NAME, TABLE_SCHEMA COLLATE DATABASE_DEFAULT as TABLE_SCHEMA, COLUMN_NAME COLLATE DATABASE_DEFAULT COLUMN_NAME, COLUMN_NAME_CLEANED + IIF(ColCounter = 1, '', ColCounter) COLLATE DATABASE_DEFAULT as COLUMN_NAME_CLEANED
	    FROM ColNameDeduped
    ),
    TokenSchema as
    (
	   SELECT
            x.value(N'(./@TABLE_NAME)[1]', N'nvarchar(255)') as TABLE_NAME,
            x.value(N'(./@COLUMN_NAME)[1]', N'nvarchar(255)') as COLUMN_NAME,
	        x.value(N'(./@DataType)[1]', N'nvarchar(512)') as DataType
        FROM
            @data.nodes(N'/TokenSchema/row') AS XTbl(x)
    ),
    SysTblCol AS
    (
        SELECT
            x.value(N'(./@TABLE_NAME)[1]', N'nvarchar(255)') as TABLE_NAME,
            x.value(N'(./@COLUMN_NAME)[1]', N'nvarchar(255)') as COLUMN_NAME,
	        x.value(N'(./@SelectExp)[1]', N'nvarchar(2048)') as SelectExp
        FROM
            @SysTblCol.nodes(N'/SysTblColSelect/row') AS XTbl(x)
    ),
    SysCol AS
    (
        SELECT
            x.value(N'(./@COLUMN_NAME)[1]', N'nvarchar(255)') as COLUMN_NAME,
	        x.value(N'(./@SelectExp)[1]', N'nvarchar(2048)') as SelectExp
        FROM
            @SysCol.nodes(N'/SysColSelect/row') AS XTbl(x)
    ),
    SysDataType AS
    (
        SELECT
            x.value(N'(./@Data_Type)[1]', N'nvarchar(255)') as DataType,
	        x.value(N'(./@SelectExp)[1]', N'nvarchar(2048)') as SelectExp
        FROM
            @SysDataType.nodes(N'/SysDataTypeSelect/row') AS XTbl(x)
    ),
   Virtual AS 
   ( 
        SELECT x.value(N'(./@FlowID)[1]', N'int') AS FlowID,
              x.value(N'(./@SrcDB)[1]', N'nvarchar(255)') AS SrcDB,
              x.value(N'(./@SrcSch)[1]', N'nvarchar(255)') AS SrcSch,
              x.value(N'(./@SrcTbl)[1]', N'nvarchar(255)') AS SrcTbl,
              x.value(N'(./@ColumnName)[1]', N'nvarchar(255)') AS ColumnName,
              x.value(N'(./@OrdPos)[1]', N'nvarchar(255)') AS OrdPos,
              x.value(N'(./@DataType)[1]', N'nvarchar(255)') AS DataType,
              x.value(N'(./@Coll)[1]', N'nvarchar(255)') AS Coll,
              x.value(N'(./@ColClean)[1]', N'nvarchar(255)') AS ColClean,
              x.value(N'(./@IsKey)[1]', N'nvarchar(255)') AS IsKey,
              x.value(N'(./@IsDate)[1]', N'nvarchar(255)') AS [IsDate],
              0 as [IsIgnored],
              x.value(N'(./@DataTypeExp)[1]', N'nvarchar(255)') AS [DataTypeExp],
              x.value(N'(./@SelectExp)[1]', N'nvarchar(2048)') AS SelectExp,
              x.value(N'(./@SrcAddColumnCMD)[1]', N'nvarchar(2048)') AS SrcAddColumnCMD
        FROM @Virtual.nodes(N'/VirtualColumns/row') AS XTbl(x) 
        WHERE x.value(N'(./@FlowID)[1]', N'int') <> ''
    )
";

            return colCleanSql;
        }

        private string BuildColCleanSyn(string database, string schema, string tablename, string ignoreColumn,
    string tokenSchemaForCte, string sysTblColSelectForCte,
    string sysColSelectForCte,
    string sysDataTypeSelectForCte, string virtualSchemaForCte, string withHint)
        {



            var colCleanSql = $@"
; WITH
    data as 
    (
        {tokenSchemaForCte}
    ),
    aSysTblCol as 
    (
        {sysTblColSelectForCte}
    ),
    aSysCol as 
    (
        {sysColSelectForCte}
    ),
    aSysDataType as 
    (
        {sysDataTypeSelectForCte}
    ),
    aVirtual as 
    (
        {virtualSchemaForCte}
    ),
    E1(N) AS ( 
    -- 10 rows
    SELECT 1 UNION ALL SELECT 1 UNION ALL SELECT 1 UNION ALL SELECT 1 UNION ALL 
    SELECT 1 UNION ALL SELECT 1 UNION ALL SELECT 1 UNION ALL SELECT 1 UNION ALL 
    SELECT 1 UNION ALL SELECT 1
    ), 
    E2(N) AS (SELECT 1 FROM E1 a, E1 b), -- 100 rows
    E4(N) AS (SELECT 1 FROM E2 a, E2 b), -- 10,000 rows
    Nums(Number) AS (
        SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) FROM E4
    ),
    spt_values
    AS 
    (
	    SELECT Number 
        FROM Nums
        WHERE Number <= 201
    ),
    SrcColumnDS as 
    (
	    SELECT  TABLE_NAME,  TABLE_SCHEMA,  COLUMN_NAME, ORDINAL_POSITION
	    FROM   [{database}].INFORMATION_SCHEMA.COLUMNS with({withHint})
	    WHERE TABLE_SCHEMA = '{schema}' AND TABLE_NAME = '{tablename}'
        AND COLUMN_NAME not in ({ignoreColumn})
    ),
    CleanColumn as 
    (
        select 
            P.Number as ValueOrder,
            isnull ( substring ( t.COLUMN_NAME , number , 1 ) , '' ) as ScrubbedValue,
            t.TABLE_SCHEMA, 
		    t.TABLE_NAME, 
		    t.COLUMN_NAME,
            t.ORDINAL_POSITION
        from
            SrcColumnDS t
            left join spt_values P
                on P.number between 1 and len(t.COLUMN_NAME)
        where
            PatIndex('%{_colCleanupSqlRegExp}%', substring(t.COLUMN_NAME,P.number,1) ) = 0
    ),
    ColumnNameCleaned as
    (
    SELECT
        TABLE_NAME, TABLE_SCHEMA, COLUMN_NAME, 
        IsNull([1],'')+IsNull([2],'')+IsNull([3],'')+IsNull([4],'')+IsNull([5],'')+IsNull([6],'')+IsNull([7],'')+IsNull([8],'')+IsNull([9],'')+IsNull([10],'')+IsNull([11],'')+IsNull([12],'')+IsNull([13],'')+IsNull([14],'')+IsNull([15],'')+IsNull([16],'')+IsNull([17],'')+IsNull([18],'')+IsNull([19],'')+IsNull([20],'')+IsNull([21],'')+IsNull([22],'')+IsNull([23],'')+IsNull([24],'')+IsNull([25],'')+IsNull([26],'')+IsNull([27],'')+IsNull([28],'')+IsNull([29],'')+IsNull([30],'')+IsNull([31],'')+IsNull([32],'')+IsNull([33],'')+IsNull([34],'')+IsNull([35],'')+IsNull([36],'')+IsNull([37],'')+IsNull([38],'')+IsNull([39],'')+IsNull([40],'')+IsNull([41],'')+IsNull([42],'')+IsNull([43],'')+IsNull([44],'')+IsNull([45],'')+IsNull([46],'')+IsNull([47],'')+IsNull([48],'')+IsNull([49],'')+IsNull([50],'')+IsNull([51],'')+IsNull([52],'')+IsNull([53],'')+IsNull([54],'')+IsNull([55],'')+IsNull([56],'')+IsNull([57],'')+IsNull([58],'')+IsNull([59],'')+IsNull([60],'')+IsNull([61],'')+IsNull([62],'')+IsNull([63],'')+IsNull([64],'')+IsNull([65],'')+IsNull([66],'')+IsNull([67],'')+IsNull([68],'')+IsNull([69],'')+IsNull([70],'')+IsNull([71],'')+IsNull([72],'')+IsNull([73],'')+IsNull([74],'')+IsNull([75],'')+IsNull([76],'')+IsNull([77],'')+IsNull([78],'')+IsNull([79],'')+IsNull([80],'')+IsNull([81],'')+IsNull([82],'')+IsNull([83],'')+IsNull([84],'')+IsNull([85],'')+IsNull([86],'')+IsNull([87],'')+IsNull([88],'')+IsNull([89],'')+IsNull([90],'')+IsNull([91],'')+IsNull([92],'')+IsNull([93],'')+IsNull([94],'')+IsNull([95],'')+IsNull([96],'')+IsNull([97],'')+IsNull([98],'')+IsNull([99],'')+IsNull([100],'')+IsNull([101],'')+
                        IsNull([102],'')+IsNull([103],'')+IsNull([104],'')+IsNull([105],'')+IsNull([106],'')+IsNull([107],'')+IsNull([108],'')+IsNull([109],'')+IsNull([110],'')+IsNull([111],'')+IsNull([112],'')+IsNull([113],'')+IsNull([114],'')+IsNull([115],'')+IsNull([116],'')+IsNull([117],'')+IsNull([118],'')+IsNull([119],'')+IsNull([120],'')+IsNull([121],'')+IsNull([122],'')+IsNull([123],'')+IsNull([124],'')+IsNull([125],'')+IsNull([126],'')+IsNull([127],'')+IsNull([128],'')+IsNull([129],'')+IsNull([130],'')+IsNull([131],'')+IsNull([132],'')+IsNull([133],'')+IsNull([134],'')+IsNull([135],'')+IsNull([136],'')+IsNull([137],'')+IsNull([138],'')+IsNull([139],'')+IsNull([140],'')+IsNull([141],'')+IsNull([142],'')+IsNull([143],'')+IsNull([144],'')+IsNull([145],'')+IsNull([146],'')+IsNull([147],'')+IsNull([148],'')+IsNull([149],'')+IsNull([150],'')+IsNull([151],'')+IsNull([152],'')+IsNull([153],'')+IsNull([154],'')+IsNull([155],'')+IsNull([156],'')+IsNull([157],'')+IsNull([158],'')+IsNull([159],'')+IsNull([160],'')+IsNull([161],'')+IsNull([162],'')+IsNull([163],'')+IsNull([164],'')+IsNull([165],'')+IsNull([166],'')+IsNull([167],'')+IsNull([168],'')+IsNull([169],'')+IsNull([170],'')+IsNull([171],'')+IsNull([172],'')+IsNull([173],'')+IsNull([174],'')+IsNull([175],'')+IsNull([176],'')+IsNull([177],'')+IsNull([178],'')+IsNull([179],'')+IsNull([180],'')+IsNull([181],'')+IsNull([182],'')+IsNull([183],'')+IsNull([184],'')+IsNull([185],'')+IsNull([186],'')+IsNull([187],'')+IsNull([188],'')+IsNull([189],'')+IsNull([190],'')+IsNull([191],'')+IsNull([192],'')+IsNull([193],'')+IsNull([194],'')+IsNull([195],'')+IsNull([196],'')+IsNull([197],'')+IsNull([198],'')+IsNull([199],'')+IsNull([200],'')+IsNull([201],'')
                        --IsNull([202],'')+IsNull([203],'')+IsNull([204],'')+IsNull([205],'')+IsNull([206],'')+IsNull([207],'')+IsNull([208],'')+IsNull([209],'')+IsNull([210],'')+IsNull([211],'')+IsNull([212],'')+IsNull([213],'')+IsNull([214],'')+IsNull([215],'')+IsNull([216],'')+IsNull([217],'')+IsNull([218],'')+IsNull([219],'')+IsNull([220],'')+IsNull([221],'')+IsNull([222],'')+IsNull([223],'')+IsNull([224],'')+IsNull([225],'')+IsNull([226],'')+IsNull([227],'')+IsNull([228],'')+IsNull([229],'')+IsNull([230],'')+IsNull([231],'')+IsNull([232],'')+IsNull([233],'')+IsNull([234],'')+IsNull([235],'')+IsNull([236],'')+IsNull([237],'')+IsNull([238],'')+IsNull([239],'')+IsNull([240],'')+IsNull([241],'')+IsNull([242],'')+IsNull([243],'')+IsNull([244],'')+IsNull([245],'')+IsNull([246],'')+IsNull([247],'')+IsNull([248],'')+IsNull([249],'')+IsNull([250],'')+IsNull([251],'')+IsNull([252],'')+IsNull([253],'')+IsNull([254],'')+IsNull([255],'')+IsNull([256],'')+IsNull([257],'')+IsNull([258],'')+IsNull([259],'')+IsNull([260],'')+IsNull([261],'')+IsNull([262],'')+IsNull([263],'')+IsNull([264],'')+IsNull([265],'')+IsNull([266],'')+IsNull([267],'')+IsNull([268],'')+IsNull([269],'')+IsNull([270],'')+IsNull([271],'')+IsNull([272],'')+IsNull([273],'')+IsNull([274],'')+IsNull([275],'')+IsNull([276],'')+IsNull([277],'')+IsNull([278],'')+IsNull([279],'')+IsNull([280],'')+IsNull([281],'')+IsNull([282],'')+IsNull([283],'')+IsNull([284],'')+IsNull([285],'')+IsNull([286],'')+IsNull([287],'')+IsNull([288],'')+IsNull([289],'')+IsNull([290],'')+IsNull([291],'')+IsNull([292],'')+IsNull([293],'')+IsNull([294],'')+IsNull([295],'')+IsNull([296],'')+IsNull([297],'')+IsNull([298],'')+IsNull([299],'')+IsNull([300],'')+IsNull([301],'')+
                        --IsNull([302],'')+IsNull([303],'')+IsNull([304],'')+IsNull([305],'')+IsNull([306],'')+IsNull([307],'')+IsNull([308],'')+IsNull([309],'')+IsNull([310],'')+IsNull([311],'')+IsNull([312],'')+IsNull([313],'')+IsNull([314],'')+IsNull([315],'')+IsNull([316],'')+IsNull([317],'')+IsNull([318],'')+IsNull([319],'')+IsNull([320],'')+IsNull([321],'')+IsNull([322],'')+IsNull([323],'')+IsNull([324],'')+IsNull([325],'')+IsNull([326],'')+IsNull([327],'')+IsNull([328],'')+IsNull([329],'')+IsNull([330],'')+IsNull([331],'')+IsNull([332],'')+IsNull([333],'')+IsNull([334],'')+IsNull([335],'')+IsNull([336],'')+IsNull([337],'')+IsNull([338],'')+IsNull([339],'')+IsNull([340],'')+IsNull([341],'')+IsNull([342],'')+IsNull([343],'')+IsNull([344],'')+IsNull([345],'')+IsNull([346],'')+IsNull([347],'')+IsNull([348],'')+IsNull([349],'')+IsNull([350],'')+IsNull([351],'')+IsNull([352],'')+IsNull([353],'')+IsNull([354],'')+IsNull([355],'')+IsNull([356],'')+IsNull([357],'')+IsNull([358],'')+IsNull([359],'')+IsNull([360],'')+IsNull([361],'')+IsNull([362],'')+IsNull([363],'')+IsNull([364],'')+IsNull([365],'')+IsNull([366],'')+IsNull([367],'')+IsNull([368],'')+IsNull([369],'')+IsNull([370],'')+IsNull([371],'')+IsNull([372],'')+IsNull([373],'')+IsNull([374],'')+IsNull([375],'')+IsNull([376],'')+IsNull([377],'')+IsNull([378],'')+IsNull([379],'')+IsNull([380],'')+IsNull([381],'')+IsNull([382],'')+IsNull([383],'')+IsNull([384],'')+IsNull([385],'')+IsNull([386],'')+IsNull([387],'')+IsNull([388],'')+IsNull([389],'')+IsNull([390],'')+IsNull([391],'')+IsNull([392],'')+IsNull([393],'')+IsNull([394],'')+IsNull([395],'')+IsNull([396],'')+IsNull([397],'')+IsNull([398],'')+IsNull([399],'')+IsNull([400],'')+IsNull([401],'')+
                        --IsNull([402],'')+IsNull([403],'')+IsNull([404],'')+IsNull([405],'')+IsNull([406],'')+IsNull([407],'')+IsNull([408],'')+IsNull([409],'')+IsNull([410],'')+IsNull([411],'')+IsNull([412],'')+IsNull([413],'')+IsNull([414],'')+IsNull([415],'')+IsNull([416],'')+IsNull([417],'')+IsNull([418],'')+IsNull([419],'')+IsNull([420],'')+IsNull([421],'')+IsNull([422],'')+IsNull([423],'')+IsNull([424],'')+IsNull([425],'')+IsNull([426],'')+IsNull([427],'')+IsNull([428],'')+IsNull([429],'')+IsNull([430],'')+IsNull([431],'')+IsNull([432],'')+IsNull([433],'')+IsNull([434],'')+IsNull([435],'')+IsNull([436],'')+IsNull([437],'')+IsNull([438],'')+IsNull([439],'')+IsNull([440],'')+IsNull([441],'')+IsNull([442],'')+IsNull([443],'')+IsNull([444],'')+IsNull([445],'')+IsNull([446],'')+IsNull([447],'')+IsNull([448],'')+IsNull([449],'')+IsNull([450],'')+IsNull([451],'')+IsNull([452],'')+IsNull([453],'')+IsNull([454],'')+IsNull([455],'')+IsNull([456],'')+IsNull([457],'')+IsNull([458],'')+IsNull([459],'')+IsNull([460],'')+IsNull([461],'')+IsNull([462],'')+IsNull([463],'')+IsNull([464],'')+IsNull([465],'')+IsNull([466],'')+IsNull([467],'')+IsNull([468],'')+IsNull([469],'')+IsNull([470],'')+IsNull([471],'')+IsNull([472],'')+IsNull([473],'')+IsNull([474],'')+IsNull([475],'')+IsNull([476],'')+IsNull([477],'')+IsNull([478],'')+IsNull([479],'')+IsNull([480],'')+IsNull([481],'')+IsNull([482],'')+IsNull([483],'')+IsNull([484],'')+IsNull([485],'')+IsNull([486],'')+IsNull([487],'')+IsNull([488],'')+IsNull([489],'')+IsNull([490],'')+IsNull([491],'')+IsNull([492],'')+IsNull([493],'')+IsNull([494],'')+IsNull([495],'')+IsNull([496],'')+IsNull([497],'')+IsNull([498],'')+IsNull([499],'')+IsNull([500],'')+IsNull([501],'')+
                        --IsNull([502],'')+IsNull([503],'')+IsNull([504],'')+IsNull([505],'')+IsNull([506],'')+IsNull([507],'')+IsNull([508],'')+IsNull([509],'')+IsNull([510],'')+IsNull([511],'')+IsNull([512],'')+IsNull([513],'')+IsNull([514],'')+IsNull([515],'')+IsNull([516],'')+IsNull([517],'')+IsNull([518],'')+IsNull([519],'')+IsNull([520],'')+IsNull([521],'')+IsNull([522],'')+IsNull([523],'')+IsNull([524],'')+IsNull([525],'')+IsNull([526],'')+IsNull([527],'')+IsNull([528],'')+IsNull([529],'')+IsNull([530],'')+IsNull([531],'')+IsNull([532],'')+IsNull([533],'')+IsNull([534],'')+IsNull([535],'')+IsNull([536],'')+IsNull([537],'')+IsNull([538],'')+IsNull([539],'')+IsNull([540],'')+IsNull([541],'')+IsNull([542],'')+IsNull([543],'')+IsNull([544],'')+IsNull([545],'')+IsNull([546],'')+IsNull([547],'')+IsNull([548],'')+IsNull([549],'')+IsNull([550],'')+IsNull([551],'')+IsNull([552],'')+IsNull([553],'')+IsNull([554],'')+IsNull([555],'')+IsNull([556],'')+IsNull([557],'')+IsNull([558],'')+IsNull([559],'')+IsNull([560],'')+IsNull([561],'')+IsNull([562],'')+IsNull([563],'')+IsNull([564],'')+IsNull([565],'')+IsNull([566],'')+IsNull([567],'')+IsNull([568],'')+IsNull([569],'')+IsNull([570],'')+IsNull([571],'')+IsNull([572],'')+IsNull([573],'')+IsNull([574],'')+IsNull([575],'')+IsNull([576],'')+IsNull([577],'')+IsNull([578],'')+IsNull([579],'')+IsNull([580],'')+IsNull([581],'')+IsNull([582],'')+IsNull([583],'')+IsNull([584],'')+IsNull([585],'')+IsNull([586],'')+IsNull([587],'')+IsNull([588],'')+IsNull([589],'')+IsNull([590],'')+IsNull([591],'')+IsNull([592],'')+IsNull([593],'')+IsNull([594],'')+IsNull([595],'')+IsNull([596],'')+IsNull([597],'')+IsNull([598],'')+IsNull([599],'')+IsNull([600],'')+IsNull([601],'')+
                        --IsNull([602],'')+IsNull([603],'')+IsNull([604],'')+IsNull([605],'')+IsNull([606],'')+IsNull([607],'')+IsNull([608],'')+IsNull([609],'')+IsNull([610],'')+IsNull([611],'')+IsNull([612],'')+IsNull([613],'')+IsNull([614],'')+IsNull([615],'')+IsNull([616],'')+IsNull([617],'')+IsNull([618],'')+IsNull([619],'')+IsNull([620],'')+IsNull([621],'')+IsNull([622],'')+IsNull([623],'')+IsNull([624],'')+IsNull([625],'')+IsNull([626],'')+IsNull([627],'')+IsNull([628],'')+IsNull([629],'')+IsNull([630],'')+IsNull([631],'')+IsNull([632],'')+IsNull([633],'')+IsNull([634],'')+IsNull([635],'')+IsNull([636],'')+IsNull([637],'')+IsNull([638],'')+IsNull([639],'')+IsNull([640],'')+IsNull([641],'')+IsNull([642],'')+IsNull([643],'')+IsNull([644],'')+IsNull([645],'')+IsNull([646],'')+IsNull([647],'')+IsNull([648],'')+IsNull([649],'')+IsNull([650],'')+IsNull([651],'')+IsNull([652],'')+IsNull([653],'')+IsNull([654],'')+IsNull([655],'')+IsNull([656],'')+IsNull([657],'')+IsNull([658],'')+IsNull([659],'')+IsNull([660],'')+IsNull([661],'')+IsNull([662],'')+IsNull([663],'')+IsNull([664],'')+IsNull([665],'')+IsNull([666],'')+IsNull([667],'')+IsNull([668],'')+IsNull([669],'')+IsNull([670],'')+IsNull([671],'')+IsNull([672],'')+IsNull([673],'')+IsNull([674],'')+IsNull([675],'')+IsNull([676],'')+IsNull([677],'')+IsNull([678],'')+IsNull([679],'')+IsNull([680],'')+IsNull([681],'')+IsNull([682],'')+IsNull([683],'')+IsNull([684],'')+IsNull([685],'')+IsNull([686],'')+IsNull([687],'')+IsNull([688],'')+IsNull([689],'')+IsNull([690],'')+IsNull([691],'')+IsNull([692],'')+IsNull([693],'')+IsNull([694],'')+IsNull([695],'')+IsNull([696],'')+IsNull([697],'')+IsNull([698],'')+IsNull([699],'')+IsNull([700],'')+IsNull([701],'')+
                        --IsNull([702],'')+IsNull([703],'')+IsNull([704],'')+IsNull([705],'')+IsNull([706],'')+IsNull([707],'')+IsNull([708],'')+IsNull([709],'')+IsNull([710],'')+IsNull([711],'')+IsNull([712],'')+IsNull([713],'')+IsNull([714],'')+IsNull([715],'')+IsNull([716],'')+IsNull([717],'')+IsNull([718],'')+IsNull([719],'')+IsNull([720],'')+IsNull([721],'')+IsNull([722],'')+IsNull([723],'')+IsNull([724],'')+IsNull([725],'')+IsNull([726],'')+IsNull([727],'')+IsNull([728],'')+IsNull([729],'')+IsNull([730],'')+IsNull([731],'')+IsNull([732],'')+IsNull([733],'')+IsNull([734],'')+IsNull([735],'')+IsNull([736],'')+IsNull([737],'')+IsNull([738],'')+IsNull([739],'')+IsNull([740],'')+IsNull([741],'')+IsNull([742],'')+IsNull([743],'')+IsNull([744],'')+IsNull([745],'')+IsNull([746],'')+IsNull([747],'')+IsNull([748],'')+IsNull([749],'')+IsNull([750],'')+IsNull([751],'')+IsNull([752],'')+IsNull([753],'')+IsNull([754],'')+IsNull([755],'')+IsNull([756],'')+IsNull([757],'')+IsNull([758],'')+IsNull([759],'')+IsNull([760],'')+IsNull([761],'')+IsNull([762],'')+IsNull([763],'')+IsNull([764],'')+IsNull([765],'')+IsNull([766],'')+IsNull([767],'')+IsNull([768],'')+IsNull([769],'')+IsNull([770],'')+IsNull([771],'')+IsNull([772],'')+IsNull([773],'')+IsNull([774],'')+IsNull([775],'')+IsNull([776],'')+IsNull([777],'')+IsNull([778],'')+IsNull([779],'')+IsNull([780],'')+IsNull([781],'')+IsNull([782],'')+IsNull([783],'')+IsNull([784],'')+IsNull([785],'')+IsNull([786],'')+IsNull([787],'')+IsNull([788],'')+IsNull([789],'')+IsNull([790],'')+IsNull([791],'')+IsNull([792],'')+IsNull([793],'')+IsNull([794],'')+IsNull([795],'')+IsNull([796],'')+IsNull([797],'')+IsNull([798],'')+IsNull([799],'')+IsNull([800],'')+IsNull([801],'')+
                        --IsNull([802],'')+IsNull([803],'')+IsNull([804],'')+IsNull([805],'')+IsNull([806],'')+IsNull([807],'')+IsNull([808],'')+IsNull([809],'')+IsNull([810],'')+IsNull([811],'')+IsNull([812],'')+IsNull([813],'')+IsNull([814],'')+IsNull([815],'')+IsNull([816],'')+IsNull([817],'')+IsNull([818],'')+IsNull([819],'')+IsNull([820],'')+IsNull([821],'')+IsNull([822],'')+IsNull([823],'')+IsNull([824],'')+IsNull([825],'')+IsNull([826],'')+IsNull([827],'')+IsNull([828],'')+IsNull([829],'')+IsNull([830],'')+IsNull([831],'')+IsNull([832],'')+IsNull([833],'')+IsNull([834],'')+IsNull([835],'')+IsNull([836],'')+IsNull([837],'')+IsNull([838],'')+IsNull([839],'')+IsNull([840],'')+IsNull([841],'')+IsNull([842],'')+IsNull([843],'')+IsNull([844],'')+IsNull([845],'')+IsNull([846],'')+IsNull([847],'')+IsNull([848],'')+IsNull([849],'')+IsNull([850],'')+IsNull([851],'')+IsNull([852],'')+IsNull([853],'')+IsNull([854],'')+IsNull([855],'')+IsNull([856],'')+IsNull([857],'')+IsNull([858],'')+IsNull([859],'')+IsNull([860],'')+IsNull([861],'')+IsNull([862],'')+IsNull([863],'')+IsNull([864],'')+IsNull([865],'')+IsNull([866],'')+IsNull([867],'')+IsNull([868],'')+IsNull([869],'')+IsNull([870],'')+IsNull([871],'')+IsNull([872],'')+IsNull([873],'')+IsNull([874],'')+IsNull([875],'')+IsNull([876],'')+IsNull([877],'')+IsNull([878],'')+IsNull([879],'')+IsNull([880],'')+IsNull([881],'')+IsNull([882],'')+IsNull([883],'')+IsNull([884],'')+IsNull([885],'')+IsNull([886],'')+IsNull([887],'')+IsNull([888],'')+IsNull([889],'')+IsNull([890],'')+IsNull([891],'')+IsNull([892],'')+IsNull([893],'')+IsNull([894],'')+IsNull([895],'')+IsNull([896],'')+IsNull([897],'')+IsNull([898],'')+IsNull([899],'')+IsNull([900],'')+IsNull([901],'')+
                        --IsNull([902],'')+IsNull([903],'')+IsNull([904],'')+IsNull([905],'')+IsNull([906],'')+IsNull([907],'')+IsNull([908],'')+IsNull([909],'')+IsNull([910],'')+IsNull([911],'')+IsNull([912],'')+IsNull([913],'')+IsNull([914],'')+IsNull([915],'')+IsNull([916],'')+IsNull([917],'')+IsNull([918],'')+IsNull([919],'')+IsNull([920],'')+IsNull([921],'')+IsNull([922],'')+IsNull([923],'')+IsNull([924],'')+IsNull([925],'')+IsNull([926],'')+IsNull([927],'')+IsNull([928],'')+IsNull([929],'')+IsNull([930],'')+IsNull([931],'')+IsNull([932],'')+IsNull([933],'')+IsNull([934],'')+IsNull([935],'')+IsNull([936],'')+IsNull([937],'')+IsNull([938],'')+IsNull([939],'')+IsNull([940],'')+IsNull([941],'')+IsNull([942],'')+IsNull([943],'')+IsNull([944],'')+IsNull([945],'')+IsNull([946],'')+IsNull([947],'')+IsNull([948],'')+IsNull([949],'')+IsNull([950],'')+IsNull([951],'')+IsNull([952],'')+IsNull([953],'')+IsNull([954],'')+IsNull([955],'')+IsNull([956],'')+IsNull([957],'')+IsNull([958],'')+IsNull([959],'')+IsNull([960],'')+IsNull([961],'')+IsNull([962],'')+IsNull([963],'')+IsNull([964],'')+IsNull([965],'')+IsNull([966],'')+IsNull([967],'')+IsNull([968],'')+IsNull([969],'')+IsNull([970],'')+IsNull([971],'')+IsNull([972],'')+IsNull([973],'')+IsNull([974],'')+IsNull([975],'')+IsNull([976],'')+IsNull([977],'')+IsNull([978],'')+IsNull([979],'')+IsNull([980],'')+IsNull([981],'')+IsNull([982],'')+IsNull([983],'')+IsNull([984],'')+IsNull([985],'')+IsNull([986],'')+IsNull([987],'')+IsNull([988],'')+IsNull([989],'')+IsNull([990],'')+IsNull([991],'')+IsNull([992],'')+IsNull([993],'')+IsNull([994],'')+IsNull([995],'')+IsNull([996],'')+IsNull([997],'')+IsNull([998],'')+IsNull([999],'')+IsNull([1000],'')
        as COLUMN_NAME_CLEANED
    FROM (
        select *
        from CleanColumn
        ) 
        src
        PIVOT (
            MAX(ScrubbedValue) FOR ValueOrder IN (
            [1],[2],[3],[4],[5],[6],[7],[8],[9],[10],[11],[12],[13],[14],[15],[16],[17],[18],[19],[20],[21],[22],[23],[24],[25],[26],[27],[28],[29],[30],[31],[32],[33],[34],[35],[36],[37],[38],[39],[40],[41],[42],[43],[44],[45],[46],[47],[48],[49],[50],[51],[52],[53],[54],[55],[56],[57],[58],[59],[60],[61],[62],[63],[64],[65],[66],[67],[68],[69],[70],[71],[72],[73],[74],[75],[76],[77],[78],[79],[80],[81],[82],[83],[84],[85],[86],[87],[88],[89],[90],[91],[92],[93],[94],[95],[96],[97],[98],[99],[100],[101],
            [102],[103],[104],[105],[106],[107],[108],[109],[110],[111],[112],[113],[114],[115],[116],[117],[118],[119],[120],[121],[122],[123],[124],[125],[126],[127],[128],[129],[130],[131],[132],[133],[134],[135],[136],[137],[138],[139],[140],[141],[142],[143],[144],[145],[146],[147],[148],[149],[150],[151],[152],[153],[154],[155],[156],[157],[158],[159],[160],[161],[162],[163],[164],[165],[166],[167],[168],[169],[170],[171],[172],[173],[174],[175],[176],[177],[178],[179],[180],[181],[182],[183],[184],[185],[186],[187],[188],[189],[190],[191],[192],[193],[194],[195],[196],[197],[198],[199],[200],[201]
            --[202],[203],[204],[205],[206],[207],[208],[209],[210],[211],[212],[213],[214],[215],[216],[217],[218],[219],[220],[221],[222],[223],[224],[225],[226],[227],[228],[229],[230],[231],[232],[233],[234],[235],[236],[237],[238],[239],[240],[241],[242],[243],[244],[245],[246],[247],[248],[249],[250],[251],[252],[253],[254],[255],[256],[257],[258],[259],[260],[261],[262],[263],[264],[265],[266],[267],[268],[269],[270],[271],[272],[273],[274],[275],[276],[277],[278],[279],[280],[281],[282],[283],[284],[285],[286],[287],[288],[289],[290],[291],[292],[293],[294],[295],[296],[297],[298],[299],[300],[301],
            --[302],[303],[304],[305],[306],[307],[308],[309],[310],[311],[312],[313],[314],[315],[316],[317],[318],[319],[320],[321],[322],[323],[324],[325],[326],[327],[328],[329],[330],[331],[332],[333],[334],[335],[336],[337],[338],[339],[340],[341],[342],[343],[344],[345],[346],[347],[348],[349],[350],[351],[352],[353],[354],[355],[356],[357],[358],[359],[360],[361],[362],[363],[364],[365],[366],[367],[368],[369],[370],[371],[372],[373],[374],[375],[376],[377],[378],[379],[380],[381],[382],[383],[384],[385],[386],[387],[388],[389],[390],[391],[392],[393],[394],[395],[396],[397],[398],[399],[400],[401],
            --[402],[403],[404],[405],[406],[407],[408],[409],[410],[411],[412],[413],[414],[415],[416],[417],[418],[419],[420],[421],[422],[423],[424],[425],[426],[427],[428],[429],[430],[431],[432],[433],[434],[435],[436],[437],[438],[439],[440],[441],[442],[443],[444],[445],[446],[447],[448],[449],[450],[451],[452],[453],[454],[455],[456],[457],[458],[459],[460],[461],[462],[463],[464],[465],[466],[467],[468],[469],[470],[471],[472],[473],[474],[475],[476],[477],[478],[479],[480],[481],[482],[483],[484],[485],[486],[487],[488],[489],[490],[491],[492],[493],[494],[495],[496],[497],[498],[499],[500],[501],
            --[502],[503],[504],[505],[506],[507],[508],[509],[510],[511],[512],[513],[514],[515],[516],[517],[518],[519],[520],[521],[522],[523],[524],[525],[526],[527],[528],[529],[530],[531],[532],[533],[534],[535],[536],[537],[538],[539],[540],[541],[542],[543],[544],[545],[546],[547],[548],[549],[550],[551],[552],[553],[554],[555],[556],[557],[558],[559],[560],[561],[562],[563],[564],[565],[566],[567],[568],[569],[570],[571],[572],[573],[574],[575],[576],[577],[578],[579],[580],[581],[582],[583],[584],[585],[586],[587],[588],[589],[590],[591],[592],[593],[594],[595],[596],[597],[598],[599],[600],[601],
            --[602],[603],[604],[605],[606],[607],[608],[609],[610],[611],[612],[613],[614],[615],[616],[617],[618],[619],[620],[621],[622],[623],[624],[625],[626],[627],[628],[629],[630],[631],[632],[633],[634],[635],[636],[637],[638],[639],[640],[641],[642],[643],[644],[645],[646],[647],[648],[649],[650],[651],[652],[653],[654],[655],[656],[657],[658],[659],[660],[661],[662],[663],[664],[665],[666],[667],[668],[669],[670],[671],[672],[673],[674],[675],[676],[677],[678],[679],[680],[681],[682],[683],[684],[685],[686],[687],[688],[689],[690],[691],[692],[693],[694],[695],[696],[697],[698],[699],[700],[701],
            --[702],[703],[704],[705],[706],[707],[708],[709],[710],[711],[712],[713],[714],[715],[716],[717],[718],[719],[720],[721],[722],[723],[724],[725],[726],[727],[728],[729],[730],[731],[732],[733],[734],[735],[736],[737],[738],[739],[740],[741],[742],[743],[744],[745],[746],[747],[748],[749],[750],[751],[752],[753],[754],[755],[756],[757],[758],[759],[760],[761],[762],[763],[764],[765],[766],[767],[768],[769],[770],[771],[772],[773],[774],[775],[776],[777],[778],[779],[780],[781],[782],[783],[784],[785],[786],[787],[788],[789],[790],[791],[792],[793],[794],[795],[796],[797],[798],[799],[800],[801],
            --[802],[803],[804],[805],[806],[807],[808],[809],[810],[811],[812],[813],[814],[815],[816],[817],[818],[819],[820],[821],[822],[823],[824],[825],[826],[827],[828],[829],[830],[831],[832],[833],[834],[835],[836],[837],[838],[839],[840],[841],[842],[843],[844],[845],[846],[847],[848],[849],[850],[851],[852],[853],[854],[855],[856],[857],[858],[859],[860],[861],[862],[863],[864],[865],[866],[867],[868],[869],[870],[871],[872],[873],[874],[875],[876],[877],[878],[879],[880],[881],[882],[883],[884],[885],[886],[887],[888],[889],[890],[891],[892],[893],[894],[895],[896],[897],[898],[899],[900],[901],
            --[902],[903],[904],[905],[906],[907],[908],[909],[910],[911],[912],[913],[914],[915],[916],[917],[918],[919],[920],[921],[922],[923],[924],[925],[926],[927],[928],[929],[930],[931],[932],[933],[934],[935],[936],[937],[938],[939],[940],[941],[942],[943],[944],[945],[946],[947],[948],[949],[950],[951],[952],[953],[954],[955],[956],[957],[958],[959],[960],[961],[962],[963],[964],[965],[966],[967],[968],[969],[970],[971],[972],[973],[974],[975],[976],[977],[978],[979],[980],[981],[982],[983],[984],[985],[986],[987],[988],[989],[990],[991],[992],[993],[994],[995],[996],[997],[998],[999],[1000]
            )
        ) pvt
    ),
    ColNameDeduped as
    ( 
        Select TABLE_NAME, TABLE_SCHEMA, COLUMN_NAME,  COLUMN_NAME_CLEANED, CAST(ROW_NUMBER() over(partition by COLUMN_NAME_CLEANED Order by COLUMN_NAME_CLEANED) as varchar(255)) ColCounter
        From ColumnNameCleaned
    ),
    UniqueCleanedColName as
    (
	    SELECT TABLE_NAME COLLATE DATABASE_DEFAULT as TABLE_NAME, TABLE_SCHEMA COLLATE DATABASE_DEFAULT as TABLE_SCHEMA, COLUMN_NAME COLLATE DATABASE_DEFAULT COLUMN_NAME, COLUMN_NAME_CLEANED + CASE WHEN ColCounter = 1 THEN '' ELSE ColCounter END COLLATE DATABASE_DEFAULT as COLUMN_NAME_CLEANED
	    FROM ColNameDeduped
    ),
    TokenSchema as
    (
	   SELECT  TABLE_NAME, COLUMN_NAME, DataType
        FROM data
    ),
    SysTblCol AS
    (
        SELECT
            TABLE_NAME,
            COLUMN_NAME,
	        SelectExp
        FROM aSysTblCol
    ),
    SysCol AS
    (
        SELECT COLUMN_NAME, SelectExp
        FROM aSysCol
    ),
    SysDataType AS
    (
        SELECT
             DataType,
	        SelectExp
        FROM
            aSysDataType
    ),
   Virtual AS 
   ( 
        SELECT FlowID,
              SrcDB,
              SrcSch,
              SrcTbl,
               ColumnName,
               OrdPos,
               DataType,
               Coll,
               ColClean,
              IsKey,
               [IsDate],
               0 as [IsIgnored],
               [DataTypeExp],
               SelectExp,
              SrcAddColumnCMD
        FROM aVirtual
        WHERE FlowID <> 0
    )
";

            return colCleanSql;
        }

        private string BuildCreateCmd(string srcDatabase, string srcSchema, string srcObject,
            string trgDatabase, string trgSchema, string trgObject, bool createIndexes,
            string keyColumnsParsed, string dateColumn, string ignoreColumns, string tokenSchemaXml,
            string sysTblColSelectXml,
            string sysColSelectXml,
            string sysDataTypeSelectXml, string virtualSchemaXml, string withHint, string incrementalColumns)
        {
            if (createIndexes == false) keyColumnsParsed = "''";

            var rValue = "";
            var createCmd = BuildColClean(srcDatabase, srcSchema, srcObject, ignoreColumns, tokenSchemaXml,
                sysTblColSelectXml, sysColSelectXml, sysDataTypeSelectXml, virtualSchemaXml, withHint);

            //
            createCmd = createCmd + $@" 
SELECT 'CREATE TABLE ' +  Quotename('{trgDatabase}') + '.' + Quotename('{trgSchema}') + '.' + Quotename('{trgObject}') + ' ('
        + CASE WHEN LEN('{_identityColumn}') = 0 THEN '' ELSE '{_identityColumn} INT IDENTITY(1,1) NOT NULL,' END
        + Substring(Replace(Replace(Replace(Replace(Replace(o.List,'&amp;','&'),'&gt;','>'),'&lt;','<'),'&apos;',char(39)),'&quot;',char(34)), 2, Len(o.List)) --Regular Columns
        + IsNull(Replace(Replace(Replace(Replace(Replace(v.List,'&amp;','&'),'&gt;','>'),'&lt;','<'),'&apos;',char(39)),'&quot;',char(34)),'') + ');  ' --Virtual Columns
        + CASE WHEN (len(IsNull(j.list,'')) = 0 AND LEN('{_identityColumn}') = 0) OR 0 = {_addConstraints} THEN '' ELSE 'ALTER TABLE ' +  Quotename('{trgDatabase}') + '.' + Quotename('{trgSchema}') + '.' + Quotename('{trgObject}') + ' ADD CONSTRAINT ' + Quotename('PK_{trgObject}')  + ' PRIMARY KEY ' + ' (' +
        + IIF(len(IsNull(j.list,'')) > 0 ,substring(replace(Replace(Replace(replace(replace(j.List,'&amp;','&'),'&gt;','>'),'&lt;','<'),'&apos;',char(39)),'&quot;',char(34)), 2, Len(j.List)), '{_identityColumn}') + '); ' END 
        + IIF(m.IndexCols  is null, '' , '; CREATE UNIQUE INDEX NCI_KeyColumn ON ' +  Quotename('{trgDatabase}') + '.' + Quotename('{trgSchema}') + '.' + Quotename('{trgObject}') + '('+ 
        + substring(replace(Replace(Replace(replace(replace(m.IndexCols,'&amp;','&'),'&gt;','>'),'&lt;','<'),'&apos;',char(39)),'&quot;',char(34)), 2, Len(m.IndexCols)) +')')
		+ IIF(s.DateCol  is null, '' , '; CREATE INDEX NCI_DateColumn ON ' + Quotename('{trgDatabase}') + '.' + Quotename('{trgSchema}') + '.' + Quotename('{trgObject}') + '('+ 
        + substring(replace(Replace(Replace(replace(replace(s.DateCol,'&amp;','&'),'&gt;','>'),'&lt;','<'),'&apos;',char(39)),'&quot;',char(34)), 2, Len(s.DateCol)) +')')
        + IIF(k.IncrementalCol  is null, '' , '; CREATE INDEX NCI_IncrementalCol ON ' + Quotename('{trgDatabase}') + '.' + Quotename('{trgSchema}') + '.' + Quotename('{trgObject}') + '('+ 
        + substring(replace(Replace(Replace(replace(replace(k.IncrementalCol,'&amp;','&'),'&gt;','>'),'&lt;','<'),'&apos;',char(39)),'&quot;',char(34)), 2, Len(k.IncrementalCol)) +')')
        AS 'SQL_CREATE_TABLE'
FROM [{srcDatabase}].sys.objects so with({withHint})
        CROSS APPLY (
				SELECT ',' + QUOTENAME(ColumnName) + ' ' + DataTypeExp
					FROM Virtual
             FOR XML PATH('')
		) v(list)
        CROSS APPLY(SELECT  ',' + '  [' + cl.{_metaJoinColName} + '] ' +
                        CASE WHEN len(Isnull(ts.DataType,'')) > 0 THEN ts.DataType ELSE
                        CASE WHEN data_type in ('nvarchar','nchar','ntext') AND 1={_convUnicodeDt} THEN substring(data_type,2,len(data_type)) ELSE data_type END
                    + CASE WHEN data_type in ('sql_variant','text','ntext','int','float','smallint','bigint','real','datetime','smalldatetime','tinyint', 'bit', 'datetime2','date','xml','hierarchyid','geography') THEN '' WHEN data_type in ('time') THEN '(' + Cast(Datetime_Precision AS VARCHAR) + ')' WHEN data_type in ('decimal', 'NUMERIC')  THEN '(' + Cast(numeric_precision AS VARCHAR) + ', ' + Cast(numeric_scale AS VARCHAR) + ')' ELSE ISNULL( CASE WHEN DATA_TYPE IN ('XML')  THEN '' WHEN character_maximum_length = -1 THEN '(MAX)' ELSE '(' + CAST(character_maximum_length AS VARCHAR) + ')' END , '')  END END + ' ' 
                    -- NOT NULL can lead to issues + ' ' + (CASE WHEN IS_NULLABLE = 'No' THEN 'NOT ' ELSE '' END) + 'NULL ' 
                    + CASE WHEN cl.COLUMN_NAME_CLEANED in ({keyColumnsParsed}) OR cl.COLUMN_NAME IN ({keyColumnsParsed}) OR kcu.COLUMN_NAME is not null THEN 'NOT ' ELSE '' END +  'NULL ' --NotNullOnlyFor PK Columns
                    --+ CASE WHEN a.COLUMN_DEFAULT IS NOT NULL THEN 'DEFAULT '  + a.COLUMN_DEFAULT ELSE '' END -- The source data will provide default values
                    FROM   [{srcDatabase}].information_schema.columns a with({withHint}) 
                    INNER JOIN UniqueCleanedColName cl
                                on cl.TABLE_SCHEMA = a.TABLE_SCHEMA
                            AND cl.TABLE_NAME = a.TABLE_NAME
                            AND cl.COLUMN_NAME = a.COLUMN_NAME
                            AND cl.COLUMN_NAME not in ({ignoreColumns})
                            AND cl.COLUMN_NAME not in (SELECT ColumnName FROM Virtual) --Exclude SysCols / Virtual Cols as they are added seperatly v(list)
                    LEFT OUTER JOIN  TokenSchema ts with({withHint}) 
                            ON ts.COLUMN_NAME = a.COLUMN_NAME 
                            --AND ts.TABLE_NAME = a.TABLE_NAME    doesnt work if target table has different name
                    LEFT OUTER JOIN
	                (
	                    SELECT ColU.TABLE_SCHEMA, ColU.Table_Name, ColU.Column_Name FROM 
    	                    [{srcDatabase}].INFORMATION_SCHEMA.TABLE_CONSTRAINTS Tab with({withHint}), 
    	                    [{srcDatabase}].INFORMATION_SCHEMA.KEY_COLUMN_USAGE  ColU with({withHint})
	                    WHERE 
		                    ColU.TABLE_SCHEMA = Tab.TABLE_SCHEMA 
                            AND ColU.Table_Name = Tab.Table_Name
                            and ColU.Constraint_Name = Tab.Constraint_Name
		                    AND tab.Constraint_Type = 'PRIMARY KEY' 
	                ) kcu
	                    on kcu.TABLE_SCHEMA = a.TABLE_SCHEMA 
                        AND kcu.Table_Name = a.Table_Name 
                        AND kcu.Column_Name = a.COLUMN_NAME  
                    WHERE a.table_name = so.name
                            and a.TABLE_SCHEMA = (select top 1 name from [{srcDatabase}].sys.schemas with({withHint}) WHERE schema_id = so.schema_id)
                    ORDER BY a.ordinal_position
                    FOR XML PATH('')) o(list)
        CROSS APPLY (
			SELECT     ',' +  Quotename(cl.{_metaJoinColName}) 
			FROM    [{srcDatabase}].INFORMATION_SCHEMA.COLUMNS AS cols with({withHint})
            INNER JOIN UniqueCleanedColName cl
                    on cl.TABLE_SCHEMA = cols.TABLE_SCHEMA
                    AND cl.TABLE_NAME = cols.TABLE_NAME
                    AND cl.COLUMN_NAME = cols.COLUMN_NAME
			WHERE cols.TABLE_SCHEMA = (select top 1 name from [{srcDatabase}].sys.schemas with({withHint}) WHERE schema_id = so.schema_id)
			AND cols.TABLE_NAME = so.name
            AND cols.COLUMN_NAME not in ({ignoreColumns})
            AND cols.COLUMN_NAME in ( SELECT  kcu.COLUMN_NAME
				                        FROM   [{srcDatabase}].INFORMATION_SCHEMA.TABLE_CONSTRAINTS AS tc with({withHint}) INNER JOIN
										            [{srcDatabase}].INFORMATION_SCHEMA.KEY_COLUMN_USAGE AS kcu with({withHint}) ON kcu.CONSTRAINT_NAME = tc.CONSTRAINT_NAME
										                and kcu.TABLE_CATALOG = tc.TABLE_CATALOG
						                        and kcu.TABLE_SCHEMA = tc.TABLE_SCHEMA
						                        and kcu.TABLE_NAME = tc.TABLE_NAME
                                                AND tc.TABLE_CATALOG = cols.TABLE_CATALOG
				                                AND tc.TABLE_SCHEMA = Cols.TABLE_SCHEMA
				                                AND tc.TABLE_NAME = Cols.TABLE_NAME
				                        WHERE     (tc.CONSTRAINT_TYPE = 'PRIMARY KEY')
            ) FOR XML PATH('')
		) j(list)
        CROSS APPLY (
			SELECT    ',' +   Quotename(cl.{_metaJoinColName}) 
			FROM         [{srcDatabase}].INFORMATION_SCHEMA.COLUMNS AS cols with({withHint})
			INNER JOIN UniqueCleanedColName cl
                    on cl.TABLE_SCHEMA = cols.TABLE_SCHEMA
                    AND cl.TABLE_NAME = cols.TABLE_NAME
                    AND cl.COLUMN_NAME = cols.COLUMN_NAME
			WHERE cols.TABLE_SCHEMA = (select top 1 name from [{srcDatabase}].sys.schemas with({withHint}) WHERE schema_id = so.schema_id)
			AND cols.TABLE_NAME = so.name
			AND cols.COLUMN_NAME in ({keyColumnsParsed})
            AND cols.COLUMN_NAME not in ({ignoreColumns})
            AND cols.COLUMN_NAME NOT IN 
            (
                SELECT  kcu.COLUMN_NAME
				FROM        [{srcDatabase}].INFORMATION_SCHEMA.TABLE_CONSTRAINTS AS tc with({withHint}) INNER JOIN
										[{srcDatabase}].INFORMATION_SCHEMA.KEY_COLUMN_USAGE AS kcu with({withHint}) ON kcu.CONSTRAINT_NAME = tc.CONSTRAINT_NAME
                                AND tc.TABLE_CATALOG = cols.TABLE_CATALOG
				                AND tc.TABLE_SCHEMA = Cols.TABLE_SCHEMA
				                AND tc.TABLE_NAME = Cols.TABLE_NAME
                                --AND cols.COLUMN_NAME <>  ConsCols.COLUMN_NAME
				WHERE     (tc.CONSTRAINT_TYPE = 'PRIMARY KEY')
            )
			FOR XML PATH('') ) m(IndexCols)
            CROSS APPLY (
			SELECT    ',' +   Quotename(cl.{_metaJoinColName}) 
			FROM         [{srcDatabase}].INFORMATION_SCHEMA.COLUMNS AS cols with({withHint})
			INNER JOIN UniqueCleanedColName cl 
                    on cl.TABLE_SCHEMA = cols.TABLE_SCHEMA
                    AND cl.TABLE_NAME = cols.TABLE_NAME
                    AND cl.COLUMN_NAME = cols.COLUMN_NAME
			WHERE cols.TABLE_SCHEMA = (select top 1 name from [{srcDatabase}].sys.schemas with({withHint}) WHERE schema_id = so.schema_id)
			AND cols.TABLE_NAME = so.name
			AND cols.COLUMN_NAME in ('{dateColumn}')
            AND cols.COLUMN_NAME not in ({ignoreColumns})
            AND cols.COLUMN_NAME NOT IN 
            (
                SELECT   kcu.COLUMN_NAME
				FROM         [{srcDatabase}].INFORMATION_SCHEMA.TABLE_CONSTRAINTS AS tc with({withHint}) INNER JOIN
										[{srcDatabase}].INFORMATION_SCHEMA.KEY_COLUMN_USAGE AS kcu with({withHint}) ON kcu.CONSTRAINT_NAME = tc.CONSTRAINT_NAME
                				AND tc.TABLE_CATALOG = cols.TABLE_CATALOG
				                AND tc.TABLE_SCHEMA = Cols.TABLE_SCHEMA
				                AND tc.TABLE_NAME = Cols.TABLE_NAME
                                --AND cols.COLUMN_NAME <>  ConsCols.COLUMN_NAME
				WHERE     (tc.CONSTRAINT_TYPE = 'PRIMARY KEY')
            )
			FOR XML PATH('')
		    ) s(DateCol)
            CROSS APPLY (
			SELECT    ',' +   Quotename(cl.{_metaJoinColName}) 
			FROM         [{srcDatabase}].INFORMATION_SCHEMA.COLUMNS AS cols with({withHint})
			INNER JOIN UniqueCleanedColName cl 
                    on cl.TABLE_SCHEMA = cols.TABLE_SCHEMA
                    AND cl.TABLE_NAME = cols.TABLE_NAME
                    AND cl.COLUMN_NAME = cols.COLUMN_NAME
			WHERE cols.TABLE_SCHEMA = (select top 1 name from [{srcDatabase}].sys.schemas with({withHint}) WHERE schema_id = so.schema_id)
			AND cols.TABLE_NAME = so.name
			AND cols.COLUMN_NAME in ('{incrementalColumns}')
            AND cols.COLUMN_NAME not in ({ignoreColumns})
            AND cols.COLUMN_NAME NOT IN 
            (
                SELECT   kcu.COLUMN_NAME
				FROM         [{srcDatabase}].INFORMATION_SCHEMA.TABLE_CONSTRAINTS AS tc with({withHint}) INNER JOIN
										[{srcDatabase}].INFORMATION_SCHEMA.KEY_COLUMN_USAGE AS kcu with({withHint}) ON kcu.CONSTRAINT_NAME = tc.CONSTRAINT_NAME
                				AND tc.TABLE_CATALOG = cols.TABLE_CATALOG
				                AND tc.TABLE_SCHEMA = Cols.TABLE_SCHEMA
				                AND tc.TABLE_NAME = Cols.TABLE_NAME
                                --AND cols.COLUMN_NAME <>  ConsCols.COLUMN_NAME
				WHERE     (tc.CONSTRAINT_TYPE = 'PRIMARY KEY')
            )
			FOR XML PATH('')
		    ) k(IncrementalCol)
            


        WHERE type IN('V', 'U')
        and  object_id = object_id('[{srcDatabase}].[{srcSchema}].[{srcObject}]'); 
";

            EventArgsSchema eas = new EventArgsSchema
            {
                CreateObject = $"[{trgDatabase}].[{trgSchema}].[{trgObject}]",
                GenerateCreateCmd = createCmd
            };
            GetSchemaScript?.Invoke(this, eas);

            GenerateCreateCmd = createCmd;
            var gd = new GetData(_srcSqlCon, createCmd, _commandTimeOutInSeconds);
            var dt = gd.Fetch();

            if (dt.Rows.Count > 0) rValue = dt.Rows[0]["SQL_CREATE_TABLE"]?.ToString() ?? string.Empty;

            EventArgsSchema eas2 = new EventArgsSchema
            {
                CreateObject = $"[{trgDatabase}].[{trgSchema}].[{trgObject}]",
                CreateCmd = rValue
            };
            GetSchemaScript?.Invoke(this, eas2);

            return rValue;
        }

        private string BuildCreateCmdSyn(string srcDatabase, string srcSchema, string srcObject,
    string trgDatabase, string trgSchema, string trgObject, bool createIndexes,
    string keyColumnsParsed, string dateColumn, string ignoreColumns, string tokenSchemaForCte,
    string sysTblColSelectForCte,
    string sysColSelectForCte,
    string sysDataTypeSelectForCte, string virtualSchemaForCte, string withHint, bool trgIsSynapse, bool srcIsSynapse, string incrementalColumns)
        {
            if (createIndexes == false) keyColumnsParsed = "''";

            var rValue = "";
            var createCmd = BuildColCleanSyn(srcDatabase, srcSchema, srcObject, ignoreColumns, tokenSchemaForCte,
                sysTblColSelectForCte, sysColSelectForCte, sysDataTypeSelectForCte, virtualSchemaForCte, withHint);
            if (trgIsSynapse)
            {
            }
            if (trgIsSynapse)
            {
            }

            string cmdPart1 = $@"SELECT STRING_AGG (QUOTENAME(ColumnName) + ' ' + DataTypeExp, ',') list  FROM Virtual";
            if (srcIsSynapse == false)
            {
                cmdPart1 = $@"SELECT STUFF((SELECT  ',' + QUOTENAME(ColumnName) + ' ' + DataTypeExp FROM Virtual
                FOR XML PATH(''), TYPE).value('text()[1]', 'nvarchar(max)'),1, LEN(','), '') AS list";
            }

            string cmdPart2 = $@"SELECT STRING_AGG('[' + cl.{_metaJoinColName}
                              + '] ' +
                              CASE WHEN len(Isnull(ts.DataType,'')) > 0 THEN ts.DataType ELSE
                        CASE WHEN data_type in ('nvarchar', 'nchar', 'ntext') AND 1 ={_convUnicodeDt} THEN substring(data_type,2,len(data_type)) ELSE data_type END
+ CASE WHEN data_type in ('sql_variant', 'text', 'ntext', 'int', 'float', 'smallint', 'bigint', 'real', 'datetime', 'smalldatetime', 'tinyint', 'bit', 'datetime2', 'date', 'xml', 'hierarchyid', 'geography') THEN '' WHEN data_type in ('time') THEN '(' + Cast(Datetime_Precision AS VARCHAR) + ')' WHEN data_type in ('decimal', 'NUMERIC')  THEN '(' + Cast(numeric_precision AS VARCHAR) + ', ' + Cast(numeric_scale AS VARCHAR) + ')' ELSE ISNULL(CASE WHEN DATA_TYPE IN ('XML')  THEN '' WHEN character_maximum_length = -1 THEN '(MAX)' ELSE '(' + CAST(character_maximum_length AS VARCHAR) + ')' END , '')  END END +' '
+ CASE WHEN cl.COLUMN_NAME_CLEANED in ({keyColumnsParsed}) OR cl.COLUMN_NAME IN({keyColumnsParsed}) OR kcu.COLUMN_NAME is not null THEN 'NOT ' ELSE '' END + 'NULL '
                    ,',') WITHIN GROUP(ORDER BY a.ordinal_position) list
                   FROM [{srcDatabase}].information_schema.columns a with({withHint}) 
                    INNER JOIN UniqueCleanedColName cl
                                on cl.TABLE_SCHEMA = a.TABLE_SCHEMA
                            AND cl.TABLE_NAME = a.TABLE_NAME
                            AND cl.COLUMN_NAME = a.COLUMN_NAME
                            AND cl.COLUMN_NAME not in ({ignoreColumns})
                            AND cl.COLUMN_NAME not in (SELECT ColumnName FROM Virtual) --Exclude SysCols / Virtual Cols as they are added seperatly v(list)
                    LEFT OUTER JOIN TokenSchema ts with({withHint}) 
                            ON ts.COLUMN_NAME = a.COLUMN_NAME
                            --AND ts.TABLE_NAME = a.TABLE_NAME    doesnt work if target table has different name
                    LEFT OUTER JOIN
                    (
                        SELECT ColU.TABLE_SCHEMA, ColU.Table_Name, ColU.Column_Name FROM
                            [{srcDatabase}].INFORMATION_SCHEMA.TABLE_CONSTRAINTS Tab with({withHint}), 
    	                    [{srcDatabase}].INFORMATION_SCHEMA.KEY_COLUMN_USAGE ColU with({withHint})
	                    WHERE
                            ColU.TABLE_SCHEMA = Tab.TABLE_SCHEMA
                            AND ColU.Table_Name = Tab.Table_Name
                            and ColU.Constraint_Name = Tab.Constraint_Name
                            AND tab.Constraint_Type = 'PRIMARY KEY'
	                ) kcu
                        on kcu.TABLE_SCHEMA = a.TABLE_SCHEMA
                        AND kcu.Table_Name = a.Table_Name
                        AND kcu.Column_Name = a.COLUMN_NAME
                    WHERE a.table_name = so.name
                            and a.TABLE_SCHEMA = (select top 1 name from[{srcDatabase}].sys.schemas with({withHint}) WHERE schema_id = so.schema_id) ";

            if (srcIsSynapse == false)
            {
                cmdPart2 = $@"SELECT STUFF((SELECT  ',' + '  [' + cl.{_metaJoinColName} + '] ' +
                        CASE WHEN len(Isnull(ts.DataType,'')) > 0 THEN ts.DataType ELSE
                        CASE WHEN data_type in ('nvarchar','nchar','ntext') AND 1={_convUnicodeDt} THEN substring(data_type,2,len(data_type)) ELSE data_type END
                    + CASE WHEN data_type in ('sql_variant','text','ntext','int','float','smallint','bigint','real','datetime','smalldatetime','tinyint', 'bit', 'datetime2','date','xml','hierarchyid','geography') THEN '' WHEN data_type in ('time') THEN '(' + Cast(Datetime_Precision AS VARCHAR) + ')' WHEN data_type in ('decimal', 'NUMERIC')  THEN '(' + Cast(numeric_precision AS VARCHAR) + ', ' + Cast(numeric_scale AS VARCHAR) + ')' ELSE ISNULL( CASE WHEN DATA_TYPE IN ('XML')  THEN '' WHEN character_maximum_length = -1 THEN '(MAX)' ELSE '(' + CAST(character_maximum_length AS VARCHAR) + ')' END , '')  END END + ' ' 
                    -- NOT NULL can lead to issues + ' ' + (CASE WHEN IS_NULLABLE = 'No' THEN 'NOT ' ELSE '' END) + 'NULL ' 
                    + CASE WHEN cl.COLUMN_NAME_CLEANED in ({keyColumnsParsed}) OR cl.COLUMN_NAME IN ({keyColumnsParsed}) OR kcu.COLUMN_NAME is not null THEN 'NOT ' ELSE '' END +  'NULL ' --NotNullOnlyFor PK Columns
                    --+ CASE WHEN a.COLUMN_DEFAULT IS NOT NULL THEN 'DEFAULT '  + a.COLUMN_DEFAULT ELSE '' END -- The source data will provide default values
                    FROM   [{srcDatabase}].information_schema.columns a with({withHint}) 
                    INNER JOIN UniqueCleanedColName cl
                                on cl.TABLE_SCHEMA = a.TABLE_SCHEMA
                            AND cl.TABLE_NAME = a.TABLE_NAME
                            AND cl.COLUMN_NAME = a.COLUMN_NAME
                            AND cl.COLUMN_NAME not in ({ignoreColumns})
                            AND cl.COLUMN_NAME not in (SELECT ColumnName FROM Virtual) --Exclude SysCols / Virtual Cols as they are added seperatly v(list)
                    LEFT OUTER JOIN  TokenSchema ts with({withHint}) 
                            ON ts.COLUMN_NAME = a.COLUMN_NAME 
                            --AND ts.TABLE_NAME = a.TABLE_NAME    doesnt work if target table has different name
                    LEFT OUTER JOIN
	                (
	                    SELECT ColU.TABLE_SCHEMA, ColU.Table_Name, ColU.Column_Name FROM 
    	                    [{srcDatabase}].INFORMATION_SCHEMA.TABLE_CONSTRAINTS Tab with({withHint}), 
    	                    [{srcDatabase}].INFORMATION_SCHEMA.KEY_COLUMN_USAGE  ColU with({withHint})
	                    WHERE 
		                    ColU.TABLE_SCHEMA = Tab.TABLE_SCHEMA 
                            AND ColU.Table_Name = Tab.Table_Name
                            and ColU.Constraint_Name = Tab.Constraint_Name
		                    AND tab.Constraint_Type = 'PRIMARY KEY' 
	                ) kcu
	                    on kcu.TABLE_SCHEMA = a.TABLE_SCHEMA 
                        AND kcu.Table_Name = a.Table_Name 
                        AND kcu.Column_Name = a.COLUMN_NAME  
                    WHERE a.table_name = so.name
                            and a.TABLE_SCHEMA = (select top 1 name from [{srcDatabase}].sys.schemas with({withHint}) WHERE schema_id = so.schema_id)
                    ORDER BY a.ordinal_position
                     FOR XML PATH(''), TYPE).value('text()[1]', 'nvarchar(max)'),1, LEN(','), '') AS list";
            }


            string cmdPart3 = $@"SELECT   STRING_AGG(Quotename(cl.{_metaJoinColName}) , ',' ) list
            FROM[{srcDatabase}].INFORMATION_SCHEMA.COLUMNS AS cols with({withHint})
            INNER JOIN UniqueCleanedColName cl
            on cl.TABLE_SCHEMA = cols.TABLE_SCHEMA
            AND cl.TABLE_NAME = cols.TABLE_NAME
            AND cl.COLUMN_NAME = cols.COLUMN_NAME

            WHERE cols.TABLE_SCHEMA = (select top 1 name from[{srcDatabase}].sys.schemas with({withHint}) WHERE schema_id = so.schema_id)
            AND cols.TABLE_NAME = so.name
            AND cols.COLUMN_NAME not in ({ignoreColumns})
            AND cols.COLUMN_NAME in (SELECT  kcu.COLUMN_NAME
            FROM[{srcDatabase}].INFORMATION_SCHEMA.TABLE_CONSTRAINTS AS tc with({withHint}) INNER JOIN
                [{srcDatabase}].INFORMATION_SCHEMA.KEY_COLUMN_USAGE AS kcu with({withHint}) ON kcu.CONSTRAINT_NAME = tc.CONSTRAINT_NAME

            and kcu.TABLE_CATALOG = tc.TABLE_CATALOG
            and kcu.TABLE_SCHEMA = tc.TABLE_SCHEMA
            and kcu.TABLE_NAME = tc.TABLE_NAME
            AND tc.TABLE_CATALOG = cols.TABLE_CATALOG
            AND tc.TABLE_SCHEMA = Cols.TABLE_SCHEMA
            AND tc.TABLE_NAME = Cols.TABLE_NAME
            WHERE(tc.CONSTRAINT_TYPE = 'PRIMARY KEY') ) ";

            if (srcIsSynapse == false)
            {
                cmdPart3 = $@"SELECT STUFF((SELECT ',' +  Quotename(cl.{_metaJoinColName}) 
			FROM    [{srcDatabase}].INFORMATION_SCHEMA.COLUMNS AS cols with({withHint})
            INNER JOIN UniqueCleanedColName cl
                    on cl.TABLE_SCHEMA = cols.TABLE_SCHEMA
                    AND cl.TABLE_NAME = cols.TABLE_NAME
                    AND cl.COLUMN_NAME = cols.COLUMN_NAME
			WHERE cols.TABLE_SCHEMA = (select top 1 name from [{srcDatabase}].sys.schemas with({withHint}) WHERE schema_id = so.schema_id)
			AND cols.TABLE_NAME = so.name
            AND cols.COLUMN_NAME not in ({ignoreColumns})
            AND cols.COLUMN_NAME in ( SELECT  kcu.COLUMN_NAME
				                        FROM   [{srcDatabase}].INFORMATION_SCHEMA.TABLE_CONSTRAINTS AS tc with({withHint}) INNER JOIN
										            [{srcDatabase}].INFORMATION_SCHEMA.KEY_COLUMN_USAGE AS kcu with({withHint}) ON kcu.CONSTRAINT_NAME = tc.CONSTRAINT_NAME
										                and kcu.TABLE_CATALOG = tc.TABLE_CATALOG
						                        and kcu.TABLE_SCHEMA = tc.TABLE_SCHEMA
						                        and kcu.TABLE_NAME = tc.TABLE_NAME
                                                AND tc.TABLE_CATALOG = cols.TABLE_CATALOG
				                                AND tc.TABLE_SCHEMA = Cols.TABLE_SCHEMA
				                                AND tc.TABLE_NAME = Cols.TABLE_NAME
				                        WHERE     (tc.CONSTRAINT_TYPE = 'PRIMARY KEY')
            )
                FOR XML PATH(''), TYPE).value('text()[1]', 'nvarchar(max)'),1, LEN(','), '') AS list";
            }


            string cmdPart4 = $@"SELECT   STRING_AGG( Quotename(cl.{_metaJoinColName}) , ',' ) IndexCols

            FROM[{srcDatabase}].INFORMATION_SCHEMA.COLUMNS AS cols with({withHint})
            INNER JOIN UniqueCleanedColName cl
            on cl.TABLE_SCHEMA = cols.TABLE_SCHEMA
            AND cl.TABLE_NAME = cols.TABLE_NAME
            AND cl.COLUMN_NAME = cols.COLUMN_NAME

            WHERE cols.TABLE_SCHEMA = (select top 1 name from[{srcDatabase}].sys.schemas with({withHint}) WHERE schema_id = so.schema_id)
            AND cols.TABLE_NAME = so.name

            AND cols.COLUMN_NAME in ({keyColumnsParsed})
            AND cols.COLUMN_NAME not in ({ignoreColumns})
            AND cols.COLUMN_NAME NOT IN
            (
                SELECT  kcu.COLUMN_NAME

            FROM[{srcDatabase}].INFORMATION_SCHEMA.TABLE_CONSTRAINTS AS tc with({withHint}) INNER JOIN
                [{srcDatabase}].INFORMATION_SCHEMA.KEY_COLUMN_USAGE AS kcu with({withHint}) ON kcu.CONSTRAINT_NAME = tc.CONSTRAINT_NAME
            AND tc.TABLE_CATALOG = cols.TABLE_CATALOG

            AND tc.TABLE_SCHEMA = Cols.TABLE_SCHEMA

            AND tc.TABLE_NAME = Cols.TABLE_NAME
                --AND cols.COLUMN_NAME<> ConsCols.COLUMN_NAME

            WHERE(tc.CONSTRAINT_TYPE = 'PRIMARY KEY')
                )
            ";

            if (srcIsSynapse == false)
            {
                cmdPart4 = $@"SELECT STUFF((SELECT ',' +   Quotename(cl.{_metaJoinColName}) 
			FROM         [{srcDatabase}].INFORMATION_SCHEMA.COLUMNS AS cols with({withHint})
			INNER JOIN UniqueCleanedColName cl
                    on cl.TABLE_SCHEMA = cols.TABLE_SCHEMA
                    AND cl.TABLE_NAME = cols.TABLE_NAME
                    AND cl.COLUMN_NAME = cols.COLUMN_NAME
			WHERE cols.TABLE_SCHEMA = (select top 1 name from [{srcDatabase}].sys.schemas with({withHint}) WHERE schema_id = so.schema_id)
			AND cols.TABLE_NAME = so.name
			AND cols.COLUMN_NAME in ({keyColumnsParsed})
            AND cols.COLUMN_NAME not in ({ignoreColumns})
            AND cols.COLUMN_NAME NOT IN 
            (
                SELECT  kcu.COLUMN_NAME
				FROM        [{srcDatabase}].INFORMATION_SCHEMA.TABLE_CONSTRAINTS AS tc with({withHint}) INNER JOIN
										[{srcDatabase}].INFORMATION_SCHEMA.KEY_COLUMN_USAGE AS kcu with({withHint}) ON kcu.CONSTRAINT_NAME = tc.CONSTRAINT_NAME
                                AND tc.TABLE_CATALOG = cols.TABLE_CATALOG
				                AND tc.TABLE_SCHEMA = Cols.TABLE_SCHEMA
				                AND tc.TABLE_NAME = Cols.TABLE_NAME
                                --AND cols.COLUMN_NAME <>  ConsCols.COLUMN_NAME
				WHERE     (tc.CONSTRAINT_TYPE = 'PRIMARY KEY')
            )
                FOR XML PATH(''), TYPE).value('text()[1]', 'nvarchar(max)'),1, LEN(','), '') AS IndexCols";
            }


            string cmdPart5 = $@"SELECT   STRING_AGG( Quotename(cl.{_metaJoinColName}) , ',' ) as DateCol
			FROM         [{srcDatabase}].INFORMATION_SCHEMA.COLUMNS AS cols with({withHint})
			INNER JOIN UniqueCleanedColName cl 
                    on cl.TABLE_SCHEMA = cols.TABLE_SCHEMA
                    AND cl.TABLE_NAME = cols.TABLE_NAME
                    AND cl.COLUMN_NAME = cols.COLUMN_NAME
			WHERE cols.TABLE_SCHEMA = (select top 1 name from [{srcDatabase}].sys.schemas with({withHint}) WHERE schema_id = so.schema_id)
			AND cols.TABLE_NAME = so.name
			AND cols.COLUMN_NAME in ('{dateColumn}')
            AND cols.COLUMN_NAME not in ({ignoreColumns})
            AND cols.COLUMN_NAME NOT IN 
            (
                SELECT   kcu.COLUMN_NAME
				FROM         [{srcDatabase}].INFORMATION_SCHEMA.TABLE_CONSTRAINTS AS tc with({withHint}) INNER JOIN
										[{srcDatabase}].INFORMATION_SCHEMA.KEY_COLUMN_USAGE AS kcu with({withHint}) ON kcu.CONSTRAINT_NAME = tc.CONSTRAINT_NAME
                				AND tc.TABLE_CATALOG = cols.TABLE_CATALOG
				                AND tc.TABLE_SCHEMA = Cols.TABLE_SCHEMA
				                AND tc.TABLE_NAME = Cols.TABLE_NAME
                                --AND cols.COLUMN_NAME <>  ConsCols.COLUMN_NAME
				WHERE     (tc.CONSTRAINT_TYPE = 'PRIMARY KEY')
            )";
            if (srcIsSynapse == false)
            {
                cmdPart5 = $@"SELECT STUFF((SELECT    ',' +   Quotename(cl.{_metaJoinColName}) 
			FROM         [{srcDatabase}].INFORMATION_SCHEMA.COLUMNS AS cols with({withHint})
			INNER JOIN UniqueCleanedColName cl 
                    on cl.TABLE_SCHEMA = cols.TABLE_SCHEMA
                    AND cl.TABLE_NAME = cols.TABLE_NAME
                    AND cl.COLUMN_NAME = cols.COLUMN_NAME
			WHERE cols.TABLE_SCHEMA = (select top 1 name from [{srcDatabase}].sys.schemas with({withHint}) WHERE schema_id = so.schema_id)
			AND cols.TABLE_NAME = so.name
			AND cols.COLUMN_NAME in ('{dateColumn}')
            AND cols.COLUMN_NAME not in ({ignoreColumns})
            AND cols.COLUMN_NAME NOT IN 
            (
                SELECT   kcu.COLUMN_NAME
				FROM         [{srcDatabase}].INFORMATION_SCHEMA.TABLE_CONSTRAINTS AS tc with({withHint}) INNER JOIN
										[{srcDatabase}].INFORMATION_SCHEMA.KEY_COLUMN_USAGE AS kcu with({withHint}) ON kcu.CONSTRAINT_NAME = tc.CONSTRAINT_NAME
                				AND tc.TABLE_CATALOG = cols.TABLE_CATALOG
				                AND tc.TABLE_SCHEMA = Cols.TABLE_SCHEMA
				                AND tc.TABLE_NAME = Cols.TABLE_NAME
                                --AND cols.COLUMN_NAME <>  ConsCols.COLUMN_NAME
				WHERE     (tc.CONSTRAINT_TYPE = 'PRIMARY KEY')
            )
                FOR XML PATH(''), TYPE).value('text()[1]', 'nvarchar(max)'),1, LEN(','), '') AS DateCol";
            }


            string cmdPart6 = $@"SELECT   STRING_AGG( Quotename(cl.{_metaJoinColName}) , ',' ) as IncrementalCol
			FROM         [{srcDatabase}].INFORMATION_SCHEMA.COLUMNS AS cols with({withHint})
			INNER JOIN UniqueCleanedColName cl 
                    on cl.TABLE_SCHEMA = cols.TABLE_SCHEMA
                    AND cl.TABLE_NAME = cols.TABLE_NAME
                    AND cl.COLUMN_NAME = cols.COLUMN_NAME
			WHERE cols.TABLE_SCHEMA = (select top 1 name from [{srcDatabase}].sys.schemas with({withHint}) WHERE schema_id = so.schema_id)
			AND cols.TABLE_NAME = so.name
			AND cols.COLUMN_NAME in ('{incrementalColumns}')
            AND cols.COLUMN_NAME not in ({ignoreColumns})
            AND cols.COLUMN_NAME NOT IN 
            (
                SELECT   kcu.COLUMN_NAME
				FROM         [{srcDatabase}].INFORMATION_SCHEMA.TABLE_CONSTRAINTS AS tc with({withHint}) INNER JOIN
										[{srcDatabase}].INFORMATION_SCHEMA.KEY_COLUMN_USAGE AS kcu with({withHint}) ON kcu.CONSTRAINT_NAME = tc.CONSTRAINT_NAME
                				AND tc.TABLE_CATALOG = cols.TABLE_CATALOG
				                AND tc.TABLE_SCHEMA = Cols.TABLE_SCHEMA
				                AND tc.TABLE_NAME = Cols.TABLE_NAME
                                --AND cols.COLUMN_NAME <>  ConsCols.COLUMN_NAME
				WHERE     (tc.CONSTRAINT_TYPE = 'PRIMARY KEY')
            )";
            if (srcIsSynapse == false)
            {
                cmdPart6 = $@"SELECT STUFF((SELECT    ',' +   Quotename(cl.{_metaJoinColName}) 
			FROM         [{srcDatabase}].INFORMATION_SCHEMA.COLUMNS AS cols with({withHint})
			INNER JOIN UniqueCleanedColName cl 
                    on cl.TABLE_SCHEMA = cols.TABLE_SCHEMA
                    AND cl.TABLE_NAME = cols.TABLE_NAME
                    AND cl.COLUMN_NAME = cols.COLUMN_NAME
			WHERE cols.TABLE_SCHEMA = (select top 1 name from [{srcDatabase}].sys.schemas with({withHint}) WHERE schema_id = so.schema_id)
			AND cols.TABLE_NAME = so.name
			AND cols.COLUMN_NAME in ('{incrementalColumns}')
            AND cols.COLUMN_NAME not in ({ignoreColumns})
            AND cols.COLUMN_NAME NOT IN 
            (
                SELECT   kcu.COLUMN_NAME
				FROM         [{srcDatabase}].INFORMATION_SCHEMA.TABLE_CONSTRAINTS AS tc with({withHint}) INNER JOIN
										[{srcDatabase}].INFORMATION_SCHEMA.KEY_COLUMN_USAGE AS kcu with({withHint}) ON kcu.CONSTRAINT_NAME = tc.CONSTRAINT_NAME
                				AND tc.TABLE_CATALOG = cols.TABLE_CATALOG
				                AND tc.TABLE_SCHEMA = Cols.TABLE_SCHEMA
				                AND tc.TABLE_NAME = Cols.TABLE_NAME
                                --AND cols.COLUMN_NAME <>  ConsCols.COLUMN_NAME
				WHERE     (tc.CONSTRAINT_TYPE = 'PRIMARY KEY')
            )
                FOR XML PATH(''), TYPE).value('text()[1]', 'nvarchar(max)'),1, LEN(','), '') AS IncrementalCol";
            }

            //SYNAPSE --WITH(CLUSTERED COLUMNSTORE INDEX);
            //Synapse --WITH( CLUSTERED INDEX (id) );

            string useColStorCmd = "";

            if (_columnStoreIndexOnTrg)
            {
                useColStorCmd = " WITH(CLUSTERED COLUMNSTORE INDEX); ";
            }

            string createSelect = $@" 
SELECT 'CREATE TABLE ' +  Quotename('{trgDatabase}') + '.' + Quotename('{trgSchema}') + '.' + Quotename('{trgObject}') + ' ('
        + CASE WHEN LEN('{_identityColumn}') = 0 THEN '' ELSE '{_identityColumn} INT IDENTITY(1,1) NOT NULL,' END
        + Replace(Replace(Replace(Replace(Replace(o.List,'&amp;','&'),'&gt;','>'),'&lt;','<'),'&apos;',char(39)),'&quot;',char(34)) --Regular Columns
        + IsNull(',' + Replace(Replace(Replace(Replace(Replace(v.List,'&amp;','&'),'&gt;','>'),'&lt;','<'),'&apos;',char(39)),'&quot;',char(34)),'') + ')  ' --Virtual Columns
        + CASE WHEN (len(IsNull(j.list,'')) = 0 AND LEN('{_identityColumn}') = 0) OR 0 = {_addConstraints} THEN 'WITH ( HEAP ); ' WHEN LEN('{useColStorCmd}') > 0 THEN '{useColStorCmd}' ELSE ' WITH( CLUSTERED INDEX (' +
        + CASE WHEN len(IsNull(j.list,'')) > 0  THEN replace(Replace(Replace(replace(replace(j.List,'&amp;','&'),'&gt;','>'),'&lt;','<'),'&apos;',char(39)),'&quot;',char(34)) else '{_identityColumn}' END + ')); ' END 
        + CASE WHEN m.IndexCols  is null THEN '' ELSE '; CREATE UNIQUE INDEX NCI_KeyColumn ON ' +  Quotename('{trgDatabase}') + '.' + Quotename('{trgSchema}') + '.' + Quotename('{trgObject}') + '('+ 
        + replace(Replace(Replace(replace(replace(m.IndexCols,'&amp;','&'),'&gt;','>'),'&lt;','<'),'&apos;',char(39)),'&quot;',char(34)) +')' END
		+ CASE WHEN s.DateCol  is null THEN '' ELSE '; CREATE INDEX NCI_DateColumn ON ' + Quotename('{trgDatabase}') + '.' + Quotename('{trgSchema}') + '.' + Quotename('{trgObject}') + '('+ 
        + replace(Replace(Replace(replace(replace(s.DateCol,'&amp;','&'),'&gt;','>'),'&lt;','<'),'&apos;',char(39)),'&quot;',char(34)) +')' END
        + CASE WHEN k.IncrementalCol is null THEN '' ELSE '; CREATE INDEX NCI_IncrementalCol ON ' + Quotename('{trgDatabase}') + '.' + Quotename('{trgSchema}') + '.' + Quotename('{trgObject}') + '(' +
                                       +replace(Replace(Replace(replace(replace(k.IncrementalCol, '&amp;', '&'), '&gt;', '>'), '&lt;', '<'), '&apos;', char(39)), '&quot;', char(34)) + ')' END
        AS 'SQL_CREATE_TABLE' ";


            if (trgIsSynapse == false)
            {
                createSelect = $@"
SELECT 'CREATE TABLE ' + Quotename('{trgDatabase}') + '.' + Quotename('{trgSchema}') + '.' + Quotename('{trgObject}') + ' ('
        + CASE WHEN LEN('{_identityColumn}') = 0 THEN '' ELSE '{_identityColumn} INT IDENTITY(1,1) NOT NULL,' END
        + Replace(Replace(Replace(Replace(Replace(o.List, '&amp;', '&'), '&gt;', '>'), '&lt;', '<'), '&apos;', char(39)), '&quot;', char(34)) --Regular Columns
        + IsNull(Replace(Replace(Replace(Replace(Replace(','+v.List, '&amp;', '&'), '&gt;', '>'), '&lt;', '<'), '&apos;', char(39)), '&quot;', char(34)), '') + ');  '--Virtual Columns
        + CASE WHEN(len(IsNull(j.list, '')) = 0 AND LEN('{_identityColumn}') = 0) OR 0 = {_addConstraints} THEN '' ELSE 'ALTER TABLE ' + Quotename('{trgDatabase}') + '.' + Quotename('{trgSchema}') + '.' + Quotename('{trgObject}') + ' ADD CONSTRAINT ' + Quotename('PK_{trgObject}') + ' PRIMARY KEY ' + ' (' +
        + CASE WHEN len(IsNull(j.list, '')) > 0 THEN replace(Replace(Replace(replace(replace(j.List, '&amp;', '&'), '&gt;', '>'), '&lt;', '<'), '&apos;', char(39)), '&quot;', char(34)) ELSE '{_identityColumn}' END + '); ' END
        + CASE WHEN m.IndexCols is null THEN '' ELSE '; CREATE UNIQUE INDEX NCI_KeyColumn ON ' + Quotename('{trgDatabase}') + '.' + Quotename('{trgSchema}') + '.' + Quotename('{trgObject}') + '(' +
                                       +replace(Replace(Replace(replace(replace(m.IndexCols, '&amp;', '&'), '&gt;', '>'), '&lt;', '<'), '&apos;', char(39)), '&quot;', char(34)) + ')' END
        + CASE WHEN s.DateCol is null THEN '' ELSE '; CREATE INDEX NCI_DateColumn ON ' + Quotename('{trgDatabase}') + '.' + Quotename('{trgSchema}') + '.' + Quotename('{trgObject}') + '(' +
                                       +replace(Replace(Replace(replace(replace(s.DateCol, '&amp;', '&'), '&gt;', '>'), '&lt;', '<'), '&apos;', char(39)), '&quot;', char(34)) + ')' END
        + CASE WHEN k.IncrementalCol is null THEN '' ELSE '; CREATE INDEX NCI_IncrementalCol ON ' + Quotename('{trgDatabase}') + '.' + Quotename('{trgSchema}') + '.' + Quotename('{trgObject}') + '(' +
                                       +replace(Replace(Replace(replace(replace(k.IncrementalCol, '&amp;', '&'), '&gt;', '>'), '&lt;', '<'), '&apos;', char(39)), '&quot;', char(34)) + ')' END
    AS 'SQL_CREATE_TABLE'";
            }

            //WITH(CLUSTERED COLUMNSTORE INDEX);
            //ALTER TABLE ' +  Quotename('{trgDatabase}') + '.' + Quotename('{trgSchema}') + '.' + Quotename('{trgObject}') + ' ADD CONSTRAINT ' + Quotename('PK_{trgObject}')  + ' PRIMARY KEY {pkSynVal1} '
            createCmd = createCmd + createSelect + $@"
FROM [{srcDatabase}].sys.objects so with({withHint})
        CROSS APPLY (
				        {cmdPart1}
		            ) v
        CROSS APPLY (
                        {cmdPart2}
                    ) o
        CROSS APPLY (
			            {cmdPart3}
                    ) j
        CROSS APPLY (   
                        {cmdPart4}
			        ) m
        CROSS APPLY (
			            {cmdPart5}
		            ) s
        CROSS APPLY (
			            {cmdPart6}
		            ) k
        WHERE type IN('V', 'U')
        and  object_id = object_id('[{srcDatabase}].[{srcSchema}].[{srcObject}]')
        
";
            EventArgsSchema eas = new EventArgsSchema
            {
                CreateObject = $"[{trgDatabase}].[{trgSchema}].[{trgObject}]",
                GenerateCreateCmd = createCmd
            };
            GetSchemaScript?.Invoke(this, eas);

            GenerateCreateCmd = createCmd;
            var gd = new GetData(_srcSqlCon, createCmd, _commandTimeOutInSeconds);
            var dt = gd.Fetch();

            if (dt.Rows.Count > 0) rValue = dt.Rows[0]["SQL_CREATE_TABLE"]?.ToString() ?? string.Empty;

            EventArgsSchema eas2 = new EventArgsSchema
            {
                CreateObject = $"[{trgDatabase}].[{trgSchema}].[{trgObject}]",
                CreateCmd = rValue
            };
            GetSchemaScript?.Invoke(this, eas2);

            return rValue;
        }
    }
}