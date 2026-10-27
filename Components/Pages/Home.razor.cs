using Microsoft.AspNetCore.Components;
using System.Xml.Linq;

namespace FamilyFeud.Components.Pages
{
    public partial class Home
    {
        [Inject]
        private NavigationManager Navigation { get; set; } = null!;

        private List<string> Adjectives { get; set; } = new();
        private List<Color> Colours { get; set; } = new();
        private string CurrentAdjective { get; set; } = "Inquisitive";
        private Color CurrentAdjectiveColour { get; set; } = Color.FromArgb("#ffff6b6b");
        private string CurrentAdjectiveColourHex => CurrentAdjectiveColour.ToHex();

        protected override async Task OnInitializedAsync()
        {
            await LoadDataFromXml();
        }

        private Color GetRandomColour()
        {
            var random = new Random();
            return Colours[random.Next(Colours.Count)];
        }

        private string GetRandomAdjective()
        {
            var random = new Random();
            return Adjectives[random.Next(Adjectives.Count)];
        }

        private void RandomiseAdjective()
        {
            var random = new Random();
            string a = Adjectives[random.Next(Adjectives.Count)];
            Color c = Colours[random.Next(Colours.Count)];
            CurrentAdjective = a;
            CurrentAdjectiveColour = c;
        }

        private async Task LoadDataFromXml()
        {
            try
            {
                var assembly = typeof(Home).Assembly;
                using (var stream = assembly.GetManifestResourceStream("FamilyFeud.Resources.adjectives.xml"))
                {
                    if (stream != null)
                    {
                        using (var reader = new StreamReader(stream))
                        {
                            string xmlContent = await reader.ReadToEndAsync();
                            var doc = XDocument.Parse(xmlContent);

                            Adjectives = doc.Descendants("adjective")
                                .Select(x => x.Value)
                                .ToList();

                            Colours = doc.Descendants("colour")
                                .Select(x => Color.FromArgb(x.Value))
                                .ToList();
                        }
                    }
                }
                if (Adjectives.Count > 0 && Colours.Count > 0)
                {
                    CurrentAdjective = GetRandomAdjective();
                    CurrentAdjectiveColour = GetRandomColour();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading XML: {ex.Message}");
            }
        }

        private void StartGame()
        {
            Navigation.NavigateTo("/startgame");
        }

        private void ViewQuestions()
        {
            Navigation.NavigateTo("/questions");
        }
    }
}
