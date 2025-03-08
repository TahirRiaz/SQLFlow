using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Radzen;
using Radzen.Blazor;

namespace SQLFlowUi.Components.Pages
{
    public partial class SysCheckDataTypes
    {
        [Inject]
        protected IJSRuntime JSRuntime { get; set; }

        [Inject]
        protected NavigationManager NavigationManager { get; set; }

        [Inject]
        protected DialogService DialogService { get; set; }

        [Inject]
        protected TooltipService TooltipService { get; set; }

        [Inject]
        protected ContextMenuService ContextMenuService { get; set; }

        [Inject]
        protected NotificationService NotificationService { get; set; }

        [Inject]
        public sqlflowProdService sqlflowProdService { get; set; }

        protected IEnumerable<SQLFlowUi.Models.sqlflowProd.SysCheckDataTypes> sysCheckDataTypesCollection;

        protected RadzenDataGrid<SQLFlowUi.Models.sqlflowProd.SysCheckDataTypes> grid0;

        protected string search = "";

        [Inject]
        protected SecurityService Security { get; set; }

        protected async Task Search(ChangeEventArgs args)
        {
            search = $"{args.Value}";

            await grid0.GoToPage(0);

            sysCheckDataTypesCollection = await sqlflowProdService.GetSysCheckDataTypes(new Query { Filter = $@"i => i.TableSchema.Contains(@0) || i.TableName.Contains(@0) || i.ColumnName.Contains(@0) || i.DataType.Contains(@0) || i.DataTypeExp.Contains(@0) || i.NewDataTypeExp.Contains(@0) || i.NewMaxDataTypeExp.Contains(@0) || i.MinValue.Contains(@0) || i.MaxValue.Contains(@0) || i.RandValue.Contains(@0) || i.SelectExp.Contains(@0) || i.cmdSQL.Contains(@0) || i.SQLFlowExp.Contains(@0) || i.MaxSQLFlowExp.Contains(@0)", FilterParameters = new object[] { search } });
        }
        protected override async Task OnInitializedAsync()
        {
            sysCheckDataTypesCollection = await sqlflowProdService.GetSysCheckDataTypes(new Query { Filter = $@"i => i.TableSchema.Contains(@0) || i.TableName.Contains(@0) || i.ColumnName.Contains(@0) || i.DataType.Contains(@0) || i.DataTypeExp.Contains(@0) || i.NewDataTypeExp.Contains(@0) || i.NewMaxDataTypeExp.Contains(@0) || i.MinValue.Contains(@0) || i.MaxValue.Contains(@0) || i.RandValue.Contains(@0) || i.SelectExp.Contains(@0) || i.cmdSQL.Contains(@0) || i.SQLFlowExp.Contains(@0) || i.MaxSQLFlowExp.Contains(@0)", FilterParameters = new object[] { search } });
        }

        protected async Task AddButtonClick(MouseEventArgs args)
        {
            await DialogService.OpenAsync<AddSysCheckDataTypes>("Add SysCheckDataTypes", null);
            await grid0.Reload();
        }

        protected async Task EditRow(DataGridRowMouseEventArgs<SQLFlowUi.Models.sqlflowProd.SysCheckDataTypes> args)
        {
            await DialogService.OpenAsync<EditSysCheckDataTypes>("Edit SysCheckDataTypes", new Dictionary<string, object> { {"RecID", args.Data.RecID} });
        }

        protected async Task GridDeleteButtonClick(MouseEventArgs args, SQLFlowUi.Models.sqlflowProd.SysCheckDataTypes sysCheckDataTypes)
        {
            try
            {
                if (await DialogService.Confirm("Are you sure you want to delete this record?") == true)
                {
                    var deleteResult = await sqlflowProdService.DeleteSysCheckDataTypes(sysCheckDataTypes.RecID);

                    if (deleteResult != null)
                    {
                        await grid0.Reload();
                    }
                }
            }
            catch (Exception ex)
            {
                NotificationService.Notify(new NotificationMessage
                {
                    Severity = NotificationSeverity.Error,
                    Summary = $"Error",
                    Detail = $"Unable to delete SysCheckDataTypes"
                });
            }
        }

        protected async Task ExportClick(RadzenSplitButtonItem args)
        {
            if (args?.Value == "csv")
            {
                await sqlflowProdService.ExportSysCheckDataTypesToCSV(new Query
                {
                    Filter = $@"{(string.IsNullOrEmpty(grid0.Query.Filter)? "true" : grid0.Query.Filter)}",
                    OrderBy = $"{grid0.Query.OrderBy}",
                    Expand = "",
                    Select = string.Join(",", grid0.ColumnsCollection.Where(c => c.GetVisible() && !string.IsNullOrEmpty(c.Property)).Select(c => c.Property.Contains(".") ? c.Property + " as " + c.Property.Replace(".", "") : c.Property))
                }, "SysCheckDataTypes");
            }

            if (args == null || args.Value == "xlsx")
            {
                await sqlflowProdService.ExportSysCheckDataTypesToExcel(new Query
                {
                    Filter = $@"{(string.IsNullOrEmpty(grid0.Query.Filter)? "true" : grid0.Query.Filter)}",
                    OrderBy = $"{grid0.Query.OrderBy}",
                    Expand = "",
                    Select = string.Join(",", grid0.ColumnsCollection.Where(c => c.GetVisible() && !string.IsNullOrEmpty(c.Property)).Select(c => c.Property.Contains(".") ? c.Property + " as " + c.Property.Replace(".", "") : c.Property))
                }, "SysCheckDataTypes");
            }
        }
    }
}