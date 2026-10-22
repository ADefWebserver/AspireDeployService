var builder = DistributedApplication.CreateBuilder(args);

builder.AddAzureContainerAppEnvironment("env");

builder.AddProject<Projects.Api>("api");

builder.Build().Run();
