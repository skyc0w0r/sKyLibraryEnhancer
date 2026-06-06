using System;
using System.Linq;
using System.Security.Claims;

namespace SkyLibraryEnhancer.Classes
{
    internal static class Extentions
    {
        public const string UserId = "Jellyfin-UserId";

        /// <summary>
        /// Get user id from claims.
        /// </summary>
        /// <param name="user">Current claims principal.</param>
        /// <returns>User id.</returns>
        public static Guid GetUserId(this ClaimsPrincipal user)
        {
            var value = GetClaimValue(user, UserId);
            return string.IsNullOrEmpty(value)
                ? default
                : Guid.Parse(value);
        }

        private static string? GetClaimValue(in ClaimsPrincipal user, string name)
            => user.Claims.FirstOrDefault(claim => claim.Type.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;
    }
}
