using FamilyFeud.Models;
using FamilyFeud.Services.Hasura;

namespace FamilyFeud.Services
{
    // Simple application state for teams and scores. Registered as a singleton.
    public class GameStateService
    {
        private readonly IHasuraService _hasura;

        // Use Team model instances to store team data
        public Team Team1 { get; private set; }
        public Team Team2 { get; private set; }

        public SelectedTeam StartingTeam { get; set; }

        public Answer? GetFirstAnswer(Team team)
        {
            return team.FirstAnswer;
        }

        public List<Question> Questions { get; private set; } = new();

        // Currently active question (shared so audience window can display it)
        public Question? CurrentQuestion { get; private set; }

        // Track revealed answers per question for audience display (questionId -> set of answerIds)
        private readonly Dictionary<int, HashSet<int>> _revealedAnswers = new();

        // Expose loading/error state so UI can bind to them
        public bool IsLoading { get; private set; }
        public string ErrorMessage { get; private set; } = string.Empty;

        //Set gamemode
        public GameMode mode { get; private set; }

        public void SetGameMode(GameMode m)
        {
            mode = m;
        }

        public GameStateService(IHasuraService hasura)
        {
            _hasura = hasura ?? throw new ArgumentNullException(nameof(hasura));
            // Initialise with default names and colours
            Team1 = new FamilyFeud.Models.Team("Team 1", "#ff6b6b");
            Team2 = new FamilyFeud.Models.Team("Team 2", "#4d79ff");
            SetGameMode(GameMode.PlayOff);
        }

