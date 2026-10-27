using FamilyFeud.Services;
using FamilyFeud.Services.Hasura;
using Microsoft.AspNetCore.Components;
using System.Diagnostics;

namespace FamilyFeud.Components.Pages
{
    public partial class Game : IDisposable
    {
        [Inject]
        private NavigationManager Navigation { get; set; } = null!;

        [Inject]
        private GameStateService GameState { get; set; } = null!;

        // Questions not yet asked this game, worked out by the shared game state each time so it survives this
        // page being re-created and drops questions deleted mid-game straight away (#32)
        private List<Question> RemainingQuestions => GameState.RemainingQuestions;

        // Current question shown in UI; read from the shared state so the host and audience views always match (#14)
        private Question? CurrentQuestion => GameState.CurrentQuestion;

        // Short info message for the host, e.g. when the selected category has run out of questions (#14, #28)
        private string? InfoMessage { get; set; }

        // Categories for optional filtering (includes "All")
        private List<string> Categories { get; set; } = new List<string>();

        // Selected category for filtering; "All" means no filtering
        private string SelectedCategory { get; set; } = "All";

        // The audience window, kept so it can be closed again (toggle, Back, or leaving this page)
        private Window? _audienceWindow;

        // Whether the Play/Pass modal is showing, which blocks the rest of the page
        private bool IsChoiceModalOpen => GameState.PlayoffAwaitingChoice && GameState.WhichTeamsTurn != SelectedTeam.Random;

        // Footer actions that ask for confirmation while a round is in progress (#13)
        private enum ConfirmAction { None, LoadQuestion, RevealAll }

        // The action waiting for the host to confirm; None means the confirmation modal is closed
        private ConfirmAction PendingConfirm { get; set; } = ConfirmAction.None;

        // Strike only makes sense during a live round with no modal open
        private bool CanStrike => CurrentQuestion != null
            && !IsChoiceModalOpen
            && PendingConfirm == ConfirmAction.None
            && !GameState.IsRoundFinished
            && !GameState.IsGameOver;

        // A round is in progress once a question is loaded, the round isn't finished, some answers are still
        // hidden, and at least one answer has been revealed, a strike or face-off answer given, or play has moved past the face-off
        private bool IsRoundInProgress
        {
            get
            {
                if (CurrentQuestion == null || GameState.IsRoundFinished) return false;

                bool anyRevealed = CurrentQuestion.Answers.Any(a => GameState.IsAnswerRevealed(CurrentQuestion.Id, a.Id));
                bool allRevealed = CurrentQuestion.Answers.All(a => GameState.IsAnswerRevealed(CurrentQuestion.Id, a.Id));
                if (allRevealed) return false;

                bool anyStrike = GameState.Team1.IncorrectStrikes > 0
                    || GameState.Team2.IncorrectStrikes > 0
                    || GameState.Team1.FirstAnswer != null
                    || GameState.Team2.FirstAnswer != null
                    || GameState.Mode != GameMode.PlayOff;

                return anyRevealed || anyStrike;
            }
        }

        // Exposed for UI so button text can reflect open/closed state
        private bool IsAudienceOpen => _audienceWindow != null;

