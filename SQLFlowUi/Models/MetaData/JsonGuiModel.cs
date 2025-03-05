﻿#nullable enable
using System.ComponentModel.DataAnnotations;

namespace SQLFlowUi.Models.MetaData
{
    public class JsonGuiModel
    {
        [Required]
        public string? Name { get; set; }

        [Required]
        public string? JsonValue { get; set; }
    }
}
