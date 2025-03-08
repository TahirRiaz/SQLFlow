using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("SysPeriod", Schema = "flw")]
    public partial class SysPeriod
    {
        [Key]
        [Required]
        public int PeriodID { get; set; }

        public DateTime? Date { get; set; }

        public int? DayOfMonth { get; set; }

        public string DayOfWeekName { get; set; }

        public string DayOfWeekNameShort { get; set; }

        public int? DayOfWeekNumber { get; set; }

        public int? WeekOfYear { get; set; }

        public int? MonthNumber { get; set; }

        public string MonthName { get; set; }

        public string MonthNameShort { get; set; }

        public string MonthNumName { get; set; }

        public string Quarter { get; set; }

        public int? Year { get; set; }

        public bool? IsWeekend { get; set; }

        public bool? IsLeapYear { get; set; }

        public bool? IsLastDayOfMonth { get; set; }

        public int? FiscalWeekOfYear { get; set; }

        public int? FiscalMonth { get; set; }

        public int? FiscalQuarter { get; set; }

        public int? FiscalYear { get; set; }

        public bool? IsHoliday { get; set; }

        public string HolidayName { get; set; }

        public string Season { get; set; }

        public bool? DaylightSavingTime { get; set; }

        public int? ISOWeekNumber { get; set; }
    }
}