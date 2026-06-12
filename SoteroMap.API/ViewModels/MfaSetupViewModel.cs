using System.ComponentModel.DataAnnotations;

namespace SoteroMap.API.ViewModels;

public class MfaSetupViewModel
{
    public string Username { get; set; } = string.Empty;
    public string Issuer { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;
    public string SecretDisplay { get; set; } = string.Empty;
    public string QrSvg { get; set; } = string.Empty;
    public string SetupKey { get; set; } = string.Empty;
    public string? ReturnUrl { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset ServerTimeUtc { get; set; } = DateTimeOffset.UtcNow;
    public string? DevelopmentExpectedCode { get; set; }

    [Required]
    [Display(Name = "Codigo MFA")]
    [StringLength(6, MinimumLength = 6)]
    public string Code { get; set; } = string.Empty;
}
