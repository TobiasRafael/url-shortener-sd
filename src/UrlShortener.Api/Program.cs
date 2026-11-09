using Scalar.AspNetCore;
using UrlShortener.Api.Endpoints;
using UrlShortener.Api.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddUrlShortenerApi();

var app = builder.Build();

app.UseUrlShortenerApi();

app.MapOpenApi();
app.MapScalarApiReference(options => options.Title = "URL Shortener API");
app.MapDefaultEndpoints();

app.MapLinkEndpoints();
app.MapUserEndpoints();
app.MapAnalyticsEndpoints();
app.MapRedirectEndpoints(); // last: "/{code}" is the catch-all for short links

app.Run();

/// <summary>Entry point, exposed for <c>WebApplicationFactory</c> in the API tests.</summary>
public partial class Program;
