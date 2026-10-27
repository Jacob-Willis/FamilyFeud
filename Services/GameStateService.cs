using FamilyFeud.Models;
using FamilyFeud.Services.Hasura;

namespace FamilyFeud.Services
{
    // All game rules and shared state: teams, scores, the current question, revealed answers, strikes,
    // turn control and game length. Registered as a singleton so the host and audience windows share it.
    public class GameStateService
    {
        // Game length, chosen on the start screen with SetGameLength: a number of rounds, a target score, or both
        // (whichever is reached first ends the game). New games default to 5 rounds and no target score.
        public const int DefaultTotalRounds = 5;
        // Value the start screen's target score box starts with; not a limit until the host ticks Target score
        public const int DefaultTargetScore = 300;
        public static readonly IReadOnlyList<int> RoundOptions = new[] { 5, 10, 15, 20 };

        private readonly IHasuraService _hasura;

        // Revealed answers per question, shared by the host and audience windows (questionId -> set of answerIds)
        private readonly Dictionary<int, HashSet<int>> _revealedAnswers = new();

        // Ids of questions loaded this game; when every cached question has been asked the game ends
        private readonly HashSet<int> _askedQuestionIds = new();

        public event Action? StateChanged;

        // Use Team model instances to store team data
        public Team Team1 { get; private set; }
        public Team Team2 { get; private set; }

        public SelectedTeam StartingTeam { get; set; }

        // The team whose turn it is now (face-off turn, team in control, or stealing team).
        // Random until the host picks a starting team; the team that starts each face-off is StartingTeam.
        public SelectedTeam WhichTeamsTurn { get; private set; } = SelectedTeam.Random;

        public List<Question> Questions { get; private set; } = new();

        // The category the host picked on the Game page, kept here so it survives that page being re-created (e.g. a
        // visit to the Questions page mid-game) (#50). Empty means "All categories". Only the host page uses it, so
        // setting it doesn't notify. The page resolves it against the cached categories. Cleared by ResetGame.
        public string SelectedCategory { get; set; } = string.Empty;

        // Ids of the questions asked this game, read-only (#32). Cleared by ResetGame.
        public IReadOnlyCollection<int> AskedQuestionIds => _askedQuestionIds;

        // Whether the question has been loaded this game, so it shouldn't be asked again (#32)
        public bool HasBeenAsked(int questionId) => _askedQuestionIds.Contains(questionId);

        // Cached questions not asked yet this game. Computed on demand from Questions, so questions deleted
        // mid-game drop out straight away and the pool survives the Game page being re-created (#32).
        public List<Question> RemainingQuestions => Questions.Where(q => !HasBeenAsked(q.Id)).ToList();

        // Currently active question, shared by the host and audience windows
        public Question? CurrentQuestion { get; private set; }

        // Expose loading/error state so UI can bind to them
        public bool IsLoading { get; private set; }
        public string ErrorMessage { get; private set; } = string.Empty;

        public GameMode Mode { get; private set; }

        // Playoff-specific state
        // When two teams each supply a "first answer" we determine which team has the higher answer
        // and set PlayoffAwaitingChoice = true so the control team can choose to play or pass.
        public bool PlayoffAwaitingChoice { get; private set; }

        // Points collected this round (face-off and normal reveals). Awarded in full to the round winner by EndRound.
        public int RoundPool { get; private set; }

        // Whether the current round is finished. Set by EndRound (the board was cleared in Normal play or before
        // Play/Pass was chosen, or a steal succeeded or failed) or by AbandonRound (Reveal All mid-round). Cleared when the next question loads.
        public bool IsRoundFinished { get; private set; }

        // Whether anything that counts has happened this round: a scored reveal or a strike (face-off strikes
        // included). Unlike the face-off fields it survives the reset after a double face-off strike, so the host
        // is asked before abandoning the round (#38). Cleared when the next question loads and by ResetGame.
        public bool RoundHasActivity { get; private set; }

        // Number of rounds in the game, or null for no round limit. When set, always one of RoundOptions.
        public int? TotalRounds { get; private set; } = DefaultTotalRounds;

        // Points a team needs to win, or null for no target score. When set, always positive.
        public int? TargetScore { get; private set; }

