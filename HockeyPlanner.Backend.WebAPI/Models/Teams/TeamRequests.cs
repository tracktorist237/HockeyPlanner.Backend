namespace HockeyPlanner.Backend.WebAPI.Models.Teams
{
    public class JoinTeamRequest
    {
        public int? TeamJerseyNumber { get; set; }
    }

    public class UploadTeamImageResponse
    {
        public string ImageUrl { get; set; } = string.Empty;
    }
}
