using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace AgriStream.Models;

public class CreateFarmViewModel
{
    [Required, StringLength(150)] public string Name { get; set; } = "";
    [Required, StringLength(250)] public string Location { get; set; } = "";
    [Required, Display(Name = "Farmer")] public string FarmerId { get; set; } = "";
    [Display(Name = "Agronomist (optional)")] public string? AgronomistId { get; set; }
    [ValidateNever] public List<SelectListItem> Farmers { get; set; } = [];
    [ValidateNever] public List<SelectListItem> Agronomists { get; set; } = [];
}

public class RecommendationViewModel
{
    [Range(1, int.MaxValue), Display(Name = "Farm")] public int FarmId { get; set; }
    [Required, StringLength(4000)] public string Advice { get; set; } = "";
    [ValidateNever] public List<SelectListItem> Farms { get; set; } = [];
}
