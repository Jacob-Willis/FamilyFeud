using System.Reflection;
using System.Text.Json;

namespace FamilyFeud.Services.Hasura
{
    // Hasura connection settings, read from appsettings.json (git-ignored, embedded at build time).
    // Copy appsettings.example.json to appsettings.json and fill in the real values.
    public class HasuraSettings
    {
        public string Endpoint { get; set; } = "https://family-quiz.hasura.app/v1/graphql";
        public string AdminSecret { get; set; } = string.Empty;

        public static HasuraSettings Load()
        {
            Assembly assembly = typeof(HasuraSettings).Assembly;
            using Stream? stream = assembly.GetManifestResourceStream("FamilyFeud.appsettings.json");
            if (stream == null)
            {
                System.Diagnostics.Debug.WriteLine("appsettings.json not found - copy appsettings.example.json to appsettings.json and rebuild.");
                return new HasuraSettings();
            }

            using JsonDocument doc = JsonDocument.Parse(stream);
            if (!doc.RootElement.TryGetProperty("Hasura", out JsonElement hasura))
            {
                return new HasuraSettings();
            }

            return hasura.Deserialize<HasuraSettings>() ?? new HasuraSettings();
        }
    }
}
