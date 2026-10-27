using FamilyFeud.Services;
using FamilyFeud.Services.Hasura;
using Microsoft.AspNetCore.Components;

namespace FamilyFeud.Components.Pages
{
    public partial class Game : IDisposable
    {
        [Inject]
        private NavigationManager Navigation { get; set; } = null!;

        [Inject]
        private FamilyFeud.Services.GameStateService GameState { get; set; } = null!;

        // Local copy of remaining questions so we can remove asked questions
        private List<Question> RemainingQuestions { get; set; } = new List<Question>();

        // Current question shown in UI
        private Question? CurrentQuestion { get; set; }

        // Categories for optional filtering (includes "All")
        private List<string> Categories { get; set; } = new List<string>();

        // Selected category for filtering; "All" means no filtering
        private string SelectedCategory { get; set; } = "All";

        // Window instance for the audience/play window so we can close it when this page is closed
        private Window? _audienceWindow;

        // Whether a play/pass choice modal should be shown and block other UI
        private bool IsChoiceModalOpen => GameState.PlayoffAwaitingChoice && GameState.WhichTeamsTurn != SelectedTeam.Random;



        // Exposed for UI so button text can reflect open/closed state
        private bool IsAudienceOpen => _audienceWindow != null;

        protected override async Task OnInitializedAsync()
        {
            // Ensure questions are loaded in the shared game state
            if (GameState.Questions == null || GameState.Questions.Count == 0)
            {
                await GameState.LoadQuestionsAsync();
            }

            // Copy questions locally so we can remove asked ones without mutating the shared list
            RemainingQuestions = new List<Question>(GameState.Questions ?? new List<Question>());
            BuildCategories();
            GameState.StateChanged += OnGameStateChanged;
        }

        private void OnGameStateChanged() => InvokeAsync(StateHasChanged);

        private void BuildCategories()
        {
            List<string> cats = (GameState.Questions ?? new List<Question>())
                .Where(q => !string.IsNullOrWhiteSpace(q.Category))
                .Select(q => q.Category!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(c => c)
                .ToList();

            Categories = new List<string> { "All" };
            Categories.AddRange(cats);

            if (!Categories.Contains(SelectedCategory))
                SelectedCategory = "All";
        }

        private async Task LoadQuestion()
        {
            // Refill remaining if exhausted
            if (RemainingQuestions == null || RemainingQuestions.Count == 0)
            {
                if (GameState.Questions == null || GameState.Questions.Count == 0)
                {
                    await GameState.LoadQuestionsAsync();
                }

                RemainingQuestions = new List<Question>(GameState.Questions ?? new List<Question>());
                BuildCategories();
            }

            // Filter by selected category if applicable
            List<Question> pool = string.IsNullOrEmpty(SelectedCategory) || SelectedCategory == "All"
                ? RemainingQuestions
                : RemainingQuestions.Where(q => string.Equals(q.Category ?? string.Empty, SelectedCategory, StringComparison.OrdinalIgnoreCase)).ToList();

            if (pool == null || pool.Count == 0)
            {
                // No questions in selected category; fallback to any remaining
                pool = RemainingQuestions;

                CurrentQuestion = null;
                return;
            }

            Random rand = new Random();
            int idx = rand.Next(0, pool.Count);
            CurrentQuestion = pool[idx];

            // Update shared game state so audience window can display the same question
            GameState.SetCurrentQuestion(CurrentQuestion);

            // Remove the asked question from remaining so it won't be repeated
            RemainingQuestions.RemoveAll(q => q.Id == CurrentQuestion.Id);

            StateHasChanged();
        }

        private void RevealAnswer(Answer a)
        {
            if (CurrentQuestion == null) return;
            GameState.RevealAnswer(CurrentQuestion.Id, a.Id);
        }

        private void OpenGameWindow()
        {
            // Open a second MAUI Window that hosts the same MainPage (which contains the BlazorWebView).
            // The BlazorWebView in the new window will resolve the application's singleton services
            // (registered in MauiProgram), so both windows share the same GameStateService instance and
            // will reflect the same question, answers and team scores.
            try
            {
                // If already open, close it (toggle behavior)
                if (_audienceWindow != null)
                {
                    Window toClose = _audienceWindow;
                    _audienceWindow = null;
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        try { Application.Current.CloseWindow(toClose); } catch { }
                    });
                    return;
                }

                // Create a window that hosts the audience-only page
                Window window = new Window(new AudiencePage()) { Title = "Audience" };
                _audienceWindow = window;
                // No remote server: just open the audience window on the host (projector)
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    Application.Current.OpenWindow(window);
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"OpenGameWindow error: {ex}");
            }
        }

        public void Dispose()
        {
            try
            {
                GameState.StateChanged -= OnGameStateChanged;
                if (_audienceWindow != null)
                {
                    try { Application.Current.CloseWindow(_audienceWindow); } catch { }
                    _audienceWindow = null;
                }
                // no remote server to stop
            }
            catch { }
        }

        private void GoBack()
        {
            // Close audience window before navigating back
            try { if (_audienceWindow != null) Application.Current.CloseWindow(_audienceWindow); } catch { }
            _audienceWindow = null;
            // start the next game from scratch: scores, strikes, pool, question, turn and team names
            GameState.ResetGame();
            Navigation.NavigateTo("/startgame");
        }
    }
}
