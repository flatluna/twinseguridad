using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TwinSeguridad.Services;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

// Registrar servicios de la aplicación
builder.Services.AddSingleton<ImageVisionService>();
builder.Services.AddSingleton<PdfExtractionService>();
builder.Services.AddSingleton<IndiceExtractionService>();
builder.Services.AddSingleton<TextExportService>();
builder.Services.AddSingleton<NormaEstructuradaService>();
builder.Services.AddSingleton<NormaCosmosVectorService>();
builder.Services.AddSingleton<NormaTextoEstructuradoService>();
builder.Services.AddSingleton<NormaApendixService>();
builder.Services.AddSingleton<LeySeguridadCosmosVectorService>();
builder.Services.AddSingleton<LeySeguridadTrainingService>();
builder.Services.AddSingleton<LeySeguridadTrainingReadService>();
builder.Services.AddSingleton<ManualSeguridadService>();

builder.Build().Run();
