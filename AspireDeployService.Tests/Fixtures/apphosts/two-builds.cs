var builder = DistributedApplication.CreateBuilder(args);
builder.AddAzureContainerAppEnvironment("env");
builder.Build();
builder.Build().Run();
