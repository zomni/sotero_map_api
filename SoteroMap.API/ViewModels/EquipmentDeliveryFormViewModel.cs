using System.ComponentModel.DataAnnotations;

namespace SoteroMap.API.ViewModels;

public class EquipmentDeliveryFormViewModel
{
    [Display(Name = "Fecha")]
    [Required(ErrorMessage = "Ingresa la fecha del formulario.")]
    public string DocumentDate { get; set; } = string.Empty;

    [Display(Name = "Institucion")]
    public string Institution { get; set; } = "HOSPITAL DR. SOTERO DEL RIO";

    [Display(Name = "Unidad o departamento")]
    [Required(ErrorMessage = "Ingresa la unidad o departamento.")]
    public string UnitOrDepartment { get; set; } = string.Empty;

    [Display(Name = "Numero de serie")]
    [Required(ErrorMessage = "Ingresa el numero de serie del equipo.")]
    public string SerialNumber { get; set; } = string.Empty;

    [Display(Name = "Usuario responsable")]
    [Required(ErrorMessage = "Ingresa el usuario responsable.")]
    public string ResponsibleUser { get; set; } = string.Empty;

    [Display(Name = "Correo electronico")]
    public string Email { get; set; } = string.Empty;

    [Display(Name = "IP")]
    public string IpAddress { get; set; } = string.Empty;

    [Display(Name = "Cargo funcionario")]
    public string JobTitle { get; set; } = string.Empty;

    [Display(Name = "Anexo")]
    public string Annex { get; set; } = string.Empty;

    [Display(Name = "Usuario Active Directory")]
    public string ActiveDirectoryUser { get; set; } = string.Empty;

    [Display(Name = "Tipo recepcion")]
    public string ReceptionType { get; set; } = "Nueva";

    [Display(Name = "Serie / inventario equipo reemplazado")]
    public string ReplacedEquipmentSerial { get; set; } = string.Empty;

    [Display(Name = "Marca y modelo equipo reemplazado")]
    public string ReplacedEquipmentModel { get; set; } = string.Empty;

    [Display(Name = "Correo activacion Office / proveedor")]
    public string OfficeActivationEmail { get; set; } = string.Empty;

    [Display(Name = "MDA - INSTA")]
    public string MdaTicket { get; set; } = string.Empty;

    [Display(Name = "MAC ADDR")]
    public string MacAddress { get; set; } = string.Empty;

    [Display(Name = "Marca")]
    public string ComputerBrand { get; set; } = string.Empty;

    [Display(Name = "Sistema operativo")]
    public string OperatingSystem { get; set; } = "WINDOWS 10";

    [Display(Name = "Modelo")]
    public string ComputerModel { get; set; } = string.Empty;

    [Display(Name = "Suite MS Office instalada")]
    public string OfficeSuite { get; set; } = "OFFICE 2021";

    [Display(Name = "Procesador")]
    public string Processor { get; set; } = string.Empty;

    [Display(Name = "Candado de seguridad")]
    public string SecurityLock { get; set; } = "Si";

    [Display(Name = "RAM")]
    public string Ram { get; set; } = string.Empty;

    [Display(Name = "Disco duro")]
    public string Disk { get; set; } = string.Empty;

    [Display(Name = "Lexmark")]
    public bool Lexmark { get; set; }

    [Display(Name = "Zebra")]
    public bool Zebra { get; set; }

    [Display(Name = "Huellero")]
    public bool FingerprintReader { get; set; }

    [Display(Name = "Otros")]
    public bool OtherDevices { get; set; }

    [Display(Name = "RCE Pulso")]
    public bool AppRcePulso { get; set; }
    public bool AppRce { get; set; }
    public bool AppAnatPatologica { get; set; }
    public bool AppHospitalizados { get; set; }
    public bool AppDauAdulto { get; set; }
    public bool AppDauMujer { get; set; }
    public bool AppDauInfantil { get; set; }
    public bool AppIq { get; set; }
    public bool AppInterconsultas { get; set; }
    public bool AppSga { get; set; }
    public bool AppSgde { get; set; }
    public bool AppRpecRni { get; set; }
    public bool AppHistorialClinico { get; set; }

    public bool AdminSgd { get; set; }
    public bool AdminSirh { get; set; }
    public bool AdminAbastecimiento1 { get; set; }
    public bool AdminAbastecimiento2 { get; set; }
    public bool AdminMsAccess { get; set; }
    public bool AdminTeams { get; set; }
    public bool AdminAbastSsmso { get; set; }
    public bool AdminToadModeler { get; set; }
    public bool AdminZoom { get; set; }
    public bool AdminBizagi { get; set; }
    public bool AdminMsProject { get; set; }
    public bool AdminPowerBi { get; set; }
    public bool AdminTableauReader { get; set; }

    [Display(Name = "Verificar nombre de equipo")]
    public string ValidationSerialName { get; set; } = string.Empty;

    [Display(Name = "Cambio descripcion del equipo")]
    public string ValidationDescriptionChange { get; set; } = string.Empty;

    [Display(Name = "Validacion Suite MS Office")]
    public string ValidationOfficeSuite { get; set; } = string.Empty;

    [Display(Name = "Cuenta AD")]
    public string ValidationAdAccount { get; set; } = string.Empty;

    [Display(Name = "Version instalada AV")]
    public string AntivirusInstalledVersion { get; set; } = string.Empty;

    [Display(Name = "Estado de conexion AV")]
    public string AntivirusConnectionState { get; set; } = "Activo";

    [Display(Name = "Nombre usuario firma")]
    public string SignedUserName { get; set; } = string.Empty;

    [Display(Name = "RUT usuario")]
    public string SignedUserRut { get; set; } = string.Empty;

    [Display(Name = "Nombre tecnico")]
    public string TechnicianName { get; set; } = string.Empty;
}
