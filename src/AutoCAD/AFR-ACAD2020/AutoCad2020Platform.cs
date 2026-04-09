using AFR.Abstractions;

namespace AFR;

internal sealed class AutoCad2020Platform : ICadPlatform
{
    public string BrandName => "AutoCAD";
    public string VersionName => "2020";
    public string AppName => "AFR-ACAD2020";
    public string DisplayName => "AutoCAD 2020";
    public string RegistryBasePath => @"Software\Autodesk\AutoCAD\R23.1";
    public string RegistryKeyPattern => @"^ACAD-[A-Za-z0-9]+:[A-Za-z0-9]+$";
    public string AcDbDllName => "acdb23.dll";
    public string LdFileExport => "?ldfile@@YAHPEB_WHPEAVAcDbDatabase@@PEAVAcFontDescription@@@Z";
    public int PrologueSize => 21;
    public bool SupportsLdFileHook => true;
}
