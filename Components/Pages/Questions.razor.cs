using FamilyFeud.Services;
using FamilyFeud.Services.Hasura;
using Microsoft.AspNetCore.Components;
using System.Diagnostics;

namespace FamilyFeud.Components.Pages
{
    public partial class Questions
    {
        [Inject]
        private NavigationManager Navigation { get; set; } = null!;

        [Inject]
        private GameStateService GameState { get; set; } = null!;

        [Inject]
        private IHasuraService HasuraService { get; set; } = null!;

        // Delete confirmation modal state (#17)
        private Question? _pendingDelete;
        private bool _isDeleting;
        private string _deleteError = string.Empty;

        private bool IsLoading => GameState.IsLoading;
        private string ErrorMessage => GameState.ErrorMessage;
        private List<Question> AllQuestions => GameState.Questions;

        private string _searchText = string.Empty;
        private string _selectedCategory = string.Empty;

        private List<string> GetUniqueCategories()
        {
            return AllQuestions
                .Where(q => !string.IsNullOrWhiteSpace(q.Category))
                .Select(q => q.Category!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private List<Question> GetFilteredQuestions()
        {
            IEnumerable<Question> filtered = AllQuestions.AsEnumerable();

            // Filter by search text (case-insensitive substring match on question text)
            if (!string.IsNullOrWhiteSpace(_searchText))
            {
                filtered = filtered.Where(q => q.QuestionText.Contains(_searchText, StringComparison.OrdinalIgnoreCase));
            }

            // Filter by category (case-insensitive, matching the Game page)
            if (!string.IsNullOrEmpty(_selectedCategory))
            {
                filtered = filtered.Where(q => string.Equals(q.Category?.Trim(), _selectedCategory, StringComparison.OrdinalIgnoreCase));
            }

            return filtered.ToList();
        }

        protected override async Task OnInitializedAsync()
        {
            if (AllQuestions.Count == 0)
                await GameState.LoadQuestionsAsync();
        }

        private void GoBack()
        {
            Navigation.NavigateTo("/");
        }

        private void CreateNewQuestion(int? questionId = null)
        {
            if (questionId.HasValue)
            {
                Navigation.NavigateTo($"/new-question/{questionId}");
            }
            else
            {
                Navigation.NavigateTo("/new-question");
            }
        }

        private void EditQuestion(int questionId)
        {
            CreateNewQuestion(questionId);
        }

        private void NewQuestion()
        {
            CreateNewQuestion();
        }

        private void AskDelete(Question question)
        {
            _pendingDelete = question;
            _deleteError = string.Empty;
        }

        private void CancelDelete()
        {
            if (_isDeleting) return;
            _pendingDelete = null;
            _deleteError = string.Empty;
        }

        private async Task ConfirmDelete()
        {
            if (_pendingDelete == null || _isDeleting) return;

            int questionId = _pendingDelete.Id;
            _isDeleting = true;
            _deleteError = string.Empty;

            try
            {
                // Returns false if the question was already gone; either way it no longer exists,
                // so it is removed from the list below.
                await HasuraService.DeleteQuestionAsync(questionId);

                // This page's list is the shared GameState.Questions cache; removing through the service also tells
                // the other open listeners (the audience window).
                GameState.RemoveQuestionFromCache(questionId);
                _pendingDelete = null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error deleting question {questionId}: {ex}");
                _deleteError = $"Error deleting question: {ex.Message}";
            }
            finally
            {
                _isDeleting = false;
            }
        }
    }
}
