using BepInEx.Preloader.Core.Patching;
using Mono.Cecil;

namespace MyInspectors;

[PatcherPluginInfo(GUID, Name, Version)]
public class Patcher : BasePatcher
{
    public const string GUID = "art0007i.MyInspectors";
    public const string Name = "MyInspectors";
    public const string Version = "2.1.1";

    [TargetType("FrooxEngine.dll", "FrooxEngine.Sync`1")]
    public void PatchAssembly(TypeDefinition type) => type.IsSealed = false;
}