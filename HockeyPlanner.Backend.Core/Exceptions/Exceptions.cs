using System;
using System.Collections.Generic;
using System.Text;

namespace HockeyPlanner.Backend.Core.Exceptions
{
    public class NotFoundException : Exception
    {
        public NotFoundException(string message) : base(message) { }

        public NotFoundException(string name, object key)
            : base($"Сущность '{name}' ({key}) не найдена.") { }
    }

    public class UnauthorizedException : Exception
    {
        public UnauthorizedException(string message) : base(message) { }
    }

    // A principal can authenticate without resolving a usable canonical user ID.
    // Keep this distinct from resource authorization denied to a valid user.
    public class AuthenticationRequiredException(string message) : UnauthorizedException(message) { }

    public class BusinessRuleException : Exception
    {
        public BusinessRuleException(string message) : base(message) { }
    }

    public class ConflictException(string message) : Exception(message) { }
}
