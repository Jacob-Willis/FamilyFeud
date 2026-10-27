namespace FamilyFeud.Services.Hasura
{
    public interface IHasuraService
    {
        Task<List<Question>> GetQuestionsAsync();
        Task<Question?> GetQuestionAsync(int id);
        Task<int> AddQuestionAsync(Question question);
        Task UpdateQuestionAsync(Question question);
        /// <summary>Deletes a question and its answers. Returns false if no question had this id.</summary>
        Task<bool> DeleteQuestionAsync(int id);
    }
}
