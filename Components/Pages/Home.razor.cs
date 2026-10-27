using Microsoft.AspNetCore.Components;
using System.Diagnostics;
using System.Reflection;
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
            Random random = new Random();
            return Colours[random.Next(Colours.Count)];
        }

        private string GetRandomAdjective()
        {
            Random random = new Random();
            return Adjectives[random.Next(Adjectives.Count)];
        }

        private void RandomiseAdjective()
        {
            Random random = new Random();
            string a = Adjectives[random.Next(Adjectives.Count)];
            Color c = Colours[random.Next(Colours.Count)];
            CurrentAdjective = a;
            CurrentAdjectiveColour = c;
        }

        private async Task LoadDataFromXml()
        {
            try
            {
                Assembly assembly = typeof(Home).Assembly;
                using (Stream? stream = assembly.GetManifestResourceStream("FamilyFeud.Resources.adjectives.xml"))
                {
                    if (stream != null)
                    {
                        using (StreamReader reader = new StreamReader(stream))
                        {
                            string xmlContent = await reader.ReadToEndAsync();
                            XDocument doc = XDocument.Parse(xmlContent);

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
                Debug.WriteLine($"Error loading XML: {ex.Message}");
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
