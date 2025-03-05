using System;

namespace SQLFlowCore.Common
{
    public class IncObject
    {
        internal string IncCol { get; set; }
        internal string DateCol { get; set; }

        internal string IncColCMD { get; set; }
        internal string DateColCMD { get; set; }

        internal string XML { get; set; }

        internal int IncColVal { get; set; }
        internal DateTime IncColValDT { get; set; }
        internal DateTime DateColVal { get; set; }

        internal bool IncColIsDate { get; set; }

        internal bool RunFullload { get; set; }


    }


}