        // Rounds that have ended (counted in EndRound). A round abandoned by loading a new question or by Reveal All doesn't count.
        public int RoundsPlayed { get; private set; }

        // Set by EndRound when the chosen length is reached or no questions are left. No new rounds start after this.
        public bool IsGameOver { get; private set; }

        // Why the game ended (set by EndRound with IsGameOver); None while the game is running
        public GameEndReason EndReason { get; private set; } = GameEndReason.None;

        // The round the host is on: the next round before a question loads, the finished round after it ends
        public int CurrentRoundNumber
        {
            get
            {
                int round = IsRoundFinished ? RoundsPlayed : RoundsPlayed + 1;
                return TotalRounds is int total ? Math.Min(round, total) : round;
            }
        }

        // Progress line for both windows, showing whichever limits are set: "Round 2 of 10",
        // "First to 300 points (round 2)", or "Round 2 of 10 · First to 300 points"
        public string GameProgressText
        {
            get
            {
                if (TotalRounds is int total && TargetScore is int target)
                    return $"Round {CurrentRoundNumber} of {total} · First to {PointsText(target)}";
                if (TotalRounds is int rounds)
                    return $"Round {CurrentRoundNumber} of {rounds}";
                if (TargetScore is int score)
                    return $"First to {PointsText(score)} (round {CurrentRoundNumber})";
                return $"Round {CurrentRoundNumber}";
            }
        }

        // The team with the higher score once the game is over; Random means the game isn't over or it's a tie
        public SelectedTeam Winner
        {
            get
            {
                if (!IsGameOver || Team1.Score == Team2.Score) return SelectedTeam.Random;
                return Team1.Score > Team2.Score ? SelectedTeam.Team1 : SelectedTeam.Team2;
            }
        }

        // Winning screen headline: "Winner: <name>" or "It's a tie!"; worded so it reads right for singular and plural team names (empty while the game is running)
        public string WinnerHeadline
        {
            get
            {
                if (!IsGameOver) return string.Empty;
                return Winner switch
                {
                    SelectedTeam.Team1 => $"Winner: {Team1.Name}",
                    SelectedTeam.Team2 => $"Winner: {Team2.Name}",
                    _ => "It's a tie!"
                };
            }
        }

        // Winning screen subtitle saying how the game ended, e.g. "After 5 rounds" or "Red reached 300 points"
        public string GameOverReasonText
        {
            get
            {
                string rounds = RoundsPlayed == 1 ? "1 round" : $"{RoundsPlayed} rounds";
                string target = PointsText(TargetScore ?? 0);
                return EndReason switch
                {
                    GameEndReason.RoundsPlayed => $"After {rounds}",
                    GameEndReason.TargetScore => Winner switch
                    {
                        SelectedTeam.Team1 => $"{Team1.Name} reached {target} after {rounds}",
                        SelectedTeam.Team2 => $"{Team2.Name} reached {target} after {rounds}",
                        _ => $"Reached {target} after {rounds}"
                    },
                    GameEndReason.OutOfQuestions => $"Out of questions after {rounds}",
                    _ => string.Empty
                };
            }
        }

        public GameStateService(IHasuraService hasura)
        {
            _hasura = hasura ?? throw new ArgumentNullException(nameof(hasura));
            // Initialise with default names
            Team1 = new Team("Team 1");
            Team2 = new Team("Team 2");
            Mode = GameMode.PlayOff;
        }

        public void SetCurrentQuestion(Question? q)
        {
            // no new rounds once the game is over (ResetGame starts a new game)
            if (IsGameOver && q != null) return;

            // Check if this is the first question being set
            bool isFirstQuestion = CurrentQuestion == null;

            CurrentQuestion = q;
            // reset revealed answers for the newly selected question
            if (q != null)
            {
                _revealedAnswers.Remove(q.Id);
                // remember it so EndRound can end the game when the question pool runs out
                _askedQuestionIds.Add(q.Id);
            }
            // reset playoff state for the new question
            ResetPlayoffState();
            IsRoundFinished = false;  // New round, so it's not finished
            RoundHasActivity = false;

            if (q != null)
            {
                // Alternate StartingTeam for every question after the first one
                if (!isFirstQuestion)
                {
                    StartingTeam = StartingTeam == SelectedTeam.Team2 ? SelectedTeam.Team1 : SelectedTeam.Team2;
                }

                // every face-off starts with the starting team
                SwitchTeam(StartingTeam);
            }
            NotifyStateChanged();
        }

