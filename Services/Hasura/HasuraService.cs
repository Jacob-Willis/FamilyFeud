using System.Diagnostics;
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
mutation InsertQuestion($question: String!, $category: String!,$answers: [answers_insert_input!]!) {
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
                category = NormalizeCategory(question.Category),
                answers = answersInput
            };

            GraphQLRequest req = new GraphQLRequest { Query = mutation, Variables = variables };
            InsertQuestionResponse? res = await SendRequestAsync<InsertQuestionResponse>(req).ConfigureAwait(false);
            return res?.InsertQuestionsOne?.Id ?? 0;
        }

        public async Task<bool> UpdateQuestionAsync(Question question)
        {
            if (question == null) throw new ArgumentNullException(nameof(question));

            // Every value goes in as a GraphQL variable, so answer text never needs escaping.
            // Hasura runs all root fields of one mutation in a single transaction, in order:
            // 1. update the question text and category (null if the question no longer exists),
            // 2. delete answers the host removed, 3. update the existing ones,
            // 4. insert the new answers, 5-6. undo step 4 if the question was gone (#37).
            // The app_full_access role can't set answers.question_id, so new answers go in as a
            // nested insert under the question (Hasura fills in question_id). That needs an upsert
            // on questions_pkey, which would recreate a question deleted elsewhere. So the insert
            // half of the upsert writes a one-off marker as the question text (the update half only
            // touches category, so it never writes the marker), and steps 5-6 delete any row that
            // still has the marker. A deleted question therefore stays deleted, all in one transaction.
            string mutation = @"
mutation UpdateQuestion($id: Int!, $question: String!, $category: String!, $missingMarker: String!, $existingAnswerIds: [Int!]!, $answerUpdates: [answers_updates!]!, $newAnswers: [answers_insert_input!]!) {
  update_questions_by_pk(pk_columns: { id: $id }, _set: { question: $question, category: $category }) {
    id
  }
  delete_answers(where: { question_id: { _eq: $id }, id: { _nin: $existingAnswerIds } }) {
    affected_rows
  }
  update_answers_many(updates: $answerUpdates) {
    affected_rows
  }
  insert_questions_one(
    object: { id: $id, question: $missingMarker, category: $category, answers: { data: $newAnswers } },
    on_conflict: { constraint: questions_pkey, update_columns: [category] }
  ) {
    id
  }
  undo_answers: delete_answers(where: { question_id: { _eq: $id }, question: { question: { _eq: $missingMarker } } }) {
    affected_rows
  }
  undo_question: delete_questions(where: { id: { _eq: $id }, question: { _eq: $missingMarker } }) {
    affected_rows
  }
}";
            // Answers with an id already exist and are updated; answers without one are inserted.
            List<Answer> existingAnswers = question.Answers.Where(a => a.Id > 0).ToList();
            List<Answer> newAnswers = question.Answers.Where(a => a.Id == 0).ToList();

            var variables = new
            {
                id = question.Id,
                question = question.QuestionText,
                category = NormalizeCategory(question.Category),
                // Unique per call, so it can never match a real question's text.
                missingMarker = $"__ff_missing_question_{Guid.NewGuid():N}",
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
            UpdateQuestionResponse? res = await SendRequestAsync<UpdateQuestionResponse>(req).ConfigureAwait(false);
            // Null means no question had this id (e.g. it was deleted elsewhere); nothing was saved.
            return res?.UpdateQuestionsByPk != null;
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

        /// <summary>
        /// questions.category is NOT NULL and a blank category means "no category" (#29),
        /// so null or whitespace becomes "" and anything else is trimmed.
        /// </summary>
        private static string NormalizeCategory(string? category)
        {
            return category?.Trim() ?? string.Empty;
        }

        // Longest GraphQL error text passed on to the UI; the full text always goes to the debug output (#33)
        private const int MaxErrorDetailLength = 150;

        // Generic request sender with error handling. Exception messages are short and friendly because the
        // pages show them to the host as-is; the full details (status, response body, inner exception) go to
        // the debug output instead (#33).
        private async Task<T?> SendRequestAsync<T>(GraphQLRequest request)
            where T : class
        {
            HttpResponseMessage httpResponse;
            try
            {
                httpResponse = await _httpClient.PostAsJsonAsync(string.Empty, request, _jsonOptions).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                Debug.WriteLine($"Hasura request failed: {ex}");
                throw new HttpRequestException("Couldn't reach the question server. Check the internet connection.", ex);
            }
            catch (TaskCanceledException ex)
            {
                // HttpClient reports a timeout as a cancelled task
                Debug.WriteLine($"Hasura request timed out: {ex}");
                throw new TimeoutException("The question server took too long to respond.", ex);
            }

            using (httpResponse)
            {
                if (!httpResponse.IsSuccessStatusCode)
                {
                    string content = await httpResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                    Debug.WriteLine($"Hasura HTTP error: {(int)httpResponse.StatusCode} {httpResponse.StatusCode}, Content: {content}");
                    throw new HttpRequestException($"Couldn't reach the question server (HTTP {(int)httpResponse.StatusCode}).", null, httpResponse.StatusCode);
                }

                GraphQLResponse<T>? graphResponse;
                try
                {
                    graphResponse = await httpResponse.Content.ReadFromJsonAsync<GraphQLResponse<T>>(_jsonOptions).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is JsonException || ex is NotSupportedException)
                {
                    // NotSupportedException: the body wasn't JSON at all (e.g. an HTML page from a wrong URL)
                    Debug.WriteLine($"Hasura response could not be parsed: {ex}");
                    graphResponse = null;
                }

                if (graphResponse == null)
                {
                    throw new InvalidOperationException("The question server sent a response the app couldn't read.");
                }

                if (graphResponse.Errors != null && graphResponse.Errors.Count > 0)
                {
                    string messages = string.Join("; ", graphResponse.Errors.Select(e => e.Message));
                    Debug.WriteLine($"Hasura GraphQL errors: {messages}");
                    // Keep the first error (e.g. a constraint or permission message) so save and delete errors still say why
                    string detail = graphResponse.Errors[0].Message.Trim();
                    if (detail.Length > MaxErrorDetailLength)
                        detail = detail.Substring(0, MaxErrorDetailLength) + "...";
                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
                        ? "The question server returned an error."
                        : $"The question server returned an error: {detail}");
                }

                return graphResponse.Data;
            }
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

        private class UpdateQuestionResponse
        {
            [JsonPropertyName("update_questions_by_pk")]
            public UpdatedQuestion? UpdateQuestionsByPk { get; set; }

            public class UpdatedQuestion
            {
                [JsonPropertyName("id")]
                public int Id { get; set; }
            }
        }
    }
}
