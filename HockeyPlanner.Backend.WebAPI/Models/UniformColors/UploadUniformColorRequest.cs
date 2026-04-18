namespace HockeyPlanner.Backend.WebAPI.Models.UniformColors;

public class UploadUniformColorRequest
{
    public string Name { get; set; } = string.Empty;

    public IFormFile? File { get; set; }
}