        public void RevealAnswer(int questionId, int answerId)
        {
            // the face-off is decided and the host still has to choose Play or Pass: no more reveals until then (#49).
            // Checked before the answer is marked revealed, so it stays hidden and can be revealed after the choice.
            if (Mode == GameMode.PlayOff && PlayoffAwaitingChoice && !IsRoundFinished) return;

            if (!_revealedAnswers.TryGetValue(questionId, out HashSet<int>? set))
            {
                set = new HashSet<int>();
                _revealedAnswers[questionId] = set;
            }

            // don't reveal same answer twice
            if (!set.Add(answerId)) return;

            // once the round has ended, reveals are display-only: nothing is scored until the next question loads
            if (IsRoundFinished)
            {
                NotifyStateChanged();
                return;
            }

            // Find question and answer
            Question? q = (CurrentQuestion?.Id == questionId) ? CurrentQuestion : Questions.Find(x => x.Id == questionId);
            if (q == null) return;
            Answer? ans = q.Answers.Find(a => a.Id == answerId);
            if (ans == null) return;

            RoundHasActivity = true;

            // GameMode.PlayOff mode means they are fighting to choose who goes first
            if (Mode == GameMode.PlayOff)
            {
                // every answer revealed during the face-off goes into the round pool
                RoundPool += ans.Points;

                int topPoints = q.Answers.Count > 0 ? q.Answers.Max(a => a.Points) : 0;

                // If team 1 does not have a first answer, select it here
                if (WhichTeamsTurn == SelectedTeam.Team1 && Team1.FirstAnswer == null)
                {
                    Team1.FirstAnswer = ans;

                    if (ans.Points == topPoints)
                    {
                        // if they got the top answer, team 1 remains in control and can choose to play or pass
                        PlayoffAwaitingChoice = true;
                    }
                    else if (Team2.FirstAnswer == null)
                    {
                        // switch to the other team to ask for their first answer
                        WhichTeamsTurn = SelectedTeam.Team2;
                    }
                }
                // otherwise same for team 2
                else if (WhichTeamsTurn == SelectedTeam.Team2 && Team2.FirstAnswer == null)
                {
                    Team2.FirstAnswer = ans;

                    if (ans.Points == topPoints)
                    {
                        // if they got the top answer, team 2 remains in control and can choose to play or pass
                        PlayoffAwaitingChoice = true;
                    }
                    else if (Team1.FirstAnswer == null)
                    {
                        // switch to the other team to ask for their first answer
                        WhichTeamsTurn = SelectedTeam.Team1;
                    }
                }

                if (Team1.FirstAnswer != null && Team2.FirstAnswer != null)
                {
                    // Both teams have supplied a first answer, determine which is higher and set control team
                    if (Team1.FirstAnswer.Points > Team2.FirstAnswer.Points)
                    {
                        WhichTeamsTurn = SelectedTeam.Team1;
                    }
                    else if (Team2.FirstAnswer.Points > Team1.FirstAnswer.Points)
                    {
                        WhichTeamsTurn = SelectedTeam.Team2;
                    }
                    else
                    {
                        // Tie (only possible when two answers share a value): pick the control team at random.
                        // Set WhichTeamsTurn directly so StartingTeam (and its alternation) is untouched and we notify once below.
                        WhichTeamsTurn = PickRandomTeam();
                    }
                    PlayoffAwaitingChoice = true;
                }
            }
            else if (Mode == GameMode.Normal)
            {
                // Normal play: revealed answers go into the round pool; the control team wins it if they clear the board
                RoundPool += ans.Points;

                // Check if all answers have been revealed (round is finished)
                if (CheckAndFinishRound(q)) return;
            }
            else if (Mode == GameMode.PointSteal)
            {
                // the stealing team found an answer: they win the pool plus this answer
                if (WhichTeamsTurn != SelectedTeam.Random)
                {
                    RoundPool += ans.Points;
                    EndRound(WhichTeamsTurn);
                    return;
                }
            }

            NotifyStateChanged();
        }

        // Host indicates the control team chose to keep the turn
        public void ChoosePlay()
        {
            if (!PlayoffAwaitingChoice) return;
            StartNormalPlay();
        }

