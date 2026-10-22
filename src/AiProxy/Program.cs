using AiProxy;

var builder = WebApplication.CreateBuilder(args);

var app = ProxyApplication.Create(builder);

app.Run();
