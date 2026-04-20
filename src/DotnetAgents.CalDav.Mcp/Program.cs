using DotnetAgents.CalDav.Mcp.Hosting;

var runner = new CalDavMcpRunner();
return await runner.RunAsync(CalDavEnvironmentMapper.MapFromEnvironment());