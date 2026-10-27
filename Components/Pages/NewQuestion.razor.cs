using FamilyFeud.Services;
using FamilyFeud.Services.Hasura;
using Microsoft.AspNetCore.Components;
using System.Diagnostics;

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

        // Delete (#17): only offered once the question being edited has loaded
        private bool _questionLoaded = false;
        private bool _showDeleteConfirm = false;
        private bool _isDeleting = false;
        private string _deleteError = string.Empty;

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

                Question? question = await HasuraService.GetQuestionAsync(questionId);
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
                    _questionLoaded = true;
                }
                else
                {
                    _message = "Question not found.";
                    _isLoading = false;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error loading question: {ex}");
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

                List<string> cats = questions
                    .Where(q => !string.IsNullOrWhiteSpace(q.Category))
                    .Select(q => q.Category!.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(c => c)
                    .ToList();

                _categories = cats;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error loading categories: {ex}");
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
            // Short fixed pause before re-sorting. Nothing is debounced: every change waits and then re-sorts.
            await Task.Delay(100);
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
            List<AnswerInput> invalidAnswers = _answers.Where(a => string.IsNullOrWhiteSpace(a.AnswerText) || a.Points <= 0).ToList();
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
                Question question = new Question
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

                    // Update local state only if the game has already loaded questions;
                    // otherwise the Game page loads everything from Hasura later.
                    if (GameState.Questions.Count > 0)
                    {
                        Question? existingQuestion = GameState.Questions.FirstOrDefault(q => q.Id == QuestionId.Value);
                        if (existingQuestion != null)
                        {
                            // Reload so new answers get their real database IDs (form-built answers have Id 0)
                            Question source = await ReloadQuestionAsync(QuestionId.Value) ?? question;
                            existingQuestion.QuestionText = source.QuestionText;
                            existingQuestion.Category = source.Category;
                            existingQuestion.Answers = source.Answers;
                        }
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

                        // Only add to the shared list if the game has already loaded questions.
                        // Adding to an empty list would stop the Game page loading the rest from Hasura.
                        if (GameState.Questions.Count > 0)
                        {
                            // Reload so the answers have their real database IDs (form-built answers have Id 0)
                            Question savedQuestion = await ReloadQuestionAsync(createdId) ?? question;
                            GameState.Questions.Add(savedQuestion);
                        }

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

        /// <summary>
        /// Reloads a saved question from Hasura so its answers carry their real database IDs.
        /// Returns null if the reload fails; the save itself has already succeeded.
        /// </summary>
        private async Task<Question?> ReloadQuestionAsync(int questionId)
        {
            try
            {
                return await HasuraService.GetQuestionAsync(questionId);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error reloading question {questionId}: {ex}");
                return null;
            }
        }

        private void GoBack()
        {
            Navigation.NavigateTo("/questions");
        }

        private void AskDelete()
        {
            _deleteError = string.Empty;
            _showDeleteConfirm = true;
        }

        private void CancelDelete()
        {
            if (_isDeleting) return;
            _showDeleteConfirm = false;
            _deleteError = string.Empty;
        }

        private async Task ConfirmDelete()
        {
            if (!QuestionId.HasValue || _isDeleting) return;

            int questionId = QuestionId.Value;
            _isDeleting = true;
            _deleteError = string.Empty;

            try
            {
                // Returns false if the question was already gone; either way it no longer exists.
                await HasuraService.DeleteQuestionAsync(questionId);

                // Keep the shared cache in step (mirrors HandleSubmit's create/update handling).
                GameState.Questions.RemoveAll(q => q.Id == questionId);

                _showDeleteConfirm = false;
                Navigation.NavigateTo("/questions");
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

        private class AnswerInput
        {
            public int Id { get; set; } = 0;
            public string AnswerText { get; set; } = string.Empty;
            public int Points { get; set; } = 0;
        }
    }
}