        // Host indicates the control team passed the turn to the other team
        public void ChoosePass()
        {
            if (!PlayoffAwaitingChoice) return;
            // hand control to the other team (set directly so StartNormalPlay notifies only once)
            WhichTeamsTurn = WhichTeamsTurn == SelectedTeam.Team1 ? SelectedTeam.Team2 : SelectedTeam.Team1;
            StartNormalPlay();
        }

        // After Play or Pass: the team in control (WhichTeamsTurn) starts normal play. If the face-off already
        // revealed every answer (e.g. a one-answer question), the board is clear, so that team wins the pool and
        // the round ends straight away (#39). Notifies once either way.
        private void StartNormalPlay()
        {
            PlayoffAwaitingChoice = false;
            Mode = GameMode.Normal;
            Team1.IncorrectStrikes = 0;
            Team2.IncorrectStrikes = 0;
            if (CurrentQuestion != null && CheckAndFinishRound(CurrentQuestion)) return;
            NotifyStateChanged();
        }

        // Host registers a strike for the current active team
        public void HostStrike()
        {
            // the round is over: ignore strikes until the next question loads
            if (IsRoundFinished) return;

            if (Mode == GameMode.PlayOff)
            {
                // ignore strikes while the play/pass choice is pending or before a team is in control
                if (PlayoffAwaitingChoice || WhichTeamsTurn == SelectedTeam.Random) return;

                RoundHasActivity = true;
                bool team1Striking = WhichTeamsTurn == SelectedTeam.Team1;
                Team striker = team1Striking ? Team1 : Team2;
                Team other = team1Striking ? Team2 : Team1;

                // record the miss and hand the turn to the other team (set directly so we only notify once below)
                striker.FirstAnswer = Answer.Blank;
                WhichTeamsTurn = team1Striking ? SelectedTeam.Team2 : SelectedTeam.Team1;

                if (other.FirstAnswer is { IsBlank: false })
                {
                    // the other team already gave a real answer, so they win control and choose to play or pass
                    PlayoffAwaitingChoice = true;
                }
                else if (other.FirstAnswer is { IsBlank: true })
                {
                    // both teams struck: restart the face-off with the other team (the one that struck first)
                    ResetPlayoffState();
                }

                NotifyStateChanged();
                return;
            }
            RoundHasActivity = true;

            if (Mode == GameMode.PointSteal)
            {
                // stealing team failed: the team in control keeps the pool
                EndRound(WhichTeamsTurn == SelectedTeam.Team1 ? SelectedTeam.Team2 : SelectedTeam.Team1);
                return;
            }

            if (WhichTeamsTurn == SelectedTeam.Team1) Team1.IncorrectStrikes++;
            else if (WhichTeamsTurn == SelectedTeam.Team2) Team2.IncorrectStrikes++;

            int strikes = WhichTeamsTurn == SelectedTeam.Team1 ? Team1.IncorrectStrikes : Team2.IncorrectStrikes;
            if (strikes >= 3)
            {
                Mode = GameMode.PointSteal;
                // other team becomes the stealing team (set directly so we only notify once below)
                WhichTeamsTurn = WhichTeamsTurn == SelectedTeam.Team1 ? SelectedTeam.Team2 : SelectedTeam.Team1;
                NotifyStateChanged();
                return;
            }

            NotifyStateChanged();
        }

        // Reveal all answers for the given question without awarding any points. Used after a round to show the
        // answers nobody found, or to abandon a round in progress (the host confirms that first). Revealing every
        // answer of the current question mid-round ends it as display-only: see AbandonRound.
        public void RevealAllAnswers(int questionId)
        {
            // prefer the current question so Reveal All still works if it was deleted from the cache mid-game (#32)
            Question? q = (CurrentQuestion?.Id == questionId) ? CurrentQuestion : Questions.Find(x => x.Id == questionId);
            if (q == null) return;

            if (!_revealedAnswers.TryGetValue(questionId, out HashSet<int>? set))
            {
                set = new HashSet<int>();
                _revealedAnswers[questionId] = set;
            }

            foreach (Answer a in q.Answers)
            {
                set.Add(a.Id);
            }

            // mid-round: the board is fully shown, so the round can't be played on (#30)
            if (CurrentQuestion?.Id == questionId && !IsRoundFinished)
            {
                AbandonRound();
            }

            NotifyStateChanged();
        }

