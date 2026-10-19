namespace AspireDeployService.Components.Pages;

// The infrastructure check page (ping, line flood, dialog queue) is a development aid. Release builds
// compile the component but give it no route, so it cannot be reached.
#if DEBUG
[Microsoft.AspNetCore.Components.Route("/debug/runs")]
#endif
public partial class DebugRuns
{
}
