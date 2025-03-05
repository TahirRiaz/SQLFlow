using Microsoft.EntityFrameworkCore;


namespace SQLFlowApi.Data
{
    public partial class sqlflowProdContext : DbContext
    {
        public sqlflowProdContext()
        {
        }

        public sqlflowProdContext(DbContextOptions<sqlflowProdContext> options) : base(options)
        {
        }

        partial void OnModelBuilding(ModelBuilder builder);

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            
        }

       
        protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        {
            
        }
    
    }
}