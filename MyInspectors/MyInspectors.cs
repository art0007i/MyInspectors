using HarmonyLib;
using NeosModLoader;
using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Collections.Generic;
using FrooxEngine;
using FrooxEngine.LogiX;
using BaseX;
using System.Reflection.Emit;
using FrooxEngine.UIX;

namespace MyInspectors
{
    public class MyInspectors : NeosMod
    {
        public override string Name => "MyInspectors";
        public override string Author => "art0007i";
        public override string Version => "1.3.0";
        public override string Link => "https://github.com/art0007i/MyInspectors/";
        public override void OnEngineInit()
        {
            Harmony harmony = new Harmony("me.art0007i.MyInspectors");
            harmony.PatchAll();
        }
        static FieldInfo _targetContainer = AccessTools.Field(typeof(WorkerInspector), "_targetContainer");

        // patching 'hot' code. but like idk how else to do it
        [HarmonyPatch(typeof(ComponentBase<Component>), "OnAwake")]
        class StupidInspectorFixupPatch
        {
            public static void Postfix(object __instance)
            {
                if(__instance is SceneInspector i)
                {
                    // onchanges triggers 1 update later, when we need 0 updates so it doesn't sync
                    i.RunInUpdates(0, () => {
                        Traverse.Create(__instance).Method("OnChanges").GetValue();
                    });
                }
            }
        }

        [HarmonyPatch]
        class SceneInspector_Patch
        {
            public static IEnumerable<MethodInfo> TargetMethods()
            {
                yield return AccessTools.Method(typeof(WorkerInspector), "OnChanges");
                yield return AccessTools.Method(typeof(SceneInspector), "OnChanges");
            }
            public static bool IsChangedLocally(ConflictingSyncElement el)
            {
                return el.LastConfirmedTime == el.LastHostVersion || el.IsSyncDirty;
            }

            public static bool ShouldContinue(Worker worker)
            {
                if (worker is SceneInspector s)
                {
                    return IsChangedLocally(s.ComponentView) || IsChangedLocally(s.Root);
                }
                else if (worker is WorkerInspector w)
                {
                    // doing this is weird, because we won't unregister events whenever you set a workerinspector to null.
                    // but also this never happens by default so hope it's ok
                    var target = _targetContainer.GetValue(w) as SyncRef<Worker>;
                    return target.Target != null && IsChangedLocally(target);
                }
                return false;
            }

            public static void ResetContainer(WorkerInspector i)
            {
                if(!i.World.IsAuthority)
                    (_targetContainer.GetValue(i) as SyncRef<Worker>).Target = null;
            }
            public static bool IsHost(SceneInspector i)
            {
                return i.World.IsAuthority;
            }

            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                FieldInfo _currentContainer = AccessTools.Field(typeof(WorkerInspector), "_currentContainer");

                var codes = instructions.ToList();
                var rootfield = typeof(SceneInspector).GetField("Root");
                var compfield = typeof(SceneInspector).GetField("ComponentView");
                for (var i = 0; i < codes.Count; i++)
                {
                    var code = codes[i];
                    if (code.opcode == OpCodes.Beq)
                    {
                        var code2 = codes[i - 2];
                        // targets the "if currentRoot != targetRoot" statements in scene inspector
                        if (code2.operand is FieldInfo f && (f == rootfield || f == compfield))
                        {
                            // if (IsHost | IsChangedLocally) & !(original statement)
                            yield return new CodeInstruction(OpCodes.Ceq);
                            yield return new CodeInstruction(OpCodes.Ldc_I4_0);
                            yield return new CodeInstruction(OpCodes.Ceq);
                            yield return new CodeInstruction(OpCodes.Ldarg_0);
                            yield return new CodeInstruction(OpCodes.Call, typeof(SceneInspector_Patch).GetMethod(nameof(IsHost)));
                            yield return new CodeInstruction(OpCodes.Ldarg_0);
                            yield return new CodeInstruction(OpCodes.Ldfld, code2.operand);
                            yield return new CodeInstruction(OpCodes.Callvirt, typeof(SceneInspector_Patch).GetMethod(nameof(IsChangedLocally)));
                            yield return new CodeInstruction(OpCodes.Or);
                            yield return new CodeInstruction(OpCodes.And);
                            code.opcode = OpCodes.Brfalse;
                        }
                    }
                    yield return code;
                    if (code.Calls(typeof(World).GetMethod("get_IsAuthority")))
                    {
                        // is host OR the value has been changed by local user
                        yield return new CodeInstruction(OpCodes.Ldarg_0);
                        yield return new CodeInstruction(OpCodes.Callvirt, typeof(SceneInspector_Patch).GetMethod(nameof(ShouldContinue)));
                        yield return new CodeInstruction(OpCodes.Or);
                    }
                    if (code.StoresField(_currentContainer))
                    {
                        // reset the container so others don't know about it
                        yield return new CodeInstruction(OpCodes.Ldarg_0);
                        yield return new CodeInstruction(OpCodes.Call, typeof(SceneInspector_Patch).GetMethod(nameof(ResetContainer)));
                    }
                }
            }
        }

        [HarmonyPatch(typeof(SyncField<RefID>), "InternalSetValue")]
        class SyncField_Patch
        {
            static internal bool Prefix(SyncField<RefID> __instance, ref bool sync)
            {
                if (__instance == null || __instance.World.IsAuthority || !IsAlocatingUser(__instance)) return true;

                //doing this instead of a switch to support inherited classes
                var parent = __instance.Parent;
                var parentType = parent.GetType();
                if (typeof(SlotInspector).IsAssignableFrom(parentType))
                {
                    if(!(__instance is SyncRef<Slot>) || ((SlotInspector)parent).GetSyncMember("_rootSlot") != __instance)
                        return true;
                }
                else if (typeof(UserInspectorItem).IsAssignableFrom(parentType))
                {
                    if (!(__instance is SyncRef<User>) || ((UserInspectorItem)parent).GetSyncMember("_user") != __instance)
                        return true;
                }
                else if (typeof(BagEditor).IsAssignableFrom(parentType))
                {
                    if (!(__instance is SyncRef<ISyncBag>) || ((BagEditor)parent).GetSyncMember("_targetBag") != __instance)
                        return true;
                }
                else if (typeof(ListEditor).IsAssignableFrom(parentType))
                {
                    if (!(__instance is SyncRef<ISyncList>) || ((ListEditor)parent).GetSyncMember("_targetList") != __instance)
                        return true;
                }
                else return true;


                //todo: determine using a stack trace if we are being called by a deserialization function;
                sync = false;
                return true;
            }
        }
        
        static bool IsAlocatingUser(IWorldElement element) => element.ReferenceID.User == element.World.LocalUser.AllocationID;

        [HarmonyPatch]
        class Transpiler_AlocatingUser
        {
            static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(SlotInspector), "OnChanges");
                yield return AccessTools.Method(typeof(UserInspectorItem), "OnChanges");
                yield return AccessTools.Method(typeof(BagEditor), "OnChanges");
                yield return AccessTools.Method(typeof(ListEditor), "OnChanges");
            }
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes)
            {
                int hit = 0;
                foreach (var code in codes)
                {
                    if (hit <= 0 && (code.operand as MethodInfo)?.Name == "get_World")
                    {
                        hit++;
                        code.operand = AccessTools.Method(typeof(MyInspectors), nameof(IsAlocatingUser));
                        yield return code;
                    }
                    else if (hit == 1)
                    {
                        hit++;
                        yield return new(OpCodes.Nop);
                    }
                    else
                    {
                        yield return code;
                    }
                }
            }
        }
    }
}