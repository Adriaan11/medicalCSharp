using DiagnosisApi.Data;
using DiagnosisApi.Services;
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddSingleton<AzureOpenAIService>();

var app = builder.Build();

app.MapControllers();

app.Run();
