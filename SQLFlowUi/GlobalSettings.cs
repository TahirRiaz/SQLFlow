using Radzen;

namespace SQLFlowUi
{
    public class GlobalSettings
    {
        public static string MyGlobalProperty { get; set; } = "DefaultValue";
        public static DialogOptions EditOptions = new DialogOptions()
            { Width = "900px", Height = "800px", Resizable = true, Draggable = true };

        //public static System.Linq.IQueryable<SQLFlowUi.Models.sqlflowProd.SysDataSource> sysDataSources;
    }
}
