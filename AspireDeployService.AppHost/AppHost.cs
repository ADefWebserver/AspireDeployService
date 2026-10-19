var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.AspireDeployService>("aspiredeployservice");

builder.Build().Run();