        public bool IsAnswerRevealed(int questionId, int answerId)
        {
            return _revealedAnswers.TryGetValue(questionId, out HashSet<int>? set) && set.Contains(answerId);
        }

        // Called by the start screen. Pass null to leave a limit off, but at least one must be set. Rounds must be
        // one of RoundOptions and the target score must be positive. The game ends at whichever limit is reached first.
        public void SetGameLength(int? totalRounds, int? targetScore)
        {
            if (totalRounds == null && targetScore == null)
                throw new ArgumentException("Choose a number of rounds, a target score, or both.");
            if (totalRounds is int rounds && !RoundOptions.Contains(rounds)) throw new ArgumentOutOfRangeException(nameof(totalRounds));
            if (targetScore is int target && target <= 0) throw new ArgumentOutOfRangeException(nameof(targetScore));
            TotalRounds = totalRounds;
            TargetScore = targetScore;
            NotifyStateChanged();
        }

        public void SwitchTeam(SelectedTeam t)
        {
            // Accept Team1, Team2 or Random
            if (t == SelectedTeam.Team1 || t == SelectedTeam.Team2 || t == SelectedTeam.Random)
            {
                if (WhichTeamsTurn != t)
                {
                    WhichTeamsTurn = t;
                    NotifyStateChanged();
                }
            }
        }

        // "1 point" or "N points", so a target of 1 doesn't read "1 points" (#53)
        private static string PointsText(int points)
        {
            return points == 1 ? "1 point" : $"{points} points";
        }

        // Team1 or Team2 with equal chance; changes no state
        private static SelectedTeam PickRandomTeam()
        {
            return Random.Shared.Next(0, 2) == 0 ? SelectedTeam.Team1 : SelectedTeam.Team2;
        }

        public SelectedTeam ChooseRandomFirst()
        {
            SelectedTeam pick = PickRandomTeam();
            SwitchTeam(pick);
            // set StartingTeam so we can alternate between teams
            StartingTeam = pick;
            return pick;
        }

        private void ClearScores()
        {
            Team1.Score = 0;
            Team2.Score = 0;
        }

        // Clears all game state so the next game starts fresh (used when the host goes back to the start screen).
        // The loaded Questions cache is kept; clearing the asked ids makes every question remaining again.
        public void ResetGame()
        {
            ClearScores();
            // face-off answers, strikes, Play/Pass choice, round pool and mode back to PlayOff
            ResetPlayoffState();
            IsRoundFinished = false;
            RoundHasActivity = false;
            // no current question means the next SetCurrentQuestion is the first one, so it won't flip StartingTeam
            CurrentQuestion = null;
            _revealedAnswers.Clear();
            WhichTeamsTurn = SelectedTeam.Random;
            StartingTeam = SelectedTeam.Random;
            Team1.Name = "Team 1";
            Team2.Name = "Team 2";
            // game length back to defaults (the start screen sets it again) and progress cleared
            TotalRounds = DefaultTotalRounds;
            TargetScore = null;
            RoundsPlayed = 0;
            IsGameOver = false;
            EndReason = GameEndReason.None;
            _askedQuestionIds.Clear();
            SelectedCategory = string.Empty;
            NotifyStateChanged();
        }

        public void ResetPlayoffState()
        {
            Team1.FirstAnswer = null;
            Team2.FirstAnswer = null;
            Team1.IncorrectStrikes = 0;
            Team2.IncorrectStrikes = 0;
            PlayoffAwaitingChoice = false;
            // a round abandoned by loading a new question loses its pool
            RoundPool = 0;
            Mode = GameMode.PlayOff;
        }

        // Checks if all answers in the given question have been revealed.
        // If so, the control team wins the pool and the round ends. Returns true when the round was ended (and notified).
        private bool CheckAndFinishRound(Question q)
        {
            if (q == null || !_revealedAnswers.TryGetValue(q.Id, out HashSet<int>? revealedSet))
                return false;

            // Round is finished when every answer has been revealed
            bool allRevealed = q.Answers.All(a => revealedSet.Contains(a.Id));
            if (!allRevealed) return false;

            EndRound(WhichTeamsTurn);
            return true;
        }

