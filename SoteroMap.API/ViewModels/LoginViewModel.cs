using System.ComponentModel.DataAnnotations;

namespace SoteroMap.API.ViewModels;

public class LoginViewModel
{
    [Required]
    [Display(Name = "Usuario")]
    public string Username { get; set; } = string.Empty;

    [Required]
    [DataType(DataType.Password)]
    [Display(Name = "Contrasena")]
    public string Password { get; set; } = string.Empty;

    [Display(Name = "Mantener sesion iniciada")]
    public bool RememberMe { get; set; }

    public string? ReturnUrl { get; set; }
}
