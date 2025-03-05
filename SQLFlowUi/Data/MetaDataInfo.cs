using SQLFlowCore.Utils.Period;
using SQLFlowUi.Data.MetaData;

namespace SQLFlowUi.Data
{
    public class MetaDataInfo
    {
        //buildDimPeriod = new SQLFlowCore.Services.ExecDimPeriod();

        public static IQueryable<DicObjectStrStr> GetCountry()
        {
            Dictionary<string, string> country = Country.CountryIso3166Alpha2;

            IQueryable<DicObjectStrStr> countryQueryable = country
                .Select(kv => new DicObjectStrStr { Key = kv.Key, Value = kv.Value })
                .AsQueryable();

            return countryQueryable;
        }

        public static IQueryable<DicObjectIntStr> GetAllObj()
        {
            Dictionary<int, string> allOptions = new Dictionary<int, string>();
            allOptions[0] = "New Objects";
            allOptions[1] = "All Objects";

            IQueryable<DicObjectIntStr> allOptionsQueryable = allOptions
                .Select(kv => new DicObjectIntStr { Key = kv.Key, Value = kv.Value })
                .AsQueryable();

            return allOptionsQueryable;
        }

        public static IQueryable<DicObjectStrStr> GetHolidayLang()
        {
            Dictionary<string, string> holidayLang = new Dictionary<string, string>()
            {
                { "no", "Norwegian" },
                { "en", "English" }
            };

            IQueryable<DicObjectStrStr> holidayLangQueryable = holidayLang
                .Select(kv => new DicObjectStrStr { Key = kv.Key, Value = kv.Value })
                .AsQueryable();

            return holidayLangQueryable;
        }


        public static IQueryable<DicObjectIntStr> GetFiscalStartMonth()
        {
            Dictionary<int, string> months = new Dictionary<int, string>()
            {
                { 1, "January" },
                { 2, "February" },
                { 3, "March" },
                { 4, "April" },
                { 5, "May" },
                { 6, "June" },
                { 7, "July" },
                { 8, "August" },
                { 9, "September" },
                { 10, "October" },
                { 11, "November" },
                { 12, "December" }
            };

            IQueryable<DicObjectIntStr> monthsQueryable = months
                .Select(kv => new DicObjectIntStr { Key = kv.Key, Value = kv.Value })
                .AsQueryable();

            return monthsQueryable;
        }

        public static Dictionary<string, string> CountryToHoliday = new Dictionary<string, string>();


    }
}
