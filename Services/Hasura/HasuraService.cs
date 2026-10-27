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

            // Every value goes in as a GraphQL variable, so answer text never needs escaping.
            // Hasura runs all root fields of one mutation in a single transaction, in order:
            // 1. delete answers the host removed, 2. update the existing ones,
            // 3. update the question and insert the new answers.
            // The app_full_access role can't set answers.question_id, so new answers go in as a
            // nested insert under the question (Hasura fills in question_id). That needs an upsert
            // on questions_pkey, which also updates the question text and category.
            string mutation = @"
mutation UpdateQuestion($id: Int!, $question: String!, $category: String, $existingAnswerIds: [Int!]!, $answerUpdates: [answers_updates!]!, $newAnswers: [answers_insert_input!]!) {
  delete_answers(where: { question_id: { _eq: $id }, id: { _nin: $existingAnswerIds } }) {
    affected_rows
  }
  update_answers_many(updates: $answerUpdates) {
    affected_rows
  }
  insert_questions_one(
    object: { id: $id, question: $question, category: $category, answers: { data: $newAnswers } },
    on_conflict: { constraint: questions_pkey, update_columns: [question, category] }
  ) {
    id
  }
}";
            // Answers with an id already exist and are updated; answers without one are inserted.
            List<Answer> existingAnswers = question.Answers.Where(a => a.Id > 0).ToList();
            List<Answer> newAnswers = question.Answers.Where(a => a.Id == 0).ToList();

            var variables = new
            {
                id = question.Id,
                question = question.QuestionText,
                category = question.Category,
                existingAnswerIds = existingAnswers.Select(a => a.Id).ToList(),
                answerUpdates = existingAnswers.Select(a => new
                {
                    // Match on question_id too, so an update can only touch this question's answers.
                    where = new { id = new { _eq = a.Id }, question_id = new { _eq = question.Id } },
                    _set = new { answer = a.AnswerText, points = a.Points }
                }).ToList(),
                // Sent without id or question_id; the nested insert links them to the question.
                newAnswers = newAnswers.Select(a => new
                {
                    answer = a.AnswerText,
                    points = a.Points
                }).ToList()
            };

            GraphQLRequest req = new GraphQLRequest { Query = mutation, Variables = variables };
            await SendRequestAsync<GenericMutationResponse>(req).ConfigureAwait(false);
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
