using FamilyFeud.Models;
using FamilyFeud.Services.Hasura;

namespace FamilyFeud.Services
{
    // All game rules and shared state: teams, scores, the current question, revealed answers, strikes,
    // turn control and game length. Registered as a singleton so the host and audience windows share it.
    public class GameStateService
    {
        // Game length, chosen on the start screen with SetGameLength
        public const GameLengthMode DefaultGameLength = GameLengthMode.Rounds;
        public const int DefaultTotalRounds = 5;
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

        // Whether the current round is finished. Set only by EndRound: the board was cleared in Normal play,
        // or a steal succeeded or failed. Cleared when the next question loads.
        public bool IsRoundFinished { get; private set; }

        public GameLengthMode GameLength { get; private set; } = DefaultGameLength;

        // Number of rounds in the game (used when GameLength is Rounds). Always one of RoundOptions.
        public int TotalRounds { get; private set; } = DefaultTotalRounds;

        // Points a team needs to win (used when GameLength is TargetScore)
        public int TargetScore { get; private set; } = DefaultTargetScore;

        // Rounds that have ended (counted in EndRound). A round abandoned by loading a new question doesn't count.
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
                return GameLength == GameLengthMode.Rounds ? Math.Min(round, TotalRounds) : round;
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

        // Winning screen headline: "<name> wins!" or "It's a tie!" (empty while the game is running)
        public string WinnerHeadline
        {
            get
            {
                if (!IsGameOver) return string.Empty;
                return Winner switch
                {
                    SelectedTeam.Team1 => $"{Team1.Name} wins!",
                    SelectedTeam.Team2 => $"{Team2.Name} wins!",
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
                return EndReason switch
                {
                    GameEndReason.RoundsPlayed => $"After {rounds}",
                    GameEndReason.TargetScore => Winner switch
                    {
                        SelectedTeam.Team1 => $"{Team1.Name} reached {TargetScore} points after {rounds}",
                        SelectedTeam.Team2 => $"{Team2.Name} reached {TargetScore} points after {rounds}",
                        _ => $"Reached {TargetScore} points after {rounds}"
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
            PlayoffAwaitingChoice = false;
            Mode = GameMode.Normal;
            Team1.IncorrectStrikes = 0;
            Team2.IncorrectStrikes = 0;
            NotifyStateChanged();
        }

        // Host indicates the control team passed the turn to the other team
        public void ChoosePass()
        {
            if (!PlayoffAwaitingChoice) return;
            PlayoffAwaitingChoice = false;
            // hand control to the other team (set directly so we only notify once below)
            WhichTeamsTurn = WhichTeamsTurn == SelectedTeam.Team1 ? SelectedTeam.Team2 : SelectedTeam.Team1;
            Mode = GameMode.Normal;
            Team1.IncorrectStrikes = 0;
            Team2.IncorrectStrikes = 0;
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

        // Reveal all answers for the given question without awarding any points
        public void RevealAllAnswers(int questionId)
        {
            Question? q = Questions.Find(x => x.Id == questionId);
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

            NotifyStateChanged();
        }

        public bool IsAnswerRevealed(int questionId, int answerId)
        {
            return _revealedAnswers.TryGetValue(questionId, out HashSet<int>? set) && set.Contains(answerId);
        }

        // Called by the start screen. Rounds must be one of RoundOptions and the target score must be positive.
        public void SetGameLength(GameLengthMode lengthMode, int totalRounds, int targetScore)
        {
            if (!RoundOptions.Contains(totalRounds)) throw new ArgumentOutOfRangeException(nameof(totalRounds));
            if (targetScore <= 0) throw new ArgumentOutOfRangeException(nameof(targetScore));
            GameLength = lengthMode;
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
        // The loaded Questions cache is kept; the Game page rebuilds its remaining-questions pool from it.
        public void ResetGame()
        {
            ClearScores();
            // face-off answers, strikes, Play/Pass choice, round pool and mode back to PlayOff
            ResetPlayoffState();
            IsRoundFinished = false;
            // no current question means the next SetCurrentQuestion is the first one, so it won't flip StartingTeam
            CurrentQuestion = null;
            _revealedAnswers.Clear();
            WhichTeamsTurn = SelectedTeam.Random;
            StartingTeam = SelectedTeam.Random;
            Team1.Name = "Team 1";
            Team2.Name = "Team 2";
            // game length back to defaults (the start screen sets it again) and progress cleared
            GameLength = DefaultGameLength;
            TotalRounds = DefaultTotalRounds;
            TargetScore = DefaultTargetScore;
            RoundsPlayed = 0;
            IsGameOver = false;
            EndReason = GameEndReason.None;
            _askedQuestionIds.Clear();
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
            if (IsGameLengthReached())
                EndReason = GameLength == GameLengthMode.Rounds ? GameEndReason.RoundsPlayed : GameEndReason.TargetScore;
            else if (NoQuestionsLeft())
                EndReason = GameEndReason.OutOfQuestions;
            IsGameOver = EndReason != GameEndReason.None;
            NotifyStateChanged();
        }

        private bool IsGameLengthReached()
        {
            if (GameLength == GameLengthMode.Rounds) return RoundsPlayed >= TotalRounds;
            return Team1.Score >= TargetScore || Team2.Score >= TargetScore;
        }

        // True when every loaded question has been asked this game, so no further round can start
        private bool NoQuestionsLeft()
        {
            return Questions.Count > 0 && Questions.All(q => _askedQuestionIds.Contains(q.Id));
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

        private void NotifyStateChanged() => StateChanged?.Invoke();
    }

    public enum SelectedTeam
    {
        Random,
        Team1,
        Team2
    }

    public enum GameLengthMode
    {
        Rounds,
        TargetScore
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

