namespace HockeyPlanner.Backend.WebAPI.Models.Spbhl
{
    public class SpbhlTeamSyncResult
    {
        public Guid TeamId { get; set; }
        public Guid SpbhlTeamId { get; set; }
        public int ReceivedCount { get; set; }
        public int CreatedCount { get; set; }
        public int UpdatedCount { get; set; }
        public int UnchangedCount { get; set; }
        public DateTime SyncedAt { get; set; }
    }
}
