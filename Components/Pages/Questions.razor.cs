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
                .Select(q => q.Category)
                .Distinct()
                .OrderBy(c => c)
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

            // Filter by category (if a specific category is selected)
            if (!string.IsNullOrEmpty(_selectedCategory))
            {
                filtered = filtered.Where(q => q.Category == _selectedCategory);
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
