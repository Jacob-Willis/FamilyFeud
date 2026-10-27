using FamilyFeud.Services.Hasura;

namespace FamilyFeud.Models
{
    // Simple Team model to hold name, score and face-off/strike state
    public class Team
    {
        public string Name { get; set; }
        public int Score { get; set; }
        public Answer? FirstAnswer { get; set; }
        public int IncorrectStrikes { get; set; } = 0;

        public Team(string name)
        {
            Name = name;
            Score = 0;
        }
    }
}
