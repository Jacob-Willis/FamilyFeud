using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FamilyFeud.Services.Hasura
{
    public class HasuraService : IHasuraService
    {
        private readonly HttpClient _httpClient;
        private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

        public HasuraService(HttpClient httpClient)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        }

        public async Task<List<Question>> GetQuestionsAsync()
        {
            string query = @"
query GetQuestions {
  questions {
    id
    question
    category
    answers(order_by: {id: asc}) {
      id
      question_id
      answer
      points
    }
  }
}";
            GraphQLRequest req = new GraphQLRequest { Query = query };
            QuestionsListResponse? res = await SendRequestAsync<QuestionsListResponse>(req).ConfigureAwait(false);
            return res?.Questions ?? new List<Question>();
        }

        public async Task<Question?> GetQuestionAsync(int id)
        {
            string query = @"
query GetQuestion($id: Int!) {
  questions_by_pk(id: $id) {
    id
    question
    category
    answers(order_by: {id: asc}) {
      id
      question_id
      answer
      points
    }
  }
}";
            GraphQLRequest req = new GraphQLRequest { Query = query, Variables = new { id } };
            QuestionResponse? res = await SendRequestAsync<QuestionResponse>(req).ConfigureAwait(false);
            return res?.QuestionsByPk;
        }

        public async Task<int> AddQuestionAsync(Question question)
        {
            if (question == null) throw new ArgumentNullException(nameof(question));

            string mutation = @"
mutation InsertQuestion($question: String!, $category: String, $answers: [answers_insert_input!]!) {
  insert_questions_one(object: { question: $question, category: $category, answers: { data: $answers } }) {
    id
  }
}";
            var answersInput = question.Answers.Select(a => new
            {
                answer = a.AnswerText,
                points = a.Points
            }).ToList();

            var variables = new
            {
                question = question.QuestionText,
                category = question.Category,
                answers = answersInput
            };

            GraphQLRequest req = new GraphQLRequest { Query = mutation, Variables = variables };
            InsertQuestionResponse? res = await SendRequestAsync<InsertQuestionResponse>(req).ConfigureAwait(false);
            return res?.InsertQuestionsOne?.Id ?? 0;
        }

        public async Task UpdateQuestionAsync(Question question)
        {
            if (question == null) throw new ArgumentNullException(nameof(question));

            // Separate answers into existing (with IDs) and new (without IDs)
            var existingAnswers = question.Answers.Where(a => a.Id > 0).ToList();
            var newAnswers = question.Answers.Where(a => a.Id == 0).ToList();
            var existingAnswerIds = existingAnswers.Select(a => a.Id).ToList();

            // Build new answers input (without IDs since insert_input cannot have primary keys)
            var newAnswersInput = newAnswers.Select(a => new
            {
                question_id = question.Id,
                answer = a.AnswerText,
                points = a.Points
            }).ToList();

            // Build GraphQL mutation dynamically
            var mutationBuilder = new System.Text.StringBuilder();
            mutationBuilder.AppendLine("mutation UpdateQuestion($id: Int!, $question: String!, $category: String, $existingAnswerIds: [Int!]!" + (newAnswersInput.Count > 0 ? ", $newAnswersData: [answers_insert_input!]!" : "") + ") {");
            mutationBuilder.AppendLine("  update_questions_by_pk(pk_columns: { id: $id }, _set: { question: $question, category: $category }) {");
            mutationBuilder.AppendLine("    id");
            mutationBuilder.AppendLine("  }");
            mutationBuilder.AppendLine("  delete_answers(where: { question_id: { _eq: $id }, id: { _nin: $existingAnswerIds } }) {");
            mutationBuilder.AppendLine("    affected_rows");
            mutationBuilder.AppendLine("  }");

            // Add update mutations for each existing answer
            for (int i = 0; i < existingAnswers.Count; i++)
            {
                var answer = existingAnswers[i];
                mutationBuilder.AppendLine($"  update_answer_{i}: update_answers_by_pk(pk_columns: {{ id: {answer.Id} }}, _set: {{ answer: \"{EscapeGraphQLString(answer.AnswerText)}\", points: {answer.Points} }}) {{");
                mutationBuilder.AppendLine("    affected_rows");
                mutationBuilder.AppendLine("  }");
            }

            // Add insert mutation for new answers only if there are any
            if (newAnswersInput.Count > 0)
            {
                mutationBuilder.AppendLine("  insert_answers(objects: $newAnswersData) {");
                mutationBuilder.AppendLine("    affected_rows");
                mutationBuilder.AppendLine("  }");
            }

            mutationBuilder.AppendLine("}");

            var variables = new
            {
                id = question.Id,
                question = question.QuestionText,
                category = question.Category,
                existingAnswerIds = existingAnswerIds,
                newAnswersData = newAnswersInput
            };

            GraphQLRequest req = new GraphQLRequest { Query = mutationBuilder.ToString(), Variables = variables };
            await SendRequestAsync<GenericMutationResponse>(req).ConfigureAwait(false);
        }

        private string EscapeGraphQLString(string input)
        {
            if (string.IsNullOrEmpty(input))
                return string.Empty;

            return input.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }

        public async Task<bool> DeleteQuestionAsync(int id)
        {
            // Delete the answers first so the delete doesn't depend on an FK cascade.
            // Hasura runs both root fields of one mutation in a single transaction, in order.
            string mutation = @"
mutation DeleteQuestion($id: Int!) {
  delete_answers(where: { question_id: { _eq: $id } }) {
    affected_rows
  }
  delete_questions_by_pk(id: $id) {
    id
  }
}";
            GraphQLRequest req = new GraphQLRequest { Query = mutation, Variables = new { id } };
            DeleteQuestionResponse? res = await SendRequestAsync<DeleteQuestionResponse>(req).ConfigureAwait(false);
            // Null means no question had this id (e.g. it was already deleted).
            return res?.DeleteQuestionsByPk != null;
        }

        // Generic request sender with error handling
        private async Task<T?> SendRequestAsync<T>(GraphQLRequest request)
            where T : class
        {
            using HttpResponseMessage httpResponse = await _httpClient.PostAsJsonAsync(string.Empty, request, _jsonOptions).ConfigureAwait(false);

            if (!httpResponse.IsSuccessStatusCode)
            {
                string content = await httpResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                throw new HttpRequestException($"GraphQL HTTP error: {httpResponse.StatusCode}, Content: {content}");
            }

            GraphQLResponse<T>? graphResponse = await httpResponse.Content.ReadFromJsonAsync<GraphQLResponse<T>>(_jsonOptions).ConfigureAwait(false);

            if (graphResponse == null)
            {
                throw new InvalidOperationException("Invalid response from GraphQL endpoint.");
            }

            if (graphResponse.Errors != null && graphResponse.Errors.Count > 0)
            {
                string messages = string.Join("; ", graphResponse.Errors.Select(e => e.Message));
                throw new InvalidOperationException($"GraphQL errors: {messages}");
            }

            return graphResponse.Data;
        }

        // Response DTOs matching the GraphQL shape
        private class QuestionsListResponse
        {
            [JsonPropertyName("questions")]
            public List<Question> Questions { get; set; } = new();
        }

        private class QuestionResponse
        {
            [JsonPropertyName("questions_by_pk")]
            public Question? QuestionsByPk { get; set; }
        }

        private class InsertQuestionResponse
        {
            [JsonPropertyName("insert_questions_one")]
            public InsertedQuestion? InsertQuestionsOne { get; set; }

            public class InsertedQuestion
            {
                [JsonPropertyName("id")]
                public int Id { get; set; }
            }
        }

        private class DeleteQuestionResponse
        {
            [JsonPropertyName("delete_questions_by_pk")]
            public DeletedQuestion? DeleteQuestionsByPk { get; set; }

            public class DeletedQuestion
            {
                [JsonPropertyName("id")]
                public int Id { get; set; }
            }
        }

        private class GenericMutationResponse { }
    }
}
