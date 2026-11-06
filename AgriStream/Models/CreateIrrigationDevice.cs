using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.AspNetCore.Mvc.Rendering;
namespace AgriStream.Models;
public sealed class CreateIrrigationDevice
{
    [Required,RegularExpression(@"[A-Za-z0-9][A-Za-z0-9_-]{0,63}")]public string DeviceId {get;set;}="";
    [Required,StringLength(100)]public string Name {get;set;}="";
    [Range(1,int.MaxValue)]public int FarmId {get;set;}
    [Required,RegularExpression(@"[A-Za-z0-9][A-Za-z0-9_-]{0,63}")]public string FieldId {get;set;}="";
    [ValidateNever]public List<SelectListItem> Farms {get;set;}=[];
}
