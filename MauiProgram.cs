using Microsoft.Extensions.Logging;

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
            // Register application state service for teams and scores (inject Hasura service)
            builder.Services.AddSingleton<FamilyFeud.Services.GameStateService>(sp =>
                new FamilyFeud.Services.GameStateService(sp.GetRequiredService<FamilyFeud.Services.Hasura.IHasuraService>()));

            // Register Hasura GraphQL service and HttpClient
            // Endpoint and admin secret come from appsettings.json (git-ignored) - see appsettings.example.json
            builder.Services.AddSingleton<FamilyFeud.Services.Hasura.IHasuraService>(sp =>
            {
                FamilyFeud.Services.Hasura.HasuraSettings settings = FamilyFeud.Services.Hasura.HasuraSettings.Load();
                HttpClient client = new System.Net.Http.HttpClient();
                client.BaseAddress = new Uri(settings.Endpoint);
                client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
                client.DefaultRequestHeaders.Add("x-hasura-role", "app_full_access");
                if (!string.IsNullOrEmpty(settings.AdminSecret))
                {
                    client.DefaultRequestHeaders.Add("x-hasura-admin-secret", settings.AdminSecret);
                }
                return new FamilyFeud.Services.Hasura.HasuraService(client);
            });

#if DEBUG
            builder.Services.AddBlazorWebViewDeveloperTools();
            builder.Logging.AddDebug();
#endif

            return builder.Build();
        }
    }
}
