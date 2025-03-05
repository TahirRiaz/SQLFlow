using Radzen;
using SQLFlowUi.Components;

namespace SQLFlowUi.Controllers
{
    using Microsoft.AspNetCore.Components;
    using Radzen.Blazor;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    // Include any additional namespaces required by your DialogService

    public class DocumentationService
    {
        private readonly DialogService DialogService;
        private readonly NotificationService NotificationService;
        private readonly TooltipService tooltipService;
        private readonly IEnumerable<SQLFlowUi.Models.sqlflowProd.SysDocSubSet> sysDocLkp;
        private readonly Dictionary<string, string> labelCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> labelToolTip = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Constructor that takes DialogService as a dependency
        public DocumentationService(DialogService dialogService, sqlflowProdService sqlflowProdService, NotificationService notificationService, TooltipService tooltipService)
        {
            this.DialogService = dialogService;
            this.NotificationService = notificationService;
            this.tooltipService = tooltipService;
            this.sysDocLkp = sqlflowProdService.GetSysDocSubSet().Result;
            BuildLabelCache();
        }

        private void BuildLabelCache()
        {
            foreach (var doc in sysDocLkp)
            {
                if (!labelCache.ContainsKey(doc.ObjectName))
                {
                    labelCache[doc.ObjectName] = doc.Label;
                }
                // Optionally handle alternative keys or variations here
            }

            foreach (var doc in sysDocLkp)
            {
                if (!labelToolTip.ContainsKey(doc.ObjectName))
                {
                    labelToolTip[doc.ObjectName] = doc.Question;
                }
                // Optionally handle alternative keys or variations here
            }
        }
        
        public async Task<string> GetLabel(string ObjectName)
        {
            string rValue = "";
            if (labelCache.TryGetValue(ObjectName, out var label))
            {
                rValue = label?.Trim();
            }
            return rValue?.Length > 0 ? rValue : ExtractLastElement(ObjectName);
        }


        public async Task<string> GetToolTip(string ObjectName)
        {
            string rValue = "";
            if (labelToolTip.TryGetValue(ObjectName, out var question))
            {
                rValue = question?.Trim();
            }
            return rValue?.Length > 0 ? rValue : ExtractLastElement(ObjectName);
        }

        public async Task ShowTooltip(ElementReference elementReference, string ObjectName)
        {
            TooltipOptions options = new TooltipOptions();
            options.Style = "max-width: 300px; white-space: normal;";
            options.Duration = null;
            string ToolTip = await GetToolTip(ObjectName);
            tooltipService.Open(elementReference,ToolTip , options);
        }
        
        

        public async Task OpenSysDoc(string ObjectName)
        {
            var parameters = new Dictionary<string, object>
            {
                { "ObjectName", ObjectName }
            };

            await DialogService.OpenAsync<SysDocModal>($"Documentation {ObjectName}",
                parameters,
                new DialogOptions() { Width = "1200px", Height = "760px", Resizable = true, Draggable = true });
        }

        private string ExtractLastElement(string objectName)
        {
            // Assuming elements are separated by brackets, split by ']' and take the last non-empty segment
            var segments = objectName.Split(new[] { ']', '[', '.' }, StringSplitOptions.RemoveEmptyEntries);
            return segments.LastOrDefault();
        }


        public void HideNotification()
        {
            NotificationService.Messages.Clear();
        }

        public void ShowNotification(string message)
        {
            RenderFragment ab = CreateProgressBarWithDateTime(message);
            NotificationMessage msg = new NotificationMessage();
            //NotificationService.Notify(message);
            msg = new NotificationMessage
            {
                //Severity = NotificationSeverity.Info,
                Style = "position: absolute; left: -1000px; top: 300px;",
                Duration = 40000,
                //SummaryContent = ns => @<RadzenText TextStyle="TextStyle.H6">Refreshing Description</RadzenText>,
                DetailContent = ns => @ab
            };

            NotificationService.Notify(msg);
        }

        public void ShowRegularNotification(string titile, string message)
        {
            NotificationService.Notify(NotificationSeverity.Success, titile, message);
        }
       

        public RenderFragment CreateProgressBarWithDateTime(string message)
        {
            // Create a RenderFragment using a delegate that takes a RenderTreeBuilder
            RenderFragment renderFragment = builder =>
            {
                // Adding RadzenProgressBarCircular component
                builder.OpenComponent<RadzenProgressBarCircular>(0);
                builder.AddAttribute(1, "ProgressBarStyle", ProgressBarStyle.Primary);
                builder.AddAttribute(2, "Value", 100.0);
                builder.AddAttribute(3, "ShowValue", false);
                builder.AddAttribute(4, "Mode", ProgressBarMode.Indeterminate);
                builder.CloseComponent();

                // Adding RadzenText component for displaying the current date and time
                builder.OpenComponent<RadzenText>(5);
                builder.AddAttribute(6, "TextStyle", TextStyle.H6);
                builder.AddAttribute(7, "ChildContent", new RenderFragment((childBuilder) =>
                {
                    childBuilder.AddContent(8, $"{message}");
                    //childBuilder.AddMarkupContent(9, "<br />");
                    //childBuilder.AddContent(10, DateTime.Now.ToString());
                }));
                builder.CloseComponent();
            };

            return renderFragment;
        }


    }
}
