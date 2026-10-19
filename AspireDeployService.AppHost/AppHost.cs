var builder = DistributedApplication.CreateBuilder(args);

var app = builder.AddMauiProject("aspiredeployservice", "../AspireDeployService/AspireDeployService.csproj");
app.AddWindowsDevice();

builder.Build().Run();
