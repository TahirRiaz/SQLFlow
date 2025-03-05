namespace SQLFlowUi.Controllers
{
    public class GenFlowId
    {
        public static int Get()
        {
            Random random = new Random();
            return random.Next(100000, 10000000 + 1);
        }
    }
}
