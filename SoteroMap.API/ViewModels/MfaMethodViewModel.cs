using System.ComponentModel.DataAnnotations;

namespace SoteroMap.API.ViewModels;

public class MfaMethodViewModel
{
    public string Username { get; set; } = string.Empty;
    public string? ReturnUrl { get; set; }
    public bool CanResetInDevelopment { get; set; }

    [Required]
    public string Method { get; set; } = "authenticator";
}
