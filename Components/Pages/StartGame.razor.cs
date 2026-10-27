using FamilyFeud.Services;
using Microsoft.AspNetCore.Components;

namespace FamilyFeud.Components.Pages
{
    public partial class StartGame : IDisposable
    {
        [Inject]
        private NavigationManager Navigation { get; set; } = null!;

        [Inject]
        private GameStateService GameState { get; set; } = null!;

        private const int MaxTargetScore = 10000;

        // Game length chosen on this screen; copied into GameState when the game starts
        private GameLengthMode LengthMode { get; set; } = GameStateService.DefaultGameLength;
        private int TotalRounds { get; set; } = GameStateService.DefaultTotalRounds;
        private int? TargetScore { get; set; } = GameStateService.DefaultTargetScore;

        // Only checked in target score mode; an empty or out-of-range box blocks Start
        private bool IsTargetScoreValid =>
            LengthMode != GameLengthMode.TargetScore ||
            (TargetScore is int target && target >= 1 && target <= MaxTargetScore);

        // Return true when Start should be disabled: missing team names or an invalid target score
        private bool VerifyStart =>
            string.IsNullOrWhiteSpace(GameState.Team1.Name) ||
            string.IsNullOrWhiteSpace(GameState.Team2.Name) ||
            !IsTargetScoreValid;

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
        private SelectedTeam SelectedFirst
        {
            get => GameState.WhichTeamsTurn;
            set { GameState.SwitchTeam(value); }
        }

        private void PlayGame()
        {
            // Same checks as VerifyStart: both team names and a valid target score
            if (string.IsNullOrWhiteSpace(Team1Name) || string.IsNullOrWhiteSpace(Team2Name) || !IsTargetScoreValid)
            {
                // Start is disabled in this case, so this is only a safety net
                return;
            }

            // the target score is only used in target score mode, so keep the default when the box is unused
            int targetScore = LengthMode == GameLengthMode.TargetScore && TargetScore is int target ? target : GameStateService.DefaultTargetScore;
            GameState.SetGameLength(LengthMode, TotalRounds, targetScore);

            if (SelectedFirst == SelectedTeam.Random)
            {
                // Random (the default): pick the starting team at random
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
