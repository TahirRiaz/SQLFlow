using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using SQLFlowApi.Data;


namespace SQLFlowApi
{
    public partial class sqlflowProdService
    {
        sqlflowProdContext Context
        {
           get
           {
             return this.context;
           }
        }

        private readonly sqlflowProdContext context;
        private readonly NavigationManager navigationManager;

        public sqlflowProdService(sqlflowProdContext context, NavigationManager navigationManager)
        {
            this.context = context;
            this.navigationManager = navigationManager;
        }

        public void Reset() => Context.ChangeTracker.Entries().Where(e => e.Entity != null).ToList().ForEach(e => e.State = EntityState.Detached);

        

        
        }
}