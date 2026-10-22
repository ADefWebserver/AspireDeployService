public static class Program
{
    public static void Main(string[] args)
    {
        IDistributedApplicationBuilder app = DistributedApplication.CreateBuilder(args);
        app.AddAzureContainerAppEnvironment("env");
        var host = app
            .Build();
        host.Run();
    }
}