        protected override async Task OnInitializedAsync()
        {
            // Ensure questions are loaded in the shared game state
            if (GameState.Questions == null || GameState.Questions.Count == 0)
            {
                await GameState.LoadQuestionsAsync();
            }

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

        // Remaining questions in the selected category ("All" means every remaining question). LoadQuestion and the
        // footer count both use this so they always agree (#31). Categories are trimmed and compared ignoring case,
        // the same way BuildCategories groups them for the dropdown.
        private List<Question> QuestionsInSelectedCategory()
        {
            if (string.IsNullOrEmpty(SelectedCategory) || SelectedCategory == "All")
                return RemainingQuestions;

            return RemainingQuestions
                .Where(q => string.Equals((q.Category ?? string.Empty).Trim(), SelectedCategory, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        // "Round X of N" or "First to N points", shown under the round pool
        private string GameProgressText => GameState.GameLength == GameLengthMode.Rounds
            ? $"Round {GameState.CurrentRoundNumber} of {GameState.TotalRounds}"
            : $"First to {GameState.TargetScore} points (round {GameState.CurrentRoundNumber})";

        private async Task LoadQuestion()
        {
            // no new rounds once the game is over
            if (GameState.IsGameOver) return;
            InfoMessage = null;

            // Only load from Hasura if nothing was ever loaded (e.g. the first load failed). Never refill the
            // remaining pool from the cache mid-game, because that would repeat questions already asked (#28).
            if (GameState.Questions == null || GameState.Questions.Count == 0)
            {
                await ReloadQuestionsAsync();
                // the load failed: the error alert with its Retry button says so, no need for "No questions left" (#15)
                if (HasLoadError) return;
                // nothing in Hasura at all: the "No questions found" message already says so (#33)
                if (NoQuestionsFound) return;
            }

            if (RemainingQuestions.Count == 0)
            {
                // keep the current question (host and audience stay in sync)
                InfoMessage = "No questions left";
                StateHasChanged();
                return;
            }

            // Filter by selected category if applicable
            List<Question> pool = QuestionsInSelectedCategory();

            if (pool.Count == 0)
            {
                // No questions left in the selected category; fall back to any remaining question (#14)
                InfoMessage = $"No questions left in {SelectedCategory} — picked from all categories";
                pool = RemainingQuestions;
            }

            Random rand = new Random();
            Question next = pool[rand.Next(0, pool.Count)];

            // Update shared game state so the audience window displays the same question. This also marks it
            // as asked, which takes it out of RemainingQuestions so it won't be repeated.
            GameState.SetCurrentQuestion(next);

            StateHasChanged();
        }

        // Loading questions from Hasura failed; the message comes from the shared game state (#15)
        private bool HasLoadError => !GameState.IsLoading && !string.IsNullOrEmpty(GameState.ErrorMessage);

        // The load worked but Hasura has no questions at all, so the board would be empty with no hint why (#33)
        private bool NoQuestionsFound => !GameState.IsLoading && !HasLoadError && GameState.Questions.Count == 0;

        private void GoToQuestions() => Navigation.NavigateTo("/questions");

        // Fetch the questions from Hasura and rebuild the categories from them. RemainingQuestions follows
        // automatically and still leaves out questions already asked this game.
        private async Task ReloadQuestionsAsync()
        {
            await GameState.LoadQuestionsAsync();
            BuildCategories();
        }

        // Retry button on the load error alert (#15). Like Load Question, only load from Hasura if nothing was
        // ever loaded, so a retry can never refill the pool with questions already asked (#28).
        private async Task RetryLoadQuestions()
        {
            if (GameState.IsLoading) return;
            if (GameState.Questions != null && GameState.Questions.Count > 0) return;
            InfoMessage = null;
            await ReloadQuestionsAsync();
            StateHasChanged();
        }

        // Load Question button: ask first if this would abandon a round in progress
        private async Task RequestLoadQuestion()
        {
            if (IsChoiceModalOpen || GameState.IsGameOver || GameState.IsLoading) return;
            // nothing to load: say so straight away instead of asking to abandon the round first (#28)
            if (RemainingQuestions.Count == 0 && GameState.Questions.Count > 0)
            {
                InfoMessage = "No questions left";
                return;
            }
            if (IsRoundInProgress)
            {
                PendingConfirm = ConfirmAction.LoadQuestion;
                return;
            }
            await LoadQuestion();
        }

        // Reveal All Answers button: ask first if this would spoil a round in progress
        private void RequestRevealAll()
        {
            if (IsChoiceModalOpen || CurrentQuestion == null) return;
            if (IsRoundInProgress)
            {
                PendingConfirm = ConfirmAction.RevealAll;
                return;
            }
            GameState.RevealAllAnswers(CurrentQuestion.Id);
        }

        private async Task ConfirmPendingAction()
        {
            ConfirmAction action = PendingConfirm;
            PendingConfirm = ConfirmAction.None;

            if (action == ConfirmAction.LoadQuestion)
            {
                await LoadQuestion();
            }
            else if (action == ConfirmAction.RevealAll && CurrentQuestion != null)
            {
                GameState.RevealAllAnswers(CurrentQuestion.Id);
            }
        }

        private void CancelPendingAction() => PendingConfirm = ConfirmAction.None;

        private void RevealAnswer(Answer a)
        {
            if (CurrentQuestion == null) return;
            GameState.RevealAnswer(CurrentQuestion.Id, a.Id);
        }

        private void OpenGameWindow()
        {
            // Toggle a second MAUI Window that hosts AudiencePage, whose BlazorWebView mounts Audience.razor.
            // Its BlazorWebView resolves the same singleton GameStateService (registered in MauiProgram), so
            // the audience display shows the same question, answers and scores as this host page.
            try
            {
                // If already open, close it (toggle behavior)
                if (_audienceWindow != null)
                {
                    Window toClose = _audienceWindow;
                    _audienceWindow = null;
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        try { Application.Current?.CloseWindow(toClose); } catch { }
                    });
                    return;
                }

                // Create a window that hosts the audience-only page
                Window window = new Window(new AudiencePage()) { Title = "Audience" };
                _audienceWindow = window;
                window.Destroying += OnAudienceWindowDestroying;
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    Application.Current?.OpenWindow(window);
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"OpenGameWindow error: {ex}");
            }
        }

        // The audience window was closed with its own X button: forget it so the
        // toggle button opens a new one on the next click (#25).
        private void OnAudienceWindowDestroying(object? sender, EventArgs e)
        {
            if (sender is Window window) window.Destroying -= OnAudienceWindowDestroying;
            if (!ReferenceEquals(sender, _audienceWindow)) return;
            _audienceWindow = null;
            try { _ = InvokeAsync(StateHasChanged); } catch { }
        }

        public void Dispose()
        {
            try
            {
                GameState.StateChanged -= OnGameStateChanged;
                if (_audienceWindow != null)
                {
                    try { Application.Current?.CloseWindow(_audienceWindow); } catch { }
                    _audienceWindow = null;
                }
            }
            catch { }
        }

        private void GoBack()
        {
            // Close audience window before navigating back
            try { if (_audienceWindow != null) Application.Current?.CloseWindow(_audienceWindow); } catch { }
            _audienceWindow = null;
            // start the next game from scratch: scores, strikes, pool, question, turn and team names
            GameState.ResetGame();
            Navigation.NavigateTo("/startgame");
        }
    }
}
