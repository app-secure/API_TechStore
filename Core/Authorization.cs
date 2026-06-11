using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Security.Claims;
using System.Threading.Tasks;
using TechStore360.Modules.Usuarios;

namespace TechStore360.Core
{
    public class AdminRequirement : IAuthorizationRequirement
    {
    }

    public class AdminRequirementHandler : AuthorizationHandler<AdminRequirement>
    {
        private readonly IHttpContextAccessor _httpContextAccessor;

        public AdminRequirementHandler(IHttpContextAccessor httpContextAccessor)
        {
            _httpContextAccessor = httpContextAccessor;
        }

        protected override async Task HandleRequirementAsync(
            AuthorizationHandlerContext context,
            AdminRequirement requirement)
        {
            var userId = context.User.FindFirst("user_id")?.Value 
                ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            if (userId != null)
            {
                var httpContext = _httpContextAccessor.HttpContext;
                if (httpContext != null)
                {
                    var repo = httpContext.RequestServices.GetService<IUsuariosRepository>();
                    if (repo != null)
                    {
                        var user = await repo.GetByIdAsync(userId);
                        if (user != null && string.Equals(user.Rol, "ADMIN", StringComparison.OrdinalIgnoreCase))
                        {
                            context.Succeed(requirement);
                        }
                    }
                }
            }
        }
    }
}
