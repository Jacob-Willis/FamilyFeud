namespace FamilyFeud.Services.Hasura
{
    public interface IHasuraService
    {
        Task<List<Question>> GetQuestionsAsync();
        Task<Question?> GetQuestionAsync(int id);
        Task<int> AddQuestionAsync(Question question);
        /// <summary>
        /// Saves a question and its answers. Returns false, without saving or recreating anything,
        /// if no question has this id (e.g. it was deleted elsewhere after it was opened).
        /// </summary>
        Task<bool> UpdateQuestionAsync(Question question);
        /// <summary>Deletes a question and its answers. Returns false if no question had this id.</summary>
        Task<bool> DeleteQuestionAsync(int id);
    }
}
