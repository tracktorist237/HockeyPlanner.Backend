"""HP-83 controller boundary guards, independent of whitespace/layout."""
import pathlib
import re
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[2]


def code(path):
    source = (ROOT / path).read_text(encoding="utf-8-sig")
    return re.sub(r"//[^\n]*|/\*.*?\*/", "", source, flags=re.S)


class TeamTablesBoundaryTests(unittest.TestCase):
    def test_controller_cannot_query_persist_or_decide_roles(self):
        source = code("HockeyPlanner.Backend.WebAPI/Controllers/TeamTablesController.cs")
        forbidden = r"\b(?:AppDbContext|TeamMemberRole|currentUserId|CanSeeTeamAsync|CanManageTeamAsync|SyncTableRowsAsync|RecalculateTableAsync|SaveChanges\w*|AsNoTracking|Include|ThenInclude|Where|Select|FirstOrDefaultAsync|ToListAsync|BeginTransactionAsync)\b"
        self.assertIsNone(re.search(forbidden, source))
        self.assertNotIn("Microsoft.EntityFrameworkCore", source)
        self.assertNotIn("Core.Entities", source)

    def test_all_eight_actions_require_jwt_use_case_and_cancellation(self):
        source = code("HockeyPlanner.Backend.WebAPI/Controllers/TeamTablesController.cs")
        self.assertRegex(source, r"\[Authorize\]")
        self.assertNotIn("AllowAnonymous", source)
        actions = re.findall(r"public\s+async\s+Task<.*?>\s+(\w+)\s*\((.*?)\)\s*=>", source, re.S)
        self.assertEqual(8, len(actions))
        for name, signature in actions:
            self.assertIn("CancellationToken", signature)
            self.assertRegex(source, rf"service\.{name}\([^;]*ActorUserId[^;]*cancellationToken\)")
        self.assertIn("ICurrentUser", source)
        self.assertIn("currentUser.UserId", source)

    def test_application_uses_existing_policy_time_and_di(self):
        source = code("HockeyPlanner.Backend.Application/Implementations/Services/TeamTablesService.cs")
        self.assertIn("CoreTeamRolePolicy.CanManage", source)
        self.assertNotRegex(source, r"TeamMemberRole\.(?:Owner|Admin)")
        for forbidden in ("WebAPI", "HttpContext", "CancellationToken.None", "DateTime.UtcNow", "Task.Run"):
            self.assertNotIn(forbidden, source)
        self.assertIn("TimeProvider", source)
        self.assertIn("GetUtcNow().UtcDateTime", source)
        self.assertIn("AddScoped<ITeamTablesService, TeamTablesService>", code("HockeyPlanner.Backend.Application/DependencyInjection.cs"))


if __name__ == "__main__":
    unittest.main()
