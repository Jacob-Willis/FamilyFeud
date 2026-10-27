namespace FamilyFeud.Services.Hasura
{
    public interface IHasuraService
    {
        Task<List<Question>> GetQuestionsAsync();
        Task<Question?> GetQuestionAsync(int id);
        Task<int> AddQuestionAsync(Question question);
        Task UpdateQuestionAsync(Question question);
        Task DeleteQuestionAsync(int id);
    }
}
