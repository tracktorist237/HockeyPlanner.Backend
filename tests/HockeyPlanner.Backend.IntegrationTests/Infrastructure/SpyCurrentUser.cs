using HockeyPlanner.Backend.Application.Abstractions.Identity;

namespace HockeyPlanner.Backend.IntegrationTests.Infrastructure;

public sealed class SpyCurrentUser : ICurrentUser
{
    private readonly bool _isAuthenticated;
    private readonly Guid? _userId;
    private int _isAuthenticatedReadCount;
    private int _userIdReadCount;

    public SpyCurrentUser(bool isAuthenticated, Guid? userId)
    {
        _isAuthenticated = isAuthenticated;
        _userId = userId;
    }

    public bool IsAuthenticated
    {
        get
        {
            Interlocked.Increment(ref _isAuthenticatedReadCount);
            return _isAuthenticated;
        }
    }

    public Guid? UserId
    {
        get
        {
            Interlocked.Increment(ref _userIdReadCount);
            return _userId;
        }
    }

    public int IsAuthenticatedReadCount => Volatile.Read(ref _isAuthenticatedReadCount);

    public int UserIdReadCount => Volatile.Read(ref _userIdReadCount);
}
