using ImageFunctions.Storage;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);

builder.Services.AddSingleton(static _ => BlobServiceClientFactory.CreateFromEnvironment());
builder.Services.AddSingleton<ImageBlobStore>();

builder.Build().Run();
