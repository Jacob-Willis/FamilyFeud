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

        // Every category that has a question in the cache (the dropdown lists "All categories" before these).
        // Worked out from GameState.Questions on every render, so categories added or emptied on the Questions
        // page show up straight away (#41). Categories with no unasked questions stay listed, so picking one
        // still gives the "No questions left in X" fallback (#14).
        private List<string> Categories => BuildCategories();

        // Dropdown value for "All categories" (no filtering). Real categories are trimmed and never empty, so a
        // question category that is literally called "All" can't clash with it and is treated like any other (#42).
        private const string AllCategories = "";

        // Whether a category value means "All categories"
        private static bool IsAllCategories(string? category) => string.IsNullOrEmpty(category);

        // Text shown for a category value: "All categories" for the sentinel, otherwise the category itself
        private static string CategoryLabel(string? category) => IsAllCategories(category) ? "All categories" : category!;

        // Whether a question belongs to a category value. The sentinel matches every question; otherwise the
        // question's category is trimmed and compared ignoring case, the same way BuildCategories groups them (#31).
        private static bool IsInCategory(Question q, string? category) =>
            IsAllCategories(category)
            || string.Equals((q.Category ?? string.Empty).Trim(), category, StringComparison.OrdinalIgnoreCase);

        // Selected category for filtering; AllCategories means no filtering. The host's pick is stored in the shared
        // game state, so it survives this page being re-created (#50). If the picked category no longer has any
        // questions in the cache it reads as AllCategories, so the dropdown and the filter never point at a
        // missing category (#41). The match ignores case, like IsInCategory (#31).
        private string SelectedCategory
        {
            get => ResolveCategory(GameState.SelectedCategory);
            set => GameState.SelectedCategory = value ?? AllCategories;
        }

        // A stored category value as it currently stands: the matching entry in Categories (ignoring case), or
        // AllCategories if that category no longer has any questions in the cache (#41, #44)
        private string ResolveCategory(string? category)
        {
            if (IsAllCategories(category)) return AllCategories;
            string? current = Categories.FirstOrDefault(c => string.Equals(c, category, StringComparison.OrdinalIgnoreCase));
            return current ?? AllCategories;
        }

        // The audience window, kept so it can be closed again (toggle, Back, or leaving this page)
        private Window? _audienceWindow;

        // Whether the Play/Pass modal is showing, which blocks the rest of the page
        private bool IsChoiceModalOpen => GameState.PlayoffAwaitingChoice && GameState.WhichTeamsTurn != SelectedTeam.Random;

        // Footer actions that ask for confirmation while a round is in progress (#13)
        private enum ConfirmAction { None, LoadQuestion, RevealAll }

        // The action waiting for the host to confirm; None means the confirmation modal is closed
        private ConfirmAction PendingConfirm { get; set; } = ConfirmAction.None;

        // The question the host picked in the "Choose question..." modal, kept while the confirmation modal asks
        // whether to abandon the round in progress; null means Load Question picks at random (#38)
        private Question? _chosenQuestion;

        // Whether the host-only "Choose question..." modal is open (#38). The audience window never shows it.
        private bool IsPickerOpen { get; set; }

        // Text typed into the picker's search box; filters the listed questions by their text or category (#44)
        private string PickerSearch { get; set; } = string.Empty;

        // The category picked in the picker's own dropdown (#44). It starts as the footer's category each time the
        // picker opens, but changing it never changes the footer: the picker is a one-off choice.
        private string _pickerCategory = AllCategories;

        // Picker category as it currently stands; reads as AllCategories if it no longer has questions (#41)
        private string PickerCategory
        {
            get => ResolveCategory(_pickerCategory);
            set => _pickerCategory = value ?? AllCategories;
        }

        // Strike only makes sense during a live round with no modal open
        private bool CanStrike => CurrentQuestion != null
            && !IsChoiceModalOpen
            && PendingConfirm == ConfirmAction.None
            && !IsPickerOpen
            && !GameState.IsRoundFinished
            && !GameState.IsGameOver;

        // A round is in progress once a question is loaded, the round isn't finished, some answers are still
        // hidden, and the round has had a scored reveal or a strike (including a double face-off strike, #38)
        private bool IsRoundInProgress
        {
            get
            {
                if (CurrentQuestion == null || GameState.IsRoundFinished || !GameState.RoundHasActivity) return false;

                return !CurrentQuestion.Answers.All(a => GameState.IsAnswerRevealed(CurrentQuestion.Id, a.Id));
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

            GameState.StateChanged += OnGameStateChanged;
        }

        private void OnGameStateChanged() => InvokeAsync(StateHasChanged);

        private List<string> BuildCategories()
        {
            List<string> cats = (GameState.Questions ?? new List<Question>())
                .Where(q => !string.IsNullOrWhiteSpace(q.Category))
                .Select(q => q.Category!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(c => c)
                .ToList();

            return cats;
        }

        // Remaining questions in the selected category (AllCategories means every remaining question). LoadQuestion
        // and the footer count both use this so they always agree (#31).
        private List<Question> QuestionsInSelectedCategory()
        {
            if (IsAllCategories(SelectedCategory))
                return RemainingQuestions;

            return RemainingQuestions
                .Where(q => IsInCategory(q, SelectedCategory))
                .ToList();
        }

        // "Round X of N", "First to N points (round X)" or both, shown under the round pool
        private string GameProgressText => GameState.GameProgressText;

        // Load the next question. With no chosen question one is picked at random from the selected category;
        // the "Choose question..." modal passes the host's pick instead (#38). Both go through the same checks.
        private async Task LoadQuestion(Question? chosen = null)
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

            Question next;
            if (chosen != null)
            {
                // the host's pick must still be unasked (and not deleted meanwhile), so it is never repeated (#38)
                Question? stillRemaining = RemainingQuestions.FirstOrDefault(q => q.Id == chosen.Id);
                if (stillRemaining == null)
                {
                    InfoMessage = "That question is no longer available";
                    StateHasChanged();
                    return;
                }
                next = stillRemaining;
            }
            else
            {
                // Filter by selected category if applicable
                List<Question> pool = QuestionsInSelectedCategory();

                if (pool.Count == 0)
                {
                    // No questions left in the selected category; fall back to any remaining question (#14)
                    InfoMessage = $"No questions left in {CategoryLabel(SelectedCategory)} — picked from all categories";
                    pool = RemainingQuestions;
                }

                Random rand = new Random();
                next = pool[rand.Next(0, pool.Count)];
            }

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

        // Fetch the questions from Hasura. The categories and RemainingQuestions follow automatically, and the
        // remaining pool still leaves out questions already asked this game.
        private async Task ReloadQuestionsAsync()
        {
            await GameState.LoadQuestionsAsync();
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

        // Load Question button (random), or a question picked in the "Choose question..." modal (#38): ask first
        // if this would abandon a round in progress
        private async Task RequestLoadQuestion(Question? chosen = null)
        {
            _chosenQuestion = null;
            if (IsChoiceModalOpen || GameState.IsGameOver || GameState.IsLoading) return;
            // nothing to load: say so straight away instead of asking to abandon the round first (#28)
            if (RemainingQuestions.Count == 0 && GameState.Questions.Count > 0)
            {
                InfoMessage = "No questions left";
                return;
            }
            if (IsRoundInProgress)
            {
                _chosenQuestion = chosen;
                PendingConfirm = ConfirmAction.LoadQuestion;
                return;
            }
            await LoadQuestion(chosen);
        }

        // Remaining (never already asked) questions in the picker's category (#38, #44)
        private List<Question> PickerQuestionsInCategory() =>
            RemainingQuestions.Where(q => IsInCategory(q, PickerCategory)).ToList();

        // Questions listed in the picker: the remaining questions in the picker's category, narrowed by the search
        // box. The search ignores case and matches the question text or its category (#38, #44).
        private List<Question> PickerQuestions()
        {
            List<Question> list = PickerQuestionsInCategory();
            string search = PickerSearch.Trim();
            if (search.Length == 0) return list;
            return list
                .Where(q => (q.QuestionText ?? string.Empty).Contains(search, StringComparison.OrdinalIgnoreCase)
                    || (q.Category ?? string.Empty).Contains(search, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        // "Choose question..." button: same guards as Load Question, then show the host-only picker (#38)
        private void OpenQuestionPicker()
        {
            if (IsChoiceModalOpen || PendingConfirm != ConfirmAction.None || GameState.IsGameOver || GameState.IsLoading) return;
            if (RemainingQuestions.Count == 0 && GameState.Questions.Count > 0)
            {
                InfoMessage = "No questions left";
                return;
            }
            PickerSearch = string.Empty;
            PickerCategory = SelectedCategory;
            IsPickerOpen = true;
        }

        private void CloseQuestionPicker() => IsPickerOpen = false;

        // A question was clicked in the picker: load it exactly like Load Question would, confirmation included
        private async Task PickQuestion(Question q)
        {
            IsPickerOpen = false;
            await RequestLoadQuestion(q);
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
            Question? chosen = _chosenQuestion;
            PendingConfirm = ConfirmAction.None;
            _chosenQuestion = null;

            if (action == ConfirmAction.LoadQuestion)
            {
                await LoadQuestion(chosen);
            }
            else if (action == ConfirmAction.RevealAll && CurrentQuestion != null)
            {
                GameState.RevealAllAnswers(CurrentQuestion.Id);
            }
        }

        private void CancelPendingAction()
        {
            PendingConfirm = ConfirmAction.None;
            _chosenQuestion = null;
        }

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
