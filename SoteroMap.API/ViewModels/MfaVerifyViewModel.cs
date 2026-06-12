using System.ComponentModel.DataAnnotations;

namespace SoteroMap.API.ViewModels;

public class MfaVerifyViewModel
{
    public string Username { get; set; } = string.Empty;
    public string? ReturnUrl { get; set; }

    [Required]
    [Display(Name = "Codigo MFA")]
    [StringLength(6, MinimumLength = 6)]
    public string Code { get; set; } = string.Empty;
}
