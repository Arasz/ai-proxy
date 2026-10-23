using AiProxy;
using AiProxy.Hosting;

if (GuardCommand.IsRequested(args))
{
    return GuardCommand.Run(args);
}

var builder = WebApplication.CreateBuilder(args);

var app = ProxyApplication.Create(builder);

app.Run();
return 0;
