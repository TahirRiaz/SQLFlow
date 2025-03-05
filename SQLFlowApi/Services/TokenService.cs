using SQLFlowUi.Service;

namespace SQLFlowApi.Services
{
    using System;
    using System.Collections.Generic;
    using System.IdentityModel.Tokens.Jwt;
    using System.Security.Claims;
    using System.Text;
    using Microsoft.Extensions.Configuration;
    using Microsoft.IdentityModel.Tokens;

    public class TokenService
    {
        private readonly IConfiguration _configuration;
        private readonly ConfigService _configService;

        public TokenService(IConfiguration configuration, ConfigService configService)
        {
            _configuration = configuration;
            _configService = configService;
        }

        public string GenerateJwtToken(IEnumerable<Claim> claims)
        {
            
            var keyBytes = Encoding.UTF8.GetBytes(_configService.configSettings.SecretKey);
            var key = new SymmetricSecurityKey(keyBytes);
            var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var expiryDuration = Convert.ToDouble(_configService.configSettings.ExpireMinutes);
            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(claims),
                Expires = DateTime.UtcNow.AddMinutes(expiryDuration),
                SigningCredentials = credentials,
                Issuer = _configService.configSettings.Issuer,
                Audience = _configService.configSettings.Audience
            };

            var tokenHandler = new JwtSecurityTokenHandler();
            var token = tokenHandler.CreateToken(tokenDescriptor);
            return tokenHandler.WriteToken(token);
        }

        public string GenerateLongLivedJwtToken(IEnumerable<Claim> claims)
        {

            var keyBytes = Encoding.UTF8.GetBytes(_configService.configSettings.SecretKey);
            var key = new SymmetricSecurityKey(keyBytes);
            var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var expiryDuration = Convert.ToDouble(_configService.configSettings.ExpireMinutes);
            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(claims),
                Expires = DateTime.UtcNow.AddYears(_configService.configSettings.ExpireYears4LongLived),
                SigningCredentials = credentials,
                Issuer = _configService.configSettings.Issuer,
                Audience = _configService.configSettings.Audience
            };
            var tokenHandler = new JwtSecurityTokenHandler();
            var token = tokenHandler.CreateToken(tokenDescriptor);
            return tokenHandler.WriteToken(token);
        }


        public ClaimsPrincipal ValidateToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                throw new SecurityTokenException("Invalid token");

            var tokenHandler = new JwtSecurityTokenHandler();
            var validationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = _configService.configSettings.Issuer,
                ValidAudience = _configService.configSettings.Audience,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_configService.configSettings.SecretKey))
            };

            try
            {
                SecurityToken validatedToken;
                var principal = tokenHandler.ValidateToken(token, validationParameters, out validatedToken);
                return principal; // Valid token
            }
            catch (SecurityTokenExpiredException)
            {
                throw new SecurityTokenExpiredException("Token has expired");
            }
            catch (SecurityTokenException)
            {
                throw new SecurityTokenException("Invalid token");
            }
        }
    }

}
