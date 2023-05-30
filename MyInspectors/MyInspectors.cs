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
        public override string Version => "1.2.0";
        public override string Link => "https://github.com/art0007i/MyInspectors/";
        public override void OnEngineInit()
        {
            Harmony harmony = new Harmony("me.art0007i.MyInspectors");
            harmony.PatchAll();
        }

        static FieldInfo _currentRoot = AccessTools.Field(typeof(SceneInspector), "_currentRoot");
        static FieldInfo _componentsContentRoot = AccessTools.Field(typeof(SceneInspector), "_componentsContentRoot");
        static FieldInfo _currentComponent = AccessTools.Field(typeof(SceneInspector), "_currentComponent");
        static FieldInfo _componentText = AccessTools.Field(typeof(SceneInspector), "_componentText");
        static FieldInfo _hierarchyContentRoot = AccessTools.Field(typeof(SceneInspector), "_hierarchyContentRoot");
        static FieldInfo _rootText = AccessTools.Field(typeof(SceneInspector), "_rootText");
        static MethodInfo OnChanges = AccessTools.Method(typeof(SlotInspector), "OnChanges");
        static FieldInfo slot_target = AccessTools.Field(typeof(SyncRef<Slot>), "_target");
        static FieldInfo user_target = AccessTools.Field(typeof(SyncRef<User>), "_target");
        static FieldInfo syncBag_target = AccessTools.Field(typeof(SyncRef<ISyncBag>), "_target");
        static FieldInfo synclist_target = AccessTools.Field(typeof(SyncRef<ISyncList>), "_target");
        static MethodInfo BagEditorAddNewPressed = AccessTools.Method(typeof(BagEditor), "AddNewPressed");
        static MethodInfo ListEditorAddNewPressed = AccessTools.Method(typeof(ListEditor), "AddNewPressed");

        [HarmonyPatch(typeof(SceneInspector))]
        class SceneInspector_Patch
        {
            [HarmonyPatch("OnAttach")]
            [HarmonyPostfix]
            public static void Prefix(SceneInspector __instance)
            {
                // run in updates 0 to wait until the target has been updated
                __instance.RunInUpdates(0, () => HookMethod(__instance, true));
            }

            [HarmonyPatch("OnChanges")]
            [HarmonyPrefix]
            public static void ChangesPatch(SceneInspector __instance)
            {
                HookMethod(__instance);
            }

            public static void HookMethod(SceneInspector inspector, bool force = false)
            {
                var curCom = _currentComponent.GetValue(inspector) as SyncRef<Slot>;
                // "IsSyncDirty" basically means that it's state has been modified but hasn't been sent over the network yet
                if (inspector.ComponentView.IsSyncDirty || force)
                {
                    // This is basically the default "SceneInspector.OnChanges" function.
                    if (curCom.Target != inspector.ComponentView.Target)
                    {
                        if (curCom.Target != null)
                        {
                            curCom.Target.RemoveGizmo(null);
                        }
                        if (inspector.ComponentView.Target != null && !inspector.ComponentView.Target.IsRootSlot)
                        {
                            inspector.ComponentView.Target.GetGizmo(null);
                        }
                        var comConRoot = _componentsContentRoot.GetValue(inspector) as SyncRef<Slot>;
                        comConRoot.Target.DestroyChildren(false, true, false, null);
                        curCom.Target = inspector.ComponentView.Target;
                        var comText = _componentText.GetValue(inspector) as SyncRef<Sync<string>>;
                        SyncField<string> target4 = comText.Target;
                        string str2 = "Slot: ";
                        Slot target5 = curCom.Target;
                        target4.Value = str2 + (((target5 != null) ? target5.Name : null) ?? "<i>null</i>");
                        if (curCom.Target != null)
                        {
                            comConRoot.Target.AddSlot("ComponentRoot", true).AttachComponent<WorkerInspector>().SetupContainer(curCom.Target);
                        }
                    }
                }
                var curRoot = _currentRoot.GetValue(inspector) as SyncRef<Slot>;
                var hiCurRoot = _hierarchyContentRoot.GetValue(inspector) as SyncRef<Slot>;
                if (curRoot.Target != inspector.Root.Target && inspector.Root.IsSyncDirty)
                {
                    curRoot.Target = inspector.Root.Target;
                    hiCurRoot.Target.DestroyChildren();
                    (_rootText.GetValue(inspector) as SyncRef<Sync<string>>).Target.Value = "Root: " + (curRoot.Target?.Name ?? "<i>null</i>");
                    if (curRoot.Target != null)
                    {
                        hiCurRoot.Target.AddSlot("HierarchyRoot").AttachComponent<SlotInspector>().Setup(curRoot.Target, curCom);
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
        /*
        [HarmonyPatch(typeof(WorkerInspector), "BuildUIForComponent")]
        class ComponentOrderPatch
        {
            // This patch might not be needed anymore but i'll keep it for safety?
            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes)
            {
                var injectCodes = new CodeInstruction[] {
                    new(OpCodes.Ldloc_0),
                    new(OpCodes.Callvirt, typeof(UIBuilder).GetMethod("get_Root")),
                    new(OpCodes.Call, typeof(ComponentOrderPatch).GetMethod("ReOrder"))
                };

                var lst = codes.ToList();

                lst.InsertRange(lst.FindIndex((instruction) =>
                    instruction.Is(OpCodes.Callvirt, typeof(UIBuilder).GetMethod("VerticalLayout", new Type[] { typeof(float), typeof(float), typeof(Alignment?) }))) + 2,
                    injectCodes
                );

                return lst.AsEnumerable();
            }

            public static void ReOrder(Slot slot)
            {
                if (!slot.World.IsAuthority)
                {
                    slot.OrderOffset = -1;
                }
            }
        }
        */


        // This part is for making newly added/removed components processed by local user instead of host
        [HarmonyPatch(typeof(WorkerInspector))]
        class WorkerInspector_Patch
        {
            [HarmonyPatch("OnChanges")]
            [HarmonyPrefix]
            public static bool Prefix(SyncRef<Worker> ____targetContainer)
            {
                WorkerHook(____targetContainer);

                // cancel normal method, its functionality is fully replaced by WorkerHook.
                return false;
            }

            [HarmonyPatch("SetupContainer")]
            [HarmonyPostfix]
            public static void Postfix(SyncRef<Worker> ____targetContainer)
            {
                WorkerHook(____targetContainer, true);
            }

            public static void WorkerHook(SyncRef<Worker> w, bool force = false)
            {
                WorkerInspector __instance = w.Parent as WorkerInspector;
                var curConField = AccessTools.Field(typeof(WorkerInspector), "_currentContainer");
                var tarCon = AccessTools.Field(typeof(WorkerInspector), "_targetContainer").GetValue(__instance) as SyncRef<Worker>;
                if (tarCon.Target != null && tarCon.IsSyncDirty || force)
                {
                    if (curConField.GetValue(__instance) != tarCon.Target)
                    {
                        AccessTools.Method(__instance.GetType(), "UnregisterEvents").Invoke(__instance, null);
                        Slot slot = tarCon.Target as Slot;
                        User user = tarCon.Target as User;
                        var comAdd = AccessTools.Method(__instance.GetType(), "OnComponentAdded").CreateDelegate(typeof(ComponentEvent<Component>), __instance) as ComponentEvent<Component>;
                        var comRem = AccessTools.Method(__instance.GetType(), "OnComponentRemoved").CreateDelegate(typeof(ComponentEvent<Component>), __instance) as ComponentEvent<Component>;
                        var usrComAdd = AccessTools.Method(__instance.GetType(), "UserComponentAdded").CreateDelegate(typeof(ComponentEvent<UserComponent>), __instance) as ComponentEvent<UserComponent>;
                        var usrComRem = AccessTools.Method(__instance.GetType(), "UserComponentRemoved").CreateDelegate(typeof(ComponentEvent<UserComponent>), __instance) as ComponentEvent<UserComponent>;
                        var streamAdd = AccessTools.Method(__instance.GetType(), "StreamAdded").CreateDelegate(typeof(Action<Stream>), __instance) as Action<Stream>;
                        var streamRem = AccessTools.Method(__instance.GetType(), "StreamRemoved").CreateDelegate(typeof(Action<Stream>), __instance) as Action<Stream>;

                        if (slot != null)
                        {
                            slot.ComponentAdded += comAdd;
                            slot.ComponentRemoved += comRem;
                        }
                        if (user != null)
                        {
                            user.ComponentAdded += usrComAdd;
                            user.ComponentRemoved += usrComRem;
                            user.StreamAdded += streamAdd;
                            user.StreamRemoved += streamRem;
                        }
                        curConField.SetValue(__instance, tarCon.Target);
                    }
                    tarCon.Target = null;
                }
            }
        }

        /*
         * I was trying to do some really weird stuff with registering change events instead of using the normal "OnChanges" method.
         * 
        [HarmonyPatch(typeof(SceneInspector))]
        [HarmonyPatch("OnStart")]
        class SceneInspector_OnStart_Patch
        {
            public static void Prefix(SceneInspector __instance)
            {
                Msg("hooking field events");
                __instance.ComponentView.Changed += ComponentView_Changed;
                __instance.ComponentView.OnReferenceChange += ComponentView_OnReferenceChange;
                __instance.ComponentView.OnObjectAvailable += ComponentView_OnObjectAvailable;
                __instance.ComponentView.OnTargetChange += ComponentView_OnTargetChange;
                __instance.ComponentView.OnValueChange += ComponentView_OnValueChange;
            }

            private static void ComponentView_Changed(IChangeable obj)
            {
                var reference = (SyncRef<Slot>)obj;
                Msg("chngable changed " + (reference.LastModifyingUser != null ? reference.LastModifyingUser.UserName : null));
            }

            private static void ComponentView_OnReferenceChange(SyncRef<Slot> reference)
            {
                Msg("ref chng " + (reference.LastModifyingUser != null ? reference.LastModifyingUser.UserName : null));

            }
            private static void ComponentView_OnObjectAvailable(SyncRef<Slot> reference)
            {
                Msg("obj here " + (reference.LastModifyingUser != null ? reference.LastModifyingUser.UserName : null));
            }
            private static void ComponentView_OnTargetChange(SyncRef<Slot> reference)
            {
                Msg("trgt chng " + (reference.LastModifyingUser != null ? reference.LastModifyingUser.UserName : null));
            }
            private static void ComponentView_OnValueChange(SyncField<RefID> syncField)
            {
                Msg("val chng " + (syncField.LastModifyingUser != null ? syncField.LastModifyingUser.UserName : null));
            }
        }
        */
    }
}