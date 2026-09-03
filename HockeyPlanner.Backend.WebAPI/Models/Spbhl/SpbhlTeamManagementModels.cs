namespace HockeyPlanner.Backend.WebAPI.Models.Spbhl
{
    public class SpbhlTeamLinkStatusDto
    {
        public Guid TeamId { get; set; }
        public bool IsLinked { get; set; }
        public Guid? SpbhlTeamId { get; set; }
        public string? SpbhlTeamName { get; set; }
        public string? ProfileUrl { get; set; }
        public DateTime? LastSyncAttemptAt { get; set; }
        public DateTime? LastSuccessfulSyncAt { get; set; }
    }

    public class BindSpbhlTeamRequest
    {
        public Guid SpbhlTeamId { get; set; }
        public string SpbhlTeamName { get; set; } = string.Empty;
    }

    public class SpbhlTeamBindResult
    {
        public SpbhlTeamLinkStatusDto Link { get; set; } = null!;
        public bool InitialSyncSucceeded { get; set; }
        public SpbhlTeamSyncResult? Sync { get; set; }
        public string? SyncError { get; set; }
    }
}