        public void SetCurrentQuestion(Question? q)
        {
            // Check if this is the first question being set
            bool isFirstQuestion = CurrentQuestion == null;

            CurrentQuestion = q;
            // reset revealed answers for the newly selected question
            if (q != null)
            {
                _revealedAnswers.Remove(q.Id);
            }
            // reset playoff state for the new question
            ResetPlayoffState();

            // Only alternate StartingTeam for subsequent questions, not the first one
            if (!isFirstQuestion)
            {
                // switch StartingTeam to the other team for the new question
                if (StartingTeam == SelectedTeam.Team2)
                {
                    StartingTeam = SelectedTeam.Team1;
                    SwitchTeam(SelectedTeam.Team1);
                }
                else
                {
                    StartingTeam = SelectedTeam.Team2;
                    SwitchTeam(SelectedTeam.Team2);
                }
            }
            IsRoundFinished = false;  // New round, so it's not finished
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

            // Find question and answer
            Question? q = (CurrentQuestion?.Id == questionId) ? CurrentQuestion : Questions.Find(x => x.Id == questionId);
            if (q == null) return;
            Answer? ans = q.Answers.Find(a => a.Id == answerId);
            if (ans == null) return;

            // GameMode.PlayOff mode means they are fighting to choose who goes first
            if (mode == GameMode.PlayOff)
            {
                // If team 1 does not have a first answer, select it here
                if (WhichTeamsTurn == SelectedTeam.Team1 && Team1.FirstAnswer == null)
                {
                    Team1.FirstAnswer = ans;

                    // Award points immediately to that team
                    Team1.Score += ans.Points;
                    RoundPointsTeam1 += ans.Points;

                    // Compare the answer with the answer with max points
                    int topPoints = q.Answers.Count > 0 ? q.Answers.Max(a => a.Points) : 0;
                    if (ans.Points == topPoints)
                    {
                        // if they got the top answer, team 1 remains in control and can choose to play or pass
                        PlayoffAwaitingChoice = true;
                    }
                    else
                    {
                        // switch to the other team to ask for their first answer
                        if (Team2.FirstAnswer == null) SwitchTeam();
                    }

                    NotifyStateChanged();
                }
                // otherwise same for team 2
                else if (WhichTeamsTurn == SelectedTeam.Team2 && Team2.FirstAnswer == null)
                {
                    Team2.FirstAnswer = ans;

                    Team2.Score += ans.Points;
                    RoundPointsTeam2 += ans.Points;

                    // Compare the answer with the answer with max points
                    int topPoints = q.Answers.Count > 0 ? q.Answers.Max(a => a.Points) : 0;
                    if (ans.Points == topPoints)
                    {
                        // if they got the top answer, team 2 remains in control and can choose to play or pass
                        PlayoffAwaitingChoice = true;
                    }
                    else
                    {
                        // switch to the other team to ask for their first answer
                        if (Team1.FirstAnswer == null) SwitchTeam();
                    }

                    NotifyStateChanged();
                }
                if (Team1.FirstAnswer != null && Team2.FirstAnswer != null)
                {
                    // Both teams have supplied a first answer, determine which is higher and set control team
                    if (Team1.FirstAnswer.Points > Team2.FirstAnswer.Points)
                    {
                        SwitchTeam(SelectedTeam.Team1);
                    }
                    else if (Team2.FirstAnswer.Points > Team1.FirstAnswer.Points)
                    {
                        SwitchTeam(SelectedTeam.Team2);
                    }
                    else
                    {
                        // In the case of a tie, randomly select control team
                        // This is fallback but shouldn't really happen
                        WhichTeamsTurn = ChooseRandomFirst();
                    }
                    PlayoffAwaitingChoice = true;
                    NotifyStateChanged();
                    return;

                }
            }

            else if (mode == GameMode.Normal)
            {
                // Normal play: award revealed answer points immediately to the active team (or SelectedFirst fallback)
                if (WhichTeamsTurn == SelectedTeam.Team1)
                {
                    Team1.Score += ans.Points;
                    RoundPointsTeam1 += ans.Points;
                }
                else if (WhichTeamsTurn == SelectedTeam.Team2)
                {
                    Team2.Score += ans.Points;
                    RoundPointsTeam2 += ans.Points;
                }

                // Check if all answers have been revealed (round is finished)
                CheckAndFinishRound(q);
            }
            else if (mode == GameMode.PointSteal)
            {

                // If we're in a steal attempt and the stealing team reveals an answer, they succeed and steal previously awarded round points
                if (WhichTeamsTurn != SelectedTeam.Random)
                {
                    // stealing team is PlayoffActiveTeam; owner is the other team
                    SelectedTeam stealer = WhichTeamsTurn;
                    SelectedTeam owner = stealer == SelectedTeam.Team1 ? SelectedTeam.Team2 : SelectedTeam.Team1;

                    int stolenAmount = owner == SelectedTeam.Team1 ? RoundPointsTeam1 : RoundPointsTeam2;

                    // remove stolen points from owner and give to stealer
                    if (owner == SelectedTeam.Team1)
                    {
                        Team1.Score = Math.Max(0, Team1.Score - stolenAmount);
                    }
                    else
                    {
                        Team2.Score = Math.Max(0, Team2.Score - stolenAmount);
                    }

                    // award stolen points and the points for this correct stealing answer to the stealer
                    if (stealer == SelectedTeam.Team1)
                    {
                        Team1.Score += stolenAmount + ans.Points;
                    }
                    else
                    {
                        Team2.Score += stolenAmount + ans.Points;
                    }

                    // round is over so reset round points
                    RoundPointsTeam1 = 0;
                    RoundPointsTeam2 = 0;

                    // end round: next starter is the opposite team
                    SwitchTeam();
                    ResetPlayoffState();
                    IsRoundFinished = true;
                    return;
                }
            }

            NotifyStateChanged();
            return;
        }

        // Host indicates the control team chose to keep the turn
        public void ChoosePlay()
        {
            if (!PlayoffAwaitingChoice) return;
            PlayoffAwaitingChoice = false;
            mode = GameMode.Normal;
            Team1.IncorrectStrikes = 0;
            Team2.IncorrectStrikes = 0;
            NotifyStateChanged();
        }

        // Host indicates the control team passed the turn to the other team
        public void ChoosePass()
        {
            if (!PlayoffAwaitingChoice) return;
            PlayoffAwaitingChoice = false;
            SwitchTeam();
            mode = GameMode.Normal;
            Team1.IncorrectStrikes = 0;
            Team2.IncorrectStrikes = 0;
            NotifyStateChanged();
        }

