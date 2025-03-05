using Microsoft.SqlServer.Management.Smo;
using SQLFlowCore.Common;
using System.Data;
namespace SQLFlowCore.Lineage
{
    public class DepObject
    {
        public SQLObject RootObject { get; set; }
        public UrnCollection DependencyObjects { get; set; }

        public DataRow RootDataRow { get; set; }


    }


}
