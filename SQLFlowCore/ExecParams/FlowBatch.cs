#nullable enable
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Newtonsoft.Json;

namespace SQLFlowCore.ExecParams
{
    public class FlowBatch
    {

        /// <summary>
        /// Required field.
        /// </summary>
        public string Batch { get; set; } = "";

        /// <summary>
        /// Sysalias is an optional field and limits the batch to a certain system.
        /// </summary>
        [DefaultValue("")]
        public string? Sysalias { get; set; } = "";

        /// <summary>
        /// FlowType is an optional field and limits the batch to a certain type of flow.
        /// </summary>
        [DefaultValue("")]
        public string? FlowType { get; set; } = "";

        [DefaultValue("api")]
        public string ExecMode { get; set; } = "api";

        /// <summary>
        /// Dbg is an optional field defines the detail level (1-2) of the SQLFlow process log.
        /// </summary>
        [DefaultValue("1")]
        public string Dbg { get; set; } = "1";

        /// <summary>
        /// CallBackUri is an optional field automatically populated by Azure Data Factory.
        /// </summary>
        [DefaultValue("")]
        public string? CallBackUri { get; set; } = "";


        public static FlowBatch FromNameValueCollection(NameValueCollection collection)
        {
            var flowBatch = new FlowBatch();

            // Iterate over the properties and try to assign from the NameValueCollection
            foreach (var prop in typeof(FlowBatch).GetProperties())
            {
                var queryValue = collection[prop.Name];
                if (!string.IsNullOrEmpty(queryValue))
                {
                    // Convert the queryValue to the appropriate property type if needed
                    if (prop.PropertyType == typeof(int))
                    {
                        if (int.TryParse(queryValue, out int intValue))
                        {
                            prop.SetValue(flowBatch, intValue);
                        }
                    }
                    else if (prop.PropertyType == typeof(bool))
                    {
                        if (bool.TryParse(queryValue, out bool boolValue))
                        {
                            prop.SetValue(flowBatch, boolValue);
                        }
                    }
                    else
                    {
                        prop.SetValue(flowBatch, queryValue);
                    }
                }
                else if (prop.PropertyType == typeof(string))
                {
                    // Assign default value if available
                    var defaultValue = prop.GetCustomAttributes(typeof(DefaultValueAttribute), false)
                        .FirstOrDefault() as DefaultValueAttribute;
                    if (defaultValue != null)
                    {
                        prop.SetValue(flowBatch, defaultValue.Value);
                    }
                }
            }

            return flowBatch;
        }


        public static FlowBatch FromForm(Dictionary<string, string> form)
        {
            var flowBatch = new FlowBatch();

            // Iterate over the properties and try to assign from form data
            foreach (var prop in typeof(FlowBatch).GetProperties())
            {
                if (form.TryGetValue(prop.Name, out var formValue) && !string.IsNullOrEmpty(formValue))
                {
                    // Convert the formValue to the appropriate property type if needed
                    if (prop.PropertyType == typeof(int))
                    {
                        if (int.TryParse(formValue, out int intValue))
                        {
                            prop.SetValue(flowBatch, intValue);
                        }
                    }
                    else if (prop.PropertyType == typeof(bool))
                    {
                        if (bool.TryParse(formValue, out bool boolValue))
                        {
                            prop.SetValue(flowBatch, boolValue);
                        }
                    }
                    else
                    {
                        prop.SetValue(flowBatch, formValue);
                    }
                }
                else if (prop.PropertyType == typeof(string))
                {
                    // Assign default value if available
                    var defaultValue = prop.GetCustomAttributes(typeof(DefaultValueAttribute), false)
                        .FirstOrDefault() as DefaultValueAttribute;
                    if (defaultValue != null)
                    {
                        prop.SetValue(flowBatch, defaultValue.Value);
                    }
                }
            }

            return flowBatch;
        }

        public static FlowBatch FromJson(string json)
        {
            var flowBatch = JsonConvert.DeserializeObject<FlowBatch>(json);

            // CheckForError for null and assign default values if needed
            if (flowBatch != null)
            {
                foreach (var prop in typeof(FlowBatch).GetProperties())
                {
                    var value = prop.GetValue(flowBatch);
                    if (value == null && prop.PropertyType == typeof(string))
                    {
                        // Assign default value if available
                        var defaultValue = prop.GetCustomAttributes(typeof(DefaultValueAttribute), false)
                            .FirstOrDefault() as DefaultValueAttribute;
                        if (defaultValue != null)
                        {
                            prop.SetValue(flowBatch, defaultValue.Value);
                        }
                    }
                }
            }
            else
            {
                flowBatch = new FlowBatch(); // Create a new instance with default values if JSON is null or invalid
            }

            return flowBatch;
        }
    }
}

