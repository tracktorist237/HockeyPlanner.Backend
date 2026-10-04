"""Deterministic HP-82 boundary guards; HTTP/PostgreSQL tests prove behavior."""
import pathlib
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[2]


class TeamNewsBoundaryTests(unittest.TestCase):
    def test_controller_has_no_persistence_role_storage_or_notification_orchestration(self):
        source = (ROOT / "HockeyPlanner.Backend.WebAPI/Controllers/TeamsController.cs").read_text(encoding="utf-8-sig")
        for forbidden in ("AppDbContext", "_context", "SaveChanges", "TeamMemberRole", "CanManageTeamAsync", "INotificationService", "IFileStorageService", "FileStorageFolders", "ValidateImageFile", "BeginTransaction"):
            self.assertNotIn(forbidden, source)
        for action in ("GetTeamNews", "GetNewsFeed", "CreateTeamNews", "UpdateTeamNews", "DeleteTeamNews", "UploadTeamAvatar", "UploadTeamCover", "UploadTeamNewsImage"):
            self.assertIn(action, source)

    def test_application_does_not_depend_on_webapi(self):
        project = (ROOT / "HockeyPlanner.Backend.Application/HockeyPlanner.Backend.Application.csproj").read_text(encoding="utf-8-sig")
        self.assertNotIn("WebAPI", project)
        for name in ("TeamNewsService", "TeamMediaService"):
            source = (ROOT / f"HockeyPlanner.Backend.Application/Implementations/Services/{name}.cs").read_text(encoding="utf-8-sig")
            for forbidden in ("WebAPI", "IWebPushService", "Task.Run", "CancellationToken.None", "IFileStorageService"):
                self.assertNotIn(forbidden, source)

    def test_upload_orchestrator_does_not_decide_roles_or_access_database(self):
        source = (ROOT / "HockeyPlanner.Backend.WebAPI/Services/TeamMediaUploadService.cs").read_text(encoding="utf-8-sig")
        for forbidden in ("AppDbContext", "SaveChanges", "TeamMemberRole", "BeginTransaction", "DeleteAsync", "CancellationToken.None"):
            self.assertNotIn(forbidden, source)


if __name__ == "__main__":
    unittest.main()
