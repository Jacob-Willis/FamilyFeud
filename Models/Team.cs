using FamilyFeud.Services.Hasura;

namespace FamilyFeud.Models
{
    // Simple Team model to hold name, score and colour
    public class Team
    {
        public string Name { get; set; }
        public int Score { get; set; }
        public string Colour { get; set; }
        public Answer? FirstAnswer { get; set; }
        public int IncorrectStrikes { get; set; } = 0;

        public Team(string name, string colour)
        {
            Name = name;
            Colour = colour;
            Score = 0;
        }
    }
}
