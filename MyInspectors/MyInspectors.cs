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

        static MethodInfo OnChanges = AccessTools.Method(typeof(SlotInspector), "OnChanges");
        static FieldInfo slot_target = AccessTools.Field(typeof(SyncRef<Slot>), "_target");
        static FieldInfo user_target = AccessTools.Field(typeof(SyncRef<User>), "_target");
        static FieldInfo syncBag_target = AccessTools.Field(typeof(SyncRef<ISyncBag>), "_target");
        static FieldInfo synclist_target = AccessTools.Field(typeof(SyncRef<ISyncList>), "_target");
        static MethodInfo BagEditorAddNewPressed = AccessTools.Method(typeof(BagEditor), "AddNewPressed");
        static MethodInfo ListEditorAddNewPressed = AccessTools.Method(typeof(ListEditor), "AddNewPressed");
        static FieldInfo _targetContainer = AccessTools.Field(typeof(WorkerInspector), "_targetContainer");

        [HarmonyPatch(typeof(SceneInspector), "OnAttach")]
        class StupidInspectorFixupPatch
        {
            public static void Postfix(SceneInspector __instance)
            {
                // onchanges triggers 1 update later, when we need 0 updates so it doesn't sync
                __instance.RunInUpdates(0, () => {
                    Traverse.Create(__instance).Method("OnChanges").GetValue();
                });
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
                        yield return new CodeInstruction(OpCodes.Callvirt, typeof(SceneInspector_Patch).GetMethod("ShouldContinue"));
                        yield return new CodeInstruction(OpCodes.Or);
                    }
                    if (code.StoresField(AccessTools.Field(typeof(WorkerInspector), "_currentContainer")))
                    {
                        // reset the container so others don't know about it
                        yield return new CodeInstruction(OpCodes.Ldarg_0);
                        yield return new CodeInstruction(OpCodes.Call, typeof(SceneInspector_Patch).GetMethod("ResetContainer"));
                    }
                }
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

        [HarmonyPatch(typeof(SlotInspector), nameof(SlotInspector.Setup))]
        class SlotInspector_Patch
        {
            static bool Prefix(SlotInspector __instance, Slot target, SyncRef<Slot> selectionReference, int depth, RelayRef<SyncRef<Slot>> ____selectionReference, Sync<int> ____depth, SyncRef<Slot> ____rootSlot)
            {
                if (__instance.World.IsAuthority) return true;
                ____selectionReference.Target = selectionReference;
                ____depth.Value = depth;
                slot_target.SetValue(____rootSlot, target); //i think there is some proper way to set a local value but i forget it.

                OnChanges.Invoke(__instance, null);
                return false;
            }
        }

        [HarmonyPatch(typeof(UserInspectorItem), nameof(UserInspectorItem.Setup))]
        class UserInspectorItem_Patch
        {
            static bool Prefix(UserInspectorItem __instance, User user, SyncRef<User> ____user)
            {
                if (__instance.World.IsAuthority) return true;
                user_target.SetValue(____user, user);
                return false;
            }
        }
        //these 2 could have some extra stuff done so they work after being loaded from a non local save. its not really worth it imo but something worth noting
        //from looking into it some good options are FrooxEngine.MaterialRelay.MaterialRefs and comp slot bag 
        [HarmonyPatch(typeof(BagEditor), nameof(BagEditor.Setup))]
        class BagEditor_Patch
        {
            static bool Prefix(BagEditor __instance, ISyncBag target, Button button, SyncRef<ISyncBag> ____targetBag, SyncRef<Button> ____addNewButton)
            {
                if (__instance.World.IsAuthority) return true;
                ____addNewButton.Target = button;
                syncBag_target.SetValue(____targetBag, target);
                button.Pressed.Target = BagEditorAddNewPressed.CreateDelegate(typeof(ButtonEventHandler), __instance) as ButtonEventHandler;
                return false;
            }
        }

        [HarmonyPatch(typeof(ListEditor), nameof(ListEditor.Setup))]
        class ListEditor_Patch
        {
            static bool Prefix(ListEditor __instance, ISyncList target, Button button, SyncRef<ISyncList> ____targetList, SyncRef<Button> ____addNewButton)
            {
                if (__instance.World.IsAuthority) return true;
                ____addNewButton.Target = button;
                synclist_target.SetValue(____targetList, target);
                button.Pressed.Target = ListEditorAddNewPressed.CreateDelegate(typeof(ButtonEventHandler), __instance) as ButtonEventHandler;
                return false;
            }
        }
    }
}