        // Ends the round: the winner gets the whole pool. The next SetCurrentQuestion resets the face-off state.
        private void EndRound(SelectedTeam winner)
        {
            if (winner == SelectedTeam.Team1) Team1.Score += RoundPool;
            else if (winner == SelectedTeam.Team2) Team2.Score += RoundPool;
            RoundPool = 0;
            IsRoundFinished = true;
            RoundsPlayed++;
            // If the same round reaches both limits, the target score is reported: a team actually reached it
            if (IsTargetScoreReached())
                EndReason = GameEndReason.TargetScore;
            else if (TotalRounds is int total && RoundsPlayed >= total)
                EndReason = GameEndReason.RoundsPlayed;
            else if (NoQuestionsLeft())
                EndReason = GameEndReason.OutOfQuestions;
            IsGameOver = EndReason != GameEndReason.None;
            NotifyStateChanged();
        }

        // Ends the round without a winner (Reveal All mid-round). The pool is discarded and no score changes, like a
        // round abandoned by loading a new question, so it doesn't count toward RoundsPlayed. Reveals are display-only
        // and strikes are ignored until the next question. If this was the last question the game ends, because no
        // further round can start. The caller notifies.
        private void AbandonRound()
        {
            RoundPool = 0;
            PlayoffAwaitingChoice = false;
            IsRoundFinished = true;
            if (NoQuestionsLeft())
            {
                EndReason = GameEndReason.OutOfQuestions;
                IsGameOver = true;
            }
        }

        private bool IsTargetScoreReached()
        {
            return TargetScore is int target && (Team1.Score >= target || Team2.Score >= target);
        }

        // True when no cached question is left to ask, so no further round can start. Uses the same notion of
        // "remaining" as the Game page, so questions deleted mid-game don't keep the game going and deleting
        // every question still ends the game (#32).
        private bool NoQuestionsLeft()
        {
            return _askedQuestionIds.Count > 0 && !Questions.Any(q => !HasBeenAsked(q.Id));
        }

        public async Task LoadQuestionsAsync()
        {
            IsLoading = true;
            ErrorMessage = string.Empty;
            NotifyStateChanged();

            try
            {
                List<Question> q = await _hasura.GetQuestionsAsync();
                Questions = q ?? new List<Question>();
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
                Questions = new List<Question>();
            }
            finally
            {
                IsLoading = false;
                NotifyStateChanged();
            }
        }

        // The question pages edit the cached list through these three methods instead of touching Questions directly,
        // so open listeners (the Game page and the audience window) hear about it (#51). Each notifies once.
        // Like LoadQuestionsAsync they leave the game rules alone: asked ids, the round and revealed answers stay as they are.

        // Adds a question that was just saved to Hasura. The caller only does this once questions have been loaded
        // (an empty cache must stay empty so the Game page still loads everything from Hasura).
        public void AddQuestionToCache(Question question)
        {
            Questions.Add(question);
            NotifyStateChanged();
        }

        // Copies the saved text, category and answers onto the cached question with this id. The cached instance can
        // be the CurrentQuestion the audience window is showing, so it is updated in place rather than replaced.
        // Does nothing (and doesn't notify) if the question isn't cached.
        public void UpdateCachedQuestion(int questionId, Question source)
        {
            Question? cached = Questions.Find(q => q.Id == questionId);
            if (cached == null) return;

            cached.QuestionText = source.QuestionText;
            cached.Category = source.Category;
            cached.Answers = source.Answers;
            NotifyStateChanged();
        }

        // Removes a deleted question from the cache; RemainingQuestions and the categories follow automatically.
        // Does nothing (and doesn't notify) if the question isn't cached.
        public void RemoveQuestionFromCache(int questionId)
        {
            if (Questions.RemoveAll(q => q.Id == questionId) > 0)
            {
                NotifyStateChanged();
            }
        }

        private void NotifyStateChanged() => StateChanged?.Invoke();
    }

    public enum SelectedTeam
    {
        Random,
        Team1,
        Team2
    }

    public enum GameEndReason
    {
        None,
        RoundsPlayed,
        TargetScore,
        OutOfQuestions
    }

    public enum GameMode
    {
        PlayOff,
        Normal,
        PointSteal
    }
}

