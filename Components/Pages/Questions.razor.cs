using FamilyFeud.Services.Hasura;
using Microsoft.AspNetCore.Components;

namespace FamilyFeud.Components.Pages
{
    public partial class Questions
    {
        [Inject]
        private NavigationManager Navigation { get; set; } = null!;

        [Inject]
        private FamilyFeud.Services.GameStateService GameState { get; set; } = null!;

        private bool _isLoading => GameState.IsLoading;
        private string _errorMessage => GameState.ErrorMessage;
        private List<Question> _questions => GameState.Questions;

        private string _searchText = string.Empty;
        private string _selectedCategory = string.Empty;

        private List<string> GetUniqueCategories()
        {
            return _questions
                .Where(q => !string.IsNullOrWhiteSpace(q.Category))
                .Select(q => q.Category!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private List<Question> GetFilteredQuestions()
        {
            var filtered = _questions.AsEnumerable();

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
            if (_questions.Count == 0)
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
    }
}
