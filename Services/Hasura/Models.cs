using System.Text.Json.Serialization;

namespace FamilyFeud.Services.Hasura
{
    public class Question
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("question")]
        public string QuestionText { get; set; } = string.Empty;

        [JsonPropertyName("category")]
        public string? Category { get; set; }

        [JsonPropertyName("answers")]
        public List<Answer> Answers { get; set; } = new();
    }

    public class Answer
    {
        public static readonly Answer Blank = new Answer { Id = -1, QuestionId = -1, AnswerText = "", Points = 0 };

        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("question_id")]
        public int QuestionId { get; set; }

        [JsonPropertyName("answer")]
        public string AnswerText { get; set; } = string.Empty;

        [JsonPropertyName("points")]
        public int Points { get; set; }

        [JsonIgnore]
        public bool IsBlank => Id == -1;
    }

    // GraphQL request/response helpers
    internal class GraphQLRequest
    {
        [JsonPropertyName("query")]
        public string Query { get; set; } = string.Empty;

        [JsonPropertyName("variables")]
        public object? Variables { get; set; }
    }

    internal class GraphQLError
    {
        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("extensions")]
        public object? Extensions { get; set; }
    }

    internal class GraphQLResponse<T>
    {
        [JsonPropertyName("data")]
        public T? Data { get; set; }

        [JsonPropertyName("errors")]
        public List<GraphQLError>? Errors { get; set; }
    }
}
