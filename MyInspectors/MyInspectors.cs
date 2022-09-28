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
        [HarmonyPatch(typeof(SceneInspector))]
        class SceneInspector_Patch
        {
            [HarmonyPatch("OnAttach")]
            [HarmonyPostfix]
            public static void Prefix(SceneInspector __instance)
            {
                // run in updates 0 to wait until the target has been updated
                __instance.RunInUpdates(0, () => HookMethod(__instance.ComponentView, true));
            }

            [HarmonyPatch("OnChanges")]
            [HarmonyPrefix]
            public static void ChangesPatch(SceneInspector __instance) {
                HookMethod(__instance.ComponentView);
            }

            public static void HookMethod(SyncRef<Slot> compView, bool force = false)
            {
                var based = compView.Parent as SceneInspector;
                var curCom = AccessTools.Field(typeof(SceneInspector), "_currentComponent").GetValue(based) as SyncRef<Slot>;
                // "IsSyncDirty" basically means that it's state has been modified but hasn't been sent over the network yet
                if (based.ComponentView.IsSyncDirty || force)
                {
                    // This is basically the default "SceneInspector.OnChanges" function.
                    if (curCom.Target != based.ComponentView.Target)
                    {
                        if (curCom.Target != null)
                        {
                            curCom.Target.RemoveGizmo(null);
                        }
                        if (based.ComponentView.Target != null && !based.ComponentView.Target.IsRootSlot)
                        {
                            based.ComponentView.Target.GetGizmo(null);
                        }
                        var comConRoot = AccessTools.Field(typeof(SceneInspector), "_componentsContentRoot").GetValue(based) as SyncRef<Slot>;
                        comConRoot.Target.DestroyChildren(false, true, false, null);
                        curCom.Target = based.ComponentView.Target;
                        var comText = AccessTools.Field(typeof(SceneInspector), "_componentText").GetValue(based) as SyncRef<Sync<string>>;
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