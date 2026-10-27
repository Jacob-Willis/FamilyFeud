using FamilyFeud.Services;
using FamilyFeud.Services.Hasura;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;

namespace FamilyFeud
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            MauiAppBuilder builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                });

            builder.Services.AddMauiBlazorWebView();
            // Shared game state (singleton, so the host and audience windows see the same game)
            builder.Services.AddSingleton<GameStateService>(sp =>
                new GameStateService(sp.GetRequiredService<IHasuraService>()));

            // Register Hasura GraphQL service and HttpClient
            // Endpoint and admin secret come from appsettings.json (git-ignored) - see appsettings.example.json
            builder.Services.AddSingleton<IHasuraService>(sp =>
            {
                HasuraSettings settings = HasuraSettings.Load();
                HttpClient client = new HttpClient();
                client.BaseAddress = new Uri(settings.Endpoint);
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                client.DefaultRequestHeaders.Add("x-hasura-role", "app_full_access");
                if (!string.IsNullOrEmpty(settings.AdminSecret))
                {
                    client.DefaultRequestHeaders.Add("x-hasura-admin-secret", settings.AdminSecret);
                }
                return new HasuraService(client);
            });

#if DEBUG
            builder.Services.AddBlazorWebViewDeveloperTools();
            builder.Logging.AddDebug();
#endif

            return builder.Build();
        }
    }
}
