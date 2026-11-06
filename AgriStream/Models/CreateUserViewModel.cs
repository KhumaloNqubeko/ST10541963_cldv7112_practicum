using System.ComponentModel.DataAnnotations;
namespace AgriStream.Models;
public class CreateUserViewModel {
    [Required, EmailAddress] public string Username { get; set; } = "";
    [Required, MinLength(12), DataType(DataType.Password)] public string Password { get; set; } = "";
    [Required] public string Role { get; set; } = "Farmer";
}