        // Host registers a strike for the current active team
        public void HostStrike()
        {
            if (mode == GameMode.PlayOff)
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
            if (mode == GameMode.PointSteal)
            {
                // stealing team failed: do not change scores (points were already awarded to owner on reveal)
                ResetPlayoffState();
                SwitchTeam();
                IsRoundFinished = true;
                return;
            }

            if (WhichTeamsTurn == SelectedTeam.Team1) Team1.IncorrectStrikes++;
            else if (WhichTeamsTurn == SelectedTeam.Team2) Team2.IncorrectStrikes++;

            int strikes = WhichTeamsTurn == SelectedTeam.Team1 ? Team1.IncorrectStrikes : Team2.IncorrectStrikes;
            if (strikes >= 3)
            {
                mode = GameMode.PointSteal;
                // other team becomes the stealing team
                SwitchTeam();
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

        private int SumRevealedPointsForCurrentQuestion()
        {
            if (CurrentQuestion == null) return 0;
            if (!_revealedAnswers.TryGetValue(CurrentQuestion.Id, out HashSet<int>? set) || set == null) return 0;
            int sum = 0;
            foreach (int id in set)
            {
                Answer? a = CurrentQuestion.Answers.Find(x => x.Id == id);
                if (a != null) sum += a.Points;
            }
            return sum;
        }

        public bool IsAnswerRevealed(int questionId, int answerId)
        {
            return _revealedAnswers.TryGetValue(questionId, out HashSet<int>? set) && set.Contains(answerId);
        }

        // Which team goes first: SelectedTeam.Team1, SelectedTeam.Team2, or SelectedTeam.Random (Random/not-selected)
        // Default to Random so no concrete selection is assumed until the host picks
        public SelectedTeam WhichTeamsTurn { get; private set; } = SelectedTeam.Random;

        // Playoff-specific state
        // When two teams each supply a "first answer" we determine which team has the higher answer
        // and set PlayoffAwaitingChoice = true so the control team can choose to play or pass.
        public bool PlayoffAwaitingChoice { get; private set; }

        // Track round-awarded points per team so they can be transferred if stolen
        public int RoundPointsTeam1 { get; private set; }
        public int RoundPointsTeam2 { get; private set; }

        // Whether the current round is finished (all answers revealed or strikes exhausted)
        public bool IsRoundFinished { get; set; } = false;

        public event Action? StateChanged;

        public void IncrementScore(SelectedTeam team, int delta = 1)
        {
            if (team == SelectedTeam.Team1) Team1.Score += delta;
            else if (team == SelectedTeam.Team2) Team2.Score += delta;
            NotifyStateChanged();
        }

        public void SwitchTeam()
        {
            if (WhichTeamsTurn == SelectedTeam.Team1) WhichTeamsTurn = SelectedTeam.Team2;
            else if (WhichTeamsTurn == SelectedTeam.Team2) WhichTeamsTurn = SelectedTeam.Team1;
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

        public SelectedTeam ChooseRandomFirst()
        {
            Random rand = new Random();
            SelectedTeam pick = rand.Next(0, 2) == 0 ? SelectedTeam.Team1 : SelectedTeam.Team2;
            SwitchTeam(pick);
            // set StartingTeam so we can alternate between teams
            StartingTeam = pick;
            return pick;
        }

        public void ResetScores()
        {
            Team1.Score = 0;
            Team2.Score = 0;
            NotifyStateChanged();
        }

        public void ResetPlayoffState()
        {
            Team1.FirstAnswer = null;
            Team2.FirstAnswer = null;
            Team1.IncorrectStrikes = 0;
            Team2.IncorrectStrikes = 0;
            PlayoffAwaitingChoice = false;
            RoundPointsTeam1 = 0;
            RoundPointsTeam2 = 0;
            mode = GameMode.PlayOff;
        }

        // Checks if all answers in the given question have been revealed.
        // If so, marks the round as finished (IsRoundFinished = true).
        private void CheckAndFinishRound(Question q)
        {
            if (q == null || !_revealedAnswers.TryGetValue(q.Id, out HashSet<int>? revealedSet))
                return;

            // Calculate total possible points from all answers
            int totalPossiblePoints = q.Answers.Sum(a => a.Points);

            // Calculate total revealed points
            int totalRevealedPoints = revealedSet.Sum(answerId =>
            {
                Answer? a = q.Answers.Find(x => x.Id == answerId);
                return a?.Points ?? 0;
            });

            // Round is finished when all points have been revealed
            if (totalRevealedPoints >= totalPossiblePoints)
            {
                IsRoundFinished = true;
            }
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

    public struct PlayerTurn
    {
        public SelectedTeam CurrentTeam { get; set; }
        public PlayerTurn(SelectedTeam team)
        {
            CurrentTeam = team;
        }
        public void Switch()
        {
            CurrentTeam = CurrentTeam == SelectedTeam.Team1 ? SelectedTeam.Team2 : SelectedTeam.Team1;
        }
    }

    public enum GameMode
    {
        PlayOff,
        Normal,
        PointSteal
    }
}

