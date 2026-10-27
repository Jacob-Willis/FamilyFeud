using FamilyFeud.Services;
using FamilyFeud.Services.Hasura;
using Microsoft.AspNetCore.Components;

namespace FamilyFeud.Components.Pages
{
    public partial class NewQuestion
    {
        [Inject]
        private NavigationManager Navigation { get; set; } = null!;

        [Inject]
        private IHasuraService HasuraService { get; set; } = null!;

        [Inject]
        private GameStateService GameState { get; set; } = null!;

        [Parameter]
        public int? QuestionId { get; set; }

        private string _questionText = string.Empty;
        private string _category = string.Empty;
        private List<AnswerInput> _answers = new();
        private string _message = string.Empty;
        private bool _isSuccess = false;
        private List<string> _categories = new();
        private bool _isLoading = false;
        private bool _isEditing = false;

        protected override async Task OnInitializedAsync()
        {
            // Check if we're editing an existing question
            if (QuestionId.HasValue && QuestionId.Value > 0)
            {
                _isEditing = true;
                await LoadQuestion(QuestionId.Value);
            }
            else
            {
                // Start with one empty answer for new questions
                _answers.Add(new AnswerInput());
            }

            // Load categories from existing questions
            await LoadCategories();
        }

        private async Task LoadQuestion(int questionId)
        {
            try
            {
                _isLoading = true;
                StateHasChanged();

                var question = await HasuraService.GetQuestionAsync(questionId);
                if (question != null)
                {
                    _questionText = question.QuestionText;
                    _category = question.Category ?? string.Empty;

                    // Populate answers from the loaded question, preserving IDs
                    _answers = question.Answers?.Select(a => new AnswerInput
                    {
                        Id = a.Id,
                        AnswerText = a.AnswerText,
                        Points = a.Points
                    }).ToList() ?? new List<AnswerInput> { new AnswerInput() };
                }
                else
                {
                    _message = "Question not found.";
                    _isLoading = false;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading question: {ex}");
                _message = $"Error loading question: {ex.Message}";
                _isLoading = false;
            }
            finally
            {
                _isLoading = false;
            }
        }

        private async Task LoadCategories()
        {
            // Skip if categories already loaded
            if (_categories.Count > 0)
                return;

            try
            {
                // Check if questions are already loaded in the shared game state
                List<Question> questions;
                if (GameState.Questions != null && GameState.Questions.Count > 0)
                {
                    questions = GameState.Questions;
                }
                else
                {
                    // Load questions from database if not already loaded
                    questions = await HasuraService.GetQuestionsAsync();
                }

                var cats = questions
                    .Where(q => !string.IsNullOrWhiteSpace(q.Category))
                    .Select(q => q.Category!.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(c => c)
                    .ToList();

                _categories = cats;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading categories: {ex}");
                _categories = new List<string>();
            }
        }

        private void AddAnswer()
        {
            if (_answers.Count >= 6) return;
            _answers.Add(new AnswerInput());
        }

        private void RemoveAnswer(int index)
        {
            if (_answers.Count > 1)
            {
                _answers.RemoveAt(index);
            }
            else
            {
                // If this is the last answer, clear it instead of removing
                _answers[index].AnswerText = string.Empty;
                _answers[index].Points = 0;
            }
        }

        private async Task OnAnswerPointsChanged(int index, ChangeEventArgs e)
        {
            if (int.TryParse(e.Value?.ToString(), out int points))
            {
                _answers[index].Points = points;
            }
            await UpdateAnswerOrder();
        }

        private async Task UpdateAnswerOrder()
        {
            await Task.Delay(100); // Debounce delay
            _answers = _answers.OrderByDescending(a => a.Points).ThenBy(a => a.AnswerText).ToList();
            StateHasChanged();
        }

        private async Task HandleSubmit()
        {
            _message = string.Empty;
            _isSuccess = false;

            // Validate input
            if (string.IsNullOrWhiteSpace(_questionText))
            {
                _message = "Question text is required.";
                return;
            }

            if (_answers.Count == 0 || _answers.TrueForAll(a => string.IsNullOrWhiteSpace(a.AnswerText)))
            {
                _message = "At least one answer is required.";
                return;
            }

            // Validate that all answers have non-empty text and points > 0
            var invalidAnswers = _answers.Where(a => string.IsNullOrWhiteSpace(a.AnswerText) || a.Points <= 0).ToList();
            if (invalidAnswers.Count > 0)
            {
                _message = "All answers must have text and points greater than 0.";
                return;
            }

            try
            {
                _isLoading = true;
                StateHasChanged();

                // Build Question object from form data
                var question = new Question
                {
                    QuestionText = _questionText.Trim(),
                    Category = string.IsNullOrWhiteSpace(_category) ? null : _category.Trim(),
                    Answers = _answers.Select(a => new Answer
                    {
                        Id = a.Id,
                        AnswerText = a.AnswerText.Trim(),
                        Points = a.Points
                    }).ToList()
                };

                if (_isEditing && QuestionId.HasValue)
                {
                    // Update existing question
                    question.Id = QuestionId.Value;
                    await HasuraService.UpdateQuestionAsync(question);

                    // Update local state
                    var existingQuestion = GameState.Questions.FirstOrDefault(q => q.Id == QuestionId.Value);
                    if (existingQuestion != null)
                    {
                        existingQuestion.QuestionText = question.QuestionText;
                        existingQuestion.Category = question.Category;
                        existingQuestion.Answers = question.Answers;
                    }

                    _message = $"Question updated successfully!";
                    _isSuccess = true;
                }
                else
                {
                    // Create new question
                    int createdId = await HasuraService.AddQuestionAsync(question);

                    if (createdId > 0)
                    {
                        // Update local state with returned id and add to shared GameState
                        question.Id = createdId;
                        GameState.Questions.Add(question);

                        _message = $"Question created successfully (ID: {createdId})!";
                        _isSuccess = true;
                    }
                    else
                    {
                        _message = "Failed to create question.";
                        _isSuccess = false;
                    }
                }

                // Navigate back after a short delay
                if (_isSuccess)
                {
                    await Task.Delay(1000);
                    Navigation.NavigateTo("/questions");
                }
            }
            catch (Exception ex)
            {
                _message = $"Error {(_isEditing ? "updating" : "creating")} question: {ex.Message}";
                _isSuccess = false;
            }
            finally
            {
                _isLoading = false;
                StateHasChanged();
            }
        }

        private void GoBack()
        {
            Navigation.NavigateTo("/questions");
        }

        private class AnswerInput
        {
            public int Id { get; set; } = 0;
            public string AnswerText { get; set; } = string.Empty;
            public int Points { get; set; } = 0;
        }
    }
}
