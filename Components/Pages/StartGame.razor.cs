using Microsoft.AspNetCore.Components;

namespace FamilyFeud.Components.Pages
{
    public partial class StartGame
    : IDisposable
    {
        [Inject]
        private NavigationManager Navigation { get; set; } = null!;

        [Inject]
        private FamilyFeud.Services.GameStateService GameState { get; set; } = null!;

        // Return true when Start should be disabled: missing team names or no "who goes first" selected
        private bool VerifyStart =>
            string.IsNullOrWhiteSpace(GameState.Team1.Name) ||
            string.IsNullOrWhiteSpace(GameState.Team2.Name);


        private void GoBack()
        {
            Navigation.NavigateTo("/");
        }

        protected override void OnInitialized()
        {
            // Subscribe to GameState changes so the page re-renders when state updates
            GameState.StateChanged += OnGameStateChanged;
        }

        private void OnGameStateChanged()
        {
            InvokeAsync(StateHasChanged);
        }

        public void Dispose()
        {
            GameState.StateChanged -= OnGameStateChanged;
        }

        // Bind directly to the service Team model so values persist across pages
        private string Team1Name
        {
            get => GameState.Team1.Name;
            set { GameState.Team1.Name = value; }
        }

        private string Team2Name
        {
            get => GameState.Team2.Name;
            set { GameState.Team2.Name = value; }
        }

        // Which team goes first: SelectedTeam.Team1, SelectedTeam.Team2, or SelectedTeam.Random
        private FamilyFeud.Services.SelectedTeam SelectedFirst
        {
            get => GameState.WhichTeamsTurn;
            set { GameState.SwitchTeam(value); }
        }

        private void PlayGame()
        {
            // Basic validation: ensure teams have names and a first pick is selected
            if (string.IsNullOrWhiteSpace(Team1Name) || string.IsNullOrWhiteSpace(Team2Name))
            {
                // For now, do nothing. Could show validation UI.
                return;
            }

            if (SelectedFirst == FamilyFeud.Services.SelectedTeam.Random)
            {
                // If first not selected, default to random
                GameState.ChooseRandomFirst();
            }
            else
            {
                // remember the host's pick so later rounds alternate from it
                GameState.StartingTeam = SelectedFirst;
            }

            Navigation.NavigateTo("/game");
        }
    }
}